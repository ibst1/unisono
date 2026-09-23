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
    public const string FileFields = "id, name, mimeType, size, modifiedTime, md5Checksum, parents, trashed";
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
        var flow = new GoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer
        {
            ClientSecrets = secrets,
            Scopes = new[] { DriveService.Scope.Drive },
            DataStore = new FileDataStore(TokenDir(client), fullPath: true),
            Prompt = chooseAccount ? "select_account consent" : null,
        });
        var credential = await new Google.Apis.Auth.OAuth2.AuthorizationCodeInstalledApp(flow, new LocalServerCodeReceiver())
            .AuthorizeAsync(accountId, ct);

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
        if (progress.Status != UploadStatus.Completed)
            throw new IOException($"Upload of '{name}' failed: {progress.Exception?.Message ?? progress.Status.ToString()}");
        return up.ResponseBody;
    }

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

    public async Task<File> CreateFolderAsync(string name, string parentId, CancellationToken ct)
    {
        var req = _service.Files.Create(new File { Name = name, MimeType = FolderMime, Parents = new[] { parentId } });
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
