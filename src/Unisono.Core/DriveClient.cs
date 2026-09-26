using System.Security.Cryptography;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Download;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Google.Apis.Upload;
using Google.Apis.Util.Store;
using File = Google.Apis.Drive.v3.Data.File;

namespace Unisono;

/// <summary>
/// Thin wrapper over the Drive v3 API: sign-in, path resolution, listing, changes, transfer.
/// Google-native documents (Docs, Sheets, ...) have no bytes to download; they can be exported instead.
/// </summary>
public sealed class DriveClient
{
    public const string FolderMime = "application/vnd.google-apps.folder";
    public const string FileFields = "id, name, mimeType, size, modifiedTime, md5Checksum, parents, trashed, version";
    private const string OctetStream = "application/octet-stream";

    private readonly DriveService _service;

    private DriveClient(DriveService service) => _service = service;

    /// <summary>
    /// Connects as one Google account (see <see cref="GoogleAccounts"/>). Opens the browser for consent the first time;
    /// afterwards the account's refresh token is used silently. The consent redirect lands on a loopback HttpListener,
    /// which works for a standard user. <paramref name="chooseAccount"/> makes Google show its account picker, which
    /// is what adding a second account needs (otherwise the browser's current Google session is reused silently).
    /// </summary>
    public static async Task<DriveClient> ConnectAsync(string accountId, CancellationToken ct, bool quiet = true, bool chooseAccount = false)
    {
        if (!System.IO.File.Exists(AppPaths.ClientSecretPath))
            throw new InvalidOperationException(
                $"No OAuth client found at {AppPaths.ClientSecretPath}. See README.md for how to create one.");

        var client = OAuthClientInfo.Read(AppPaths.ClientSecretPath);
        // Say which client we are about to use, so a stale or copied client_secret.json is obvious
        // before the consent screen for the wrong project opens in the browser.
        if (!quiet)
        {
            Console.WriteLine($"OAuth client {client.ClientId}");
            Console.WriteLine($"  project: {client.ProjectId ?? "(unknown)"}   type: {client.Kind}   file: {AppPaths.ClientSecretPath}");
        }
        if (client.Kind != "installed")
            Console.Error.WriteLine("warning: expected a 'Desktop app' client (\"installed\" in the JSON); other client types may not accept the loopback redirect.");

        ClientSecrets secrets;
        await using (var stream = System.IO.File.OpenRead(AppPaths.ClientSecretPath))
            secrets = (await GoogleClientSecrets.FromStreamAsync(stream, ct)).Secrets;

        // Tokens are issued per client id, so keep them per client: swapping client_secret.json
        // then simply triggers a fresh sign-in instead of invalid_grant on a token from the old client.
        // One credential per account and process: each credential refreshes its own access token and writes the
        // token file, so separate ones for the same account (the window and the sync host) would race on it.
        var key = TokenFile(client, accountId);
        Task<UserCredential> Authorize()
        {
            var flow = new GoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer
            {
                ClientSecrets = secrets,
                Scopes = new[] { DriveService.Scope.Drive },
                DataStore = new TokenStore(TokenDir(client)),
                Prompt = chooseAccount ? "select_account consent" : null,
            });
            return new AuthorizationCodeInstalledApp(flow, new LocalServerCodeReceiver()).AuthorizeAsync(accountId, ct);
        }
        UserCredential credential;
        if (chooseAccount)
            credential = await Authorize();
        else
        {
            var pending = Credentials.GetOrAdd(key, _ => Authorize());
            try { credential = await pending; }
            catch
            {
                Credentials.TryRemove(new KeyValuePair<string, Task<UserCredential>>(key, pending));
                throw;
            }
        }
        Credentials[key] = Task.FromResult(credential);

        var service = new DriveService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "Unisono",
        });
        return new DriveClient(service);
    }

    /// <summary>The default account (the first one added); for callers that do not care which.</summary>
    public static Task<DriveClient> ConnectAsync(CancellationToken ct, bool quiet = false) =>
        ConnectAsync(GoogleAccounts.Resolve(null)?.Id ?? GoogleAccounts.LegacyId, ct, quiet);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task<UserCredential>> Credentials =
        new(StringComparer.OrdinalIgnoreCase);

    private static string TokenDir(OAuthClientInfo client) => Path.Combine(AppPaths.TokenDir, client.TokenFolderName);

    private static string TokenFile(OAuthClientInfo client, string accountId) =>
        Path.Combine(TokenDir(client), "Google.Apis.Auth.OAuth2.Responses.TokenResponse-" + accountId);

    public static bool HasClientSecret() => System.IO.File.Exists(AppPaths.ClientSecretPath);

    /// <summary>True when a refresh token for this account and the current OAuth client is stored, i.e. connecting will not open a browser.</summary>
    public static bool HasStoredToken(string? accountId = null)
    {
        if (!HasClientSecret()) return false;
        try
        {
            accountId ??= GoogleAccounts.Resolve(null)?.Id;
            return accountId != null && System.IO.File.Exists(TokenFile(OAuthClientInfo.Read(AppPaths.ClientSecretPath), accountId));
        }
        catch { return false; }
    }

    /// <summary>Forget the stored Google sign-in of one account.</summary>
    public static void SignOut(string accountId)
    {
        if (!HasClientSecret()) return;
        var file = TokenFile(OAuthClientInfo.Read(AppPaths.ClientSecretPath), accountId);
        Credentials.TryRemove(file, out _);
        if (System.IO.File.Exists(file)) System.IO.File.Delete(file);
    }
    public async Task<(string Email, long Usage, long Limit, string StartPageToken)> WhoAmIAsync(CancellationToken ct)
    {
        var about = _service.About.Get();
        about.Fields = "user(emailAddress), storageQuota(usage, limit)";
        var a = await about.ExecuteAsync(ct);
        var token = await GetStartPageTokenAsync(ct);
        return (a.User.EmailAddress, a.StorageQuota.Usage ?? 0, a.StorageQuota.Limit ?? 0, token);
    }

    public async Task<string> GetStartPageTokenAsync(CancellationToken ct) =>
        (await _service.Changes.GetStartPageToken().ExecuteAsync(ct)).StartPageTokenValue;

    /// <summary>Resolves "Foo/Bar" (relative to My Drive) to a folder. "" or "/" is the root. The returned id is never the "root" alias.</summary>
    public async Task<File> ResolveFolderAsync(string drivePath, CancellationToken ct)
    {
        var current = new File { Id = "root", Name = "", MimeType = FolderMime };
        foreach (var part in drivePath.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var req = _service.Files.List();
            req.Q = $"'{current.Id}' in parents and name = '{Escape(part)}' and mimeType = '{FolderMime}' and trashed = false";
            req.Fields = $"files({FileFields})";
            req.PageSize = 10;
            var res = await req.ExecuteAsync(ct);
            var next = res.Files.FirstOrDefault()
                ?? throw new DirectoryNotFoundException($"No folder '{part}' under '{current.Name}' on Drive.");
            if (res.Files.Count > 1)
                Console.Error.WriteLine($"warning: {res.Files.Count} folders named '{part}' under '{current.Name}'; using the first.");
            current = next;
        }
        if (current.Id == "root")
        {
            // Changes report parents by real id, so the alias must be resolved for the index to link up.
            var get = _service.Files.Get("root");
            get.Fields = FileFields;
            current = await get.ExecuteAsync(ct);
        }
        return current;
    }

    /// <summary>Like <see cref="ResolveFolderAsync"/> but creates missing folders along the way.</summary>
    public async Task<File> EnsureFolderAsync(string drivePath, CancellationToken ct)
    {
        var current = await ResolveFolderAsync("", ct);
        foreach (var part in drivePath.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var req = _service.Files.List();
            req.Q = $"'{current.Id}' in parents and name = '{Escape(part)}' and mimeType = '{FolderMime}' and trashed = false";
            req.Fields = $"files({FileFields})";
            req.PageSize = 10;
            var res = await req.ExecuteAsync(ct);
            current = res.Files.FirstOrDefault() ?? await CreateFolderAsync(part, current.Id, ct);
        }
        return current;
    }

    /// <summary>Current metadata for one file; null when it no longer exists or is trashed.</summary>
    public async Task<File?> GetFileAsync(string fileId, CancellationToken ct)
    {
        try
        {
            var req = _service.Files.Get(fileId);
            req.Fields = FileFields;
            var f = await req.ExecuteAsync(ct);
            return f.Trashed == true ? null : f;
        }
        catch (Google.GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<List<File>> ListChildrenAsync(string folderId, CancellationToken ct)
    {
        var all = new List<File>();
        string? pageToken = null;
        do
        {
            var req = _service.Files.List();
            req.Q = $"'{folderId}' in parents and trashed = false";
            req.Fields = $"nextPageToken, files({FileFields})";
            req.PageSize = 1000;
            req.OrderBy = "folder, name";
            req.PageToken = pageToken;
            var res = await req.ExecuteAsync(ct);
            all.AddRange(res.Files);
            pageToken = res.NextPageToken;
        } while (pageToken != null);
        return all;
    }

    /// <summary>All changes since <paramref name="pageToken"/>, across the whole Drive; the caller filters by tree membership.</summary>
    public async Task<(List<Google.Apis.Drive.v3.Data.Change> Changes, string NewStartToken)> ListChangesAsync(string pageToken, CancellationToken ct)
    {
        var all = new List<Google.Apis.Drive.v3.Data.Change>();
        string? token = pageToken;
        string? newStart = null;
        while (token != null)
        {
            var req = _service.Changes.List(token);
            req.Fields = $"nextPageToken, newStartPageToken, changes(fileId, removed, file({FileFields}))";
            req.IncludeRemoved = true;
            req.PageSize = 1000;
            req.Spaces = "drive";
            var res = await req.ExecuteAsync(ct);
            all.AddRange(res.Changes);
            token = res.NextPageToken;
            newStart ??= res.NewStartPageToken;
            if (res.NewStartPageToken != null) newStart = res.NewStartPageToken;
        }
        return (all, newStart ?? pageToken);
    }

    /// <summary>Downloads to a temp file next to the target, then swaps it in and stamps the Drive modified time.</summary>
    public async Task DownloadAsync(File file, string localPath, CancellationToken ct)
    {
        var req = _service.Files.Get(file.Id);
        await DownloadToAsync((s, c) => req.DownloadAsync(s, c), file.Name, localPath, ct);
        if (file.ModifiedTimeDateTimeOffset is { } mt)
            System.IO.File.SetLastWriteTimeUtc(localPath, mt.UtcDateTime);
    }

    /// <summary>Exports a Google-native document (Docs -> docx etc.). Drive caps exports at 10 MB.</summary>
    public async Task ExportAsync(string fileId, string name, string exportMime, string localPath, DateTime modifiedUtc, CancellationToken ct)
    {
        var req = _service.Files.Export(fileId, exportMime);
        await DownloadToAsync((s, c) => req.DownloadAsync(s, c), name, localPath, ct);
        System.IO.File.SetLastWriteTimeUtc(localPath, modifiedUtc);
    }

    private static async Task DownloadToAsync(Func<Stream, CancellationToken, Task<IDownloadProgress>> download, string name, string localPath, CancellationToken ct)
    {
        var tmp = localPath + ".unisono-part";
        try
        {
            await using (var fs = System.IO.File.Create(tmp))
            {
                var progress = await download(fs, ct);
                if (progress.Status != DownloadStatus.Completed)
                    throw new IOException($"Download of '{name}' failed: {progress.Exception?.Message ?? progress.Status.ToString()}");
            }
            if (System.IO.File.Exists(localPath))
                System.IO.File.SetAttributes(localPath, FileAttributes.Normal);
            System.IO.File.Move(tmp, localPath, overwrite: true);
        }
        finally
        {
            if (System.IO.File.Exists(tmp)) System.IO.File.Delete(tmp);
        }
    }

    /// <summary>Creates a new file under <paramref name="parentId"/> with the local content and modified time.</summary>
    public async Task<File> UploadNewAsync(string localPath, string name, string parentId, CancellationToken ct)
    {
        // Several uploads run at once, so Drive may answer "rate limit exceeded", and a connection may drop on a long
        // run; both are worth a few more tries after a pause. A failed attempt created no file.
        for (var attempt = 1; ; attempt++)
        {
            var meta = new File
            {
                Name = name,
                Parents = new[] { parentId },
                ModifiedTimeDateTimeOffset = new DateTimeOffset(System.IO.File.GetLastWriteTimeUtc(localPath)),
            };
            await using var fs = OpenShared(localPath);
            var up = _service.Files.Create(meta, fs, OctetStream);
            up.Fields = FileFields;
            var progress = await up.UploadAsync(ct);
            if (progress.Status == UploadStatus.Completed) return up.ResponseBody;
            if (attempt < 6 && IsWorthRetrying(progress.Exception))
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt) + Random.Shared.NextDouble()), ct);
                continue;
            }
            throw new IOException($"Upload of '{name}' failed: {progress.Exception?.Message ?? progress.Status.ToString()}");
        }
    }

    private static bool IsWorthRetrying(Exception? ex) => ex switch
    {
        Google.GoogleApiException g => (int)g.HttpStatusCode is 429 or >= 500
            || (g.HttpStatusCode == System.Net.HttpStatusCode.Forbidden && g.Error?.Errors?.Any(e => e.Reason is "rateLimitExceeded" or "userRateLimitExceeded") == true),
        HttpRequestException or IOException or TaskCanceledException => true,
        _ => false,
    };

    /// <summary>Replaces the content of an existing file and stamps the local modified time on it.</summary>
    public async Task<File> UploadUpdateAsync(string localPath, string fileId, CancellationToken ct)
    {
        var meta = new File { ModifiedTimeDateTimeOffset = new DateTimeOffset(System.IO.File.GetLastWriteTimeUtc(localPath)) };
        await using var fs = OpenShared(localPath);
        var up = _service.Files.Update(meta, fileId, fs, OctetStream);
        up.Fields = FileFields;
        var progress = await up.UploadAsync(ct);
        if (progress.Status != UploadStatus.Completed)
            throw new IOException($"Upload of '{Path.GetFileName(localPath)}' failed: {progress.Exception?.Message ?? progress.Status.ToString()}");
        return up.ResponseBody;
    }

    /// <summary>
    /// Writes an Office file into an existing Google document: uploading docx/xlsx/pptx content to a Docs/Sheets/Slides
    /// file makes Drive convert it. The previous content stays in the document's version history.
    /// </summary>
    public async Task<File> ImportIntoAsync(string localPath, string fileId, string sourceMime, CancellationToken ct)
    {
        await using var fs = OpenShared(localPath);
        var up = _service.Files.Update(new File(), fileId, fs, sourceMime);
        up.Fields = FileFields;
        var progress = await up.UploadAsync(ct);
        if (progress.Status != UploadStatus.Completed)
            throw new IOException($"Writing '{Path.GetFileName(localPath)}' back into the Google document failed: {progress.Exception?.Message ?? progress.Status.ToString()}");
        return up.ResponseBody;
    }

    /// <summary>
    /// What a docx round trip would lose in a Google document: its tabs (Docs API) and open comments (Drive API).
    /// Null when the Docs API cannot be reached (not enabled for the OAuth project): then nothing is known.
    /// </summary>
    public async Task<(int Tabs, int Comments)?> RoundTripRiskAsync(string fileId, bool isDocument, CancellationToken ct)
    {
        var tabs = 1;
        if (isDocument)
        {
            using var res = await _service.HttpClient.GetAsync(
                $"https://docs.googleapis.com/v1/documents/{fileId}?includeTabsContent=true&fields=tabs(tabProperties(tabId),childTabs(tabProperties(tabId)))", ct);
            if (!res.IsSuccessStatusCode) return null;
            using var doc = System.Text.Json.JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            static int Count(System.Text.Json.JsonElement list) => list.EnumerateArray().Sum(x => 1 + (x.TryGetProperty("childTabs", out var c) ? Count(c) : 0));
            tabs = doc.RootElement.TryGetProperty("tabs", out var t) ? Count(t) : 1;
        }
        var comments = _service.Comments.List(fileId);
        comments.Fields = "comments(id, resolved)";
        comments.PageSize = 100;
        var list = await comments.ExecuteAsync(ct);
        return (tabs, list.Comments?.Count(x => x.Resolved != true) ?? 0);
    }
    /// <summary>
    /// Whether the Google Docs API is enabled for the OAuth client's project (enablement is per project, not per account):
    /// a request for a document that does not exist answers 404 when it is, 403 SERVICE_DISABLED with a link when not.
    /// Null when it cannot be told (offline, other errors).
    /// </summary>
    public async Task<(bool Enabled, string? ActivationUrl)?> DocsApiStatusAsync(CancellationToken ct)
    {
        try
        {
            using var res = await _service.HttpClient.GetAsync("https://docs.googleapis.com/v1/documents/unisono-api-check?fields=documentId", ct);
            var body = await res.Content.ReadAsStringAsync(ct);
            if (body.Contains("SERVICE_DISABLED", StringComparison.Ordinal))
            {
                var m = System.Text.RegularExpressions.Regex.Match(body, "\"activationUrl\"\\s*:\\s*\"([^\"]+)\"");
                return (false, m.Success ? m.Groups[1].Value : null);
            }
            return (int)res.StatusCode is 404 or 400 or 200 ? (true, null) : null;
        }
        catch (Exception) when (!ct.IsCancellationRequested) { return null; }
    }

    /// <summary>
    /// The current revision of a Google Docs document (Docs API): it changes with every edit at once, while Drive's
    /// modified time trails web edits by a minute or two. Null when the Docs API is not reachable.
    /// </summary>
    public async Task<string?> DocsRevisionAsync(string fileId, CancellationToken ct)
    {
        using var res = await _service.HttpClient.GetAsync($"https://docs.googleapis.com/v1/documents/{fileId}?fields=revisionId", ct);
        if (!res.IsSuccessStatusCode) return null;
        using var doc = System.Text.Json.JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        return doc.RootElement.TryGetProperty("revisionId", out var r) ? r.GetString() : null;
    }

    /// <summary>A Google document's tabs in document order with their nesting depth (Docs API); null when unreachable.</summary>
    public async Task<List<(string Title, int Depth)>?> DocsTabsAsync(string fileId, CancellationToken ct)
    {
        const string props = "tabProperties(title)";
        using var res = await _service.HttpClient.GetAsync(
            $"https://docs.googleapis.com/v1/documents/{fileId}?includeTabsContent=true&fields=tabs({props},childTabs({props},childTabs({props},childTabs({props}))))", ct);
        if (!res.IsSuccessStatusCode) return null;
        using var doc = System.Text.Json.JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        var list = new List<(string, int)>();
        void Walk(System.Text.Json.JsonElement tabs, int depth)
        {
            foreach (var t in tabs.EnumerateArray())
            {
                var title = t.TryGetProperty("tabProperties", out var p) && p.TryGetProperty("title", out var ti) ? ti.GetString() ?? "" : "";
                list.Add((title, depth));
                if (t.TryGetProperty("childTabs", out var c)) Walk(c, depth + 1);
            }
        }
        if (doc.RootElement.TryGetProperty("tabs", out var top)) Walk(top, 0);
        return list;
    }

    /// <summary>The whole document with all tabs' content (Docs API); null when unreachable.</summary>
    public async Task<System.Text.Json.JsonDocument?> DocsDocumentAsync(string fileId, CancellationToken ct)
    {
        using var res = await _service.HttpClient.GetAsync($"https://docs.googleapis.com/v1/documents/{fileId}?includeTabsContent=true", ct);
        return res.IsSuccessStatusCode ? System.Text.Json.JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct)) : null;
    }

    /// <summary>
    /// Applies Docs API requests only if the document is still at <paramref name="requiredRevision"/>; returns the new
    /// revision, or null when the document changed in the meantime (nothing was applied then).
    /// </summary>
    public async Task<string?> DocsBatchUpdateAsync(string fileId, IReadOnlyList<object> requests, string requiredRevision, CancellationToken ct)
    {
        var body = System.Text.Json.JsonSerializer.Serialize(new { requests, writeControl = new { requiredRevisionId = requiredRevision } });
        using var res = await _service.HttpClient.PostAsync($"https://docs.googleapis.com/v1/documents/{fileId}:batchUpdate",
            new StringContent(body, System.Text.Encoding.UTF8, "application/json"), ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
        {
            if (text.Contains("revision", StringComparison.OrdinalIgnoreCase) && (int)res.StatusCode == 400) return null;
            throw new IOException($"Docs API {(int)res.StatusCode}: {text[..Math.Min(400, text.Length)]}");
        }
        using var doc = System.Text.Json.JsonDocument.Parse(text);
        return doc.RootElement.TryGetProperty("writeControl", out var wc) && wc.TryGetProperty("requiredRevisionId", out var r) ? r.GetString() : requiredRevision;
    }

    /// <summary>Adds a tab to a Google document; returns its id (tests).</summary>
    public async Task<string> DocsAddTabAsync(string fileId, string title, CancellationToken ct)
    {
        using var reply = await DocsRequestsAsync(fileId, new object[] { new { addDocumentTab = new { tabProperties = new { title } } } }, ct);
        return reply.RootElement.GetProperty("replies")[0].GetProperty("addDocumentTab").GetProperty("tabProperties").GetProperty("tabId").GetString()!;
    }

    /// <summary>Raw Docs API batchUpdate without a revision condition (tests).</summary>
    public async Task<System.Text.Json.JsonDocument> DocsRequestsAsync(string fileId, IReadOnlyList<object> requests, CancellationToken ct)
    {
        var body = System.Text.Json.JsonSerializer.Serialize(new { requests });
        using var res = await _service.HttpClient.PostAsync($"https://docs.googleapis.com/v1/documents/{fileId}:batchUpdate",
            new StringContent(body, System.Text.Encoding.UTF8, "application/json"), ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode) throw new IOException($"Docs API {(int)res.StatusCode}: {text[..Math.Min(400, text.Length)]}");
        return System.Text.Json.JsonDocument.Parse(text);
    }

    /// <summary>Inserts text at the start of a Google Docs document, as an edit in the browser would (tests).</summary>
    public async Task DocsInsertAsync(string fileId, string text, CancellationToken ct)
    {
        var body = System.Text.Json.JsonSerializer.Serialize(new { requests = new[] { new { insertText = new { location = new { index = 1 }, text = text + "\n" } } } });
        using var res = await _service.HttpClient.PostAsync($"https://docs.googleapis.com/v1/documents/{fileId}:batchUpdate",
            new StringContent(body, System.Text.Encoding.UTF8, "application/json"), ct);
        if (!res.IsSuccessStatusCode) throw new IOException($"Docs API: {(int)res.StatusCode} {await res.Content.ReadAsStringAsync(ct)}");
    }

    /// <summary>An empty Google document (tests).</summary>
    public async Task<File> CreateNativeAsync(string name, string mime, string parentId, CancellationToken ct)
    {
        var req = _service.Files.Create(new File { Name = name, MimeType = mime, Parents = new[] { parentId } });
        req.Fields = FileFields;
        return await req.ExecuteAsync(ct);
    }

    public async Task<File> CreateFolderAsync(string name, string parentId, CancellationToken ct)
    {
        var req = _service.Files.Create(new File { Name = name, MimeType = FolderMime, Parents = new[] { parentId } });
        req.Fields = FileFields;
        return await req.ExecuteAsync(ct);
    }

    /// <summary>Rename and/or move; the id stays the same.</summary>
    public async Task<File> MoveAsync(string fileId, string newName, string oldParentId, string newParentId, CancellationToken ct)
    {
        var req = _service.Files.Update(new File { Name = newName }, fileId);
        if (oldParentId != newParentId)
        {
            req.AddParents = newParentId;
            req.RemoveParents = oldParentId;
        }
        req.Fields = FileFields;
        return await req.ExecuteAsync(ct);
    }

    /// <summary>Never a permanent delete: the item goes to the Drive trash, where it can be restored for 30 days.</summary>
    public async Task TrashAsync(string fileId, CancellationToken ct)
    {
        var req = _service.Files.Update(new File { Trashed = true }, fileId);
        req.Fields = "id";
        await req.ExecuteAsync(ct);
    }

    // ------------------------------------------------------------------ sharing

    /// <summary>Name, type, web link and whether this account may change sharing.</summary>
    public async Task<File> GetShareInfoAsync(string fileId, CancellationToken ct)
    {
        var req = _service.Files.Get(fileId);
        req.Fields = "id, name, mimeType, webViewLink, capabilities(canShare), owners(emailAddress, displayName)";
        return await req.ExecuteAsync(ct);
    }

    public async Task<IList<Google.Apis.Drive.v3.Data.Permission>> ListPermissionsAsync(string fileId, CancellationToken ct)
    {
        var req = _service.Permissions.List(fileId);
        req.Fields = "permissions(id, type, role, emailAddress, domain, displayName, deleted)";
        return (await req.ExecuteAsync(ct)).Permissions;
    }

    /// <summary>type: user, group, domain or anyone; role: reader, commenter or writer.</summary>
    public async Task CreatePermissionAsync(string fileId, string type, string role, string? email, string? domain,
        bool notify, string? message, CancellationToken ct)
    {
        var req = _service.Permissions.Create(new Google.Apis.Drive.v3.Data.Permission
        {
            Type = type, Role = role, EmailAddress = email, Domain = domain,
        }, fileId);
        if (type is "user" or "group")
        {
            req.SendNotificationEmail = notify;
            if (notify && !string.IsNullOrWhiteSpace(message)) req.EmailMessage = message;
        }
        await req.ExecuteAsync(ct);
    }

    public async Task UpdatePermissionAsync(string fileId, string permissionId, string role, CancellationToken ct) =>
        await _service.Permissions.Update(new Google.Apis.Drive.v3.Data.Permission { Role = role }, fileId, permissionId).ExecuteAsync(ct);

    public async Task DeletePermissionAsync(string fileId, string permissionId, CancellationToken ct) =>
        await _service.Permissions.Delete(fileId, permissionId).ExecuteAsync(ct);

    public static bool IsFolder(File f) => f.MimeType == FolderMime;

    /// <summary>Docs, Sheets, Slides, Forms, ... : exist on Drive but have no byte representation.</summary>
    public static bool IsGoogleNative(File f) =>
        f.MimeType != null && f.MimeType.StartsWith("application/vnd.google-apps.", StringComparison.Ordinal) && !IsFolder(f);

    /// <summary>True when the local file already matches the Drive copy (size and md5), so a pull can skip it.</summary>
    public static bool LocalMatches(File f, string localPath)
    {
        if (!System.IO.File.Exists(localPath) || f.Md5Checksum == null) return false;
        var info = new FileInfo(localPath);
        if (f.Size is { } size && info.Length != size) return false;
        return string.Equals(Md5Of(localPath), f.Md5Checksum, StringComparison.OrdinalIgnoreCase);
    }

    public static string Md5Of(string localPath) => PathUtil.Md5Of(localPath);

    private static FileStream OpenShared(string path) => PathUtil.OpenShared(path);

    private static string Escape(string name) => name.Replace("\\", "\\\\").Replace("'", "\\'");
}
