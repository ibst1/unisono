using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Unisono;

/// <summary>
/// The user's own Box app (Box Developer Console: a Custom App with "User Authentication (OAuth 2.0)", redirect URI
/// <see cref="RedirectUri"/>, scopes "Read and write all files and folders"). Its client secret is kept DPAPI-encrypted
/// for the Windows user, like the NAS logins.
/// </summary>
public sealed class BoxApp
{
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";

    /// <summary>Where Box sends the browser back after the sign-in; register exactly this in the Box app.</summary>
    public const string RedirectUri = "http://localhost:50734/box";

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Unisono.Box.app.v1");
    private static string FilePath => Path.Combine(AppPaths.AuthDir, "box-app.bin");

    public static BoxApp? Load()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<BoxApp>(ProtectedData.Unprotect(File.ReadAllBytes(FilePath), Entropy, DataProtectionScope.CurrentUser)) : null; }
        catch { return null; }
    }

    public void Save()
    {
        AppPaths.EnsureDataDir();
        File.WriteAllBytes(FilePath, ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(this), Entropy, DataProtectionScope.CurrentUser));
    }
}

/// <summary>A signed-in Box account: its tokens, DPAPI-encrypted, one file per login.</summary>
public sealed class BoxAccount
{
    public string Login { get; set; } = "";
    public string? Name { get; set; }
    public string RefreshToken { get; set; } = "";
    public string? AccessToken { get; set; }
    public DateTime AccessExpiresUtc { get; set; }

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Unisono.Box.account.v1");
    private static string Slug(string s) => string.Concat(s.Where(char.IsLetterOrDigit)).ToLowerInvariant();
    internal static string PathFor(string login) => Path.Combine(AppPaths.AuthDir, $"box-{Slug(login)}.bin");

    public static BoxAccount? Load(string login) => File.Exists(PathFor(login)) ? LoadFile(PathFor(login)) : null;

    private static BoxAccount? LoadFile(string path)
    {
        try { return JsonSerializer.Deserialize<BoxAccount>(ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser)); }
        catch { return null; } // another Windows user's, or damaged
    }

    public static List<BoxAccount> ListAll()
    {
        if (!Directory.Exists(AppPaths.AuthDir)) return new();
        return Directory.EnumerateFiles(AppPaths.AuthDir, "box-*.bin").Where(f => !f.EndsWith("box-app.bin", StringComparison.OrdinalIgnoreCase))
            .Select(LoadFile).Where(a => a != null).Select(a => a!).OrderBy(a => a.Login, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>By login; null or empty = the only (or first) account.</summary>
    public static BoxAccount? Resolve(string? login) =>
        string.IsNullOrEmpty(login) ? ListAll().FirstOrDefault() : Load(login);

    public void Save()
    {
        AppPaths.EnsureDataDir();
        var path = PathFor(Login);
        var tmp = path + "." + Environment.ProcessId + ".tmp";
        File.WriteAllBytes(tmp, ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(this), Entropy, DataProtectionScope.CurrentUser));
        File.Move(tmp, path, overwrite: true);
    }

    public static void Delete(string login) { try { File.Delete(PathFor(login)); } catch { } }
}

/// <summary>An item as Box describes it (file or folder; web links are left out by the listing).</summary>
public sealed record BoxItem(string Id, string Type, string Name, long Size, string? Sha1, DateTime ModifiedUtc, string ParentId, bool Trashed)
{
    public bool IsFolder => Type == "folder";
}

public sealed class BoxApiException : Exception
{
    public HttpStatusCode Status { get; }
    public string? Code { get; }
    public JsonNode? Body { get; }
    public BoxApiException(HttpStatusCode status, string? code, string message, JsonNode? body) : base(message) { Status = status; Code = code; Body = body; }
}

/// <summary>
/// Box's content API for one account: listing, transfers (chunked above 50 MB), folders, moves, trash, versions and
/// the event stream. Access tokens last an hour and the refresh token is replaced at every refresh, so refreshing is
/// serialised across Unisono's processes (tray, CLI) with a named mutex and the new tokens saved at once.
/// </summary>
public sealed class BoxClient : IDisposable
{
    private const string Api = "https://api.box.com/2.0";
    private const string Upload = "https://upload.box.com/api/2.0";
    private const string Fields = "id,type,name,size,sha1,content_modified_at,modified_at,parent,item_status";
    /// <summary>Above this a file goes up in parts (Box takes at most 50 MB in one request).</summary>
    private const long ChunkedFrom = 50L * 1024 * 1024;

    private static readonly HttpClient Http = new(new SocketsHttpHandler { AllowAutoRedirect = true, PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
    { Timeout = Timeout.InfiniteTimeSpan };

    private readonly BoxApp _app;
    private BoxAccount _account;
    private readonly SemaphoreSlim _refresh = new(1, 1);

    public string Login => _account.Login;
    public string? Name => _account.Name;

    private BoxClient(BoxApp app, BoxAccount account) { _app = app; _account = account; }

    public static bool HasApp() => BoxApp.Load() is { ClientId.Length: > 0, ClientSecret.Length: > 0 };

    /// <summary>A stored account, ready to use (its access token is refreshed when needed).</summary>
    public static BoxClient Connect(string? login)
    {
        var app = BoxApp.Load() ?? throw new InvalidOperationException("No Box app yet: enter its client id and secret under Accounts (see README, Box).");
        var acct = BoxAccount.Resolve(login) ?? throw new InvalidOperationException(string.IsNullOrEmpty(login)
            ? "no Box account signed in; add one under Accounts" : $"the Box account {login} is not signed in on this computer; add it under Accounts");
        return new BoxClient(app, acct);
    }

    // ------------------------------------------------------------------ sign-in

    /// <summary>
    /// Signs in in the browser (Box's own page; a university's sends it on to its single sign-on) and stores the
    /// account. <paramref name="openBrowser"/> opens the given address.
    /// </summary>
    public static async Task<BoxClient> SignInAsync(Action<string> openBrowser, CancellationToken ct)
    {
        var app = BoxApp.Load() ?? throw new InvalidOperationException("Enter the Box app's client id and secret first.");
        var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        using var listener = new HttpListener();
        listener.Prefixes.Add(BoxApp.RedirectUri.TrimEnd('/') + "/");
        try { listener.Start(); }
        catch (HttpListenerException ex) { throw new InvalidOperationException($"Port {new Uri(BoxApp.RedirectUri).Port} is in use, so the sign-in cannot come back to Unisono: {ex.Message}"); }
        openBrowser("https://account.box.com/api/oauth2/authorize?response_type=code&client_id=" + Uri.EscapeDataString(app.ClientId) +
                    "&redirect_uri=" + Uri.EscapeDataString(BoxApp.RedirectUri) + "&state=" + state);
        string code;
        while (true)
        {
            var getContext = listener.GetContextAsync();
            if (await Task.WhenAny(getContext, Task.Delay(Timeout.Infinite, ct)) != getContext) { ct.ThrowIfCancellationRequested(); }
            var ctx = await getContext;
            var q = System.Web.HttpUtility.ParseQueryString(ctx.Request.Url?.Query ?? "");
            var ok = q["state"] == state && q["code"] is { Length: > 0 };
            var page = ok ? "Unisono is signed in to Box. You can close this tab." : $"Box sign-in did not complete: {WebUtility.HtmlEncode(q["error_description"] ?? q["error"] ?? "no code")}";
            var bytes = Encoding.UTF8.GetBytes($"<!doctype html><meta charset=utf-8><title>Unisono</title><body style=\"font:16px system-ui;margin:3em\">{page}</body>");
            ctx.Response.ContentType = "text/html; charset=utf-8";
            await ctx.Response.OutputStream.WriteAsync(bytes, ct);
            ctx.Response.Close();
            if (q["state"] != state) continue; // a stray request (favicon, an old tab)
            if (!ok) throw new InvalidOperationException("Box sign-in did not complete: " + (q["error_description"] ?? q["error"] ?? "no code"));
            code = q["code"]!;
            break;
        }
        var token = await TokenAsync(app, new() { ["grant_type"] = "authorization_code", ["code"] = code }, ct);
        var client = new BoxClient(app, new BoxAccount { RefreshToken = token.Refresh, AccessToken = token.Access, AccessExpiresUtc = token.Expires });
        var me = await client.MeAsync(ct);
        client._account.Login = me.Login;
        client._account.Name = me.Name;
        client._account.Save();
        return client;
    }

    private sealed record Tokens(string Access, string Refresh, DateTime Expires);

    private static async Task<Tokens> TokenAsync(BoxApp app, Dictionary<string, string> form, CancellationToken ct)
    {
        form["client_id"] = app.ClientId;
        form["client_secret"] = app.ClientSecret;
        using var resp = await Http.PostAsync("https://api.box.com/oauth2/token", new FormUrlEncodedContent(form), ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        var json = JsonNode.Parse(text);
        if (!resp.IsSuccessStatusCode)
            throw new BoxApiException(resp.StatusCode, json?["error"]?.GetValue<string>(), "Box sign-in: " + (json?["error_description"]?.GetValue<string>() ?? text), json);
        return new Tokens(json!["access_token"]!.GetValue<string>(), json["refresh_token"]!.GetValue<string>(),
            DateTime.UtcNow.AddSeconds(json["expires_in"]?.GetValue<int>() ?? 3600));
    }

    /// <summary>A valid access token; refreshed a few minutes before it runs out, or at once when <paramref name="force"/>.</summary>
    private async Task<string> AccessAsync(CancellationToken ct, bool force = false)
    {
        if (!force && _account.AccessToken != null && _account.AccessExpiresUtc > DateTime.UtcNow.AddMinutes(5)) return _account.AccessToken;
        await _refresh.WaitAsync(ct);
        try
        {
            // Another Unisono process may have refreshed meanwhile: its tokens are on disk, and ours are spent.
            using var mutex = new Mutex(false, @"Local\Unisono.Box." + Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(_account.Login.ToUpperInvariant()))));
            var owned = false;
            try
            {
                try { owned = mutex.WaitOne(TimeSpan.FromSeconds(30)); } catch (AbandonedMutexException) { owned = true; }
                if (BoxAccount.Load(_account.Login) is { } disk && disk.RefreshToken != _account.RefreshToken) _account = disk;
                if (!force && _account.AccessToken != null && _account.AccessExpiresUtc > DateTime.UtcNow.AddMinutes(5)) return _account.AccessToken;
                Tokens t;
                try { t = await TokenAsync(_app, new() { ["grant_type"] = "refresh_token", ["refresh_token"] = _account.RefreshToken }, ct); }
                catch (BoxApiException ex) when (ex.Code is "invalid_grant")
                {
                    throw new InvalidOperationException($"The Box sign-in of {_account.Login} has run out (Box keeps it 60 days without use): sign in again under Accounts.", ex);
                }
                _account.AccessToken = t.Access;
                _account.RefreshToken = t.Refresh;
                _account.AccessExpiresUtc = t.Expires;
                _account.Save();
                return t.Access;
            }
            finally { if (owned) mutex.ReleaseMutex(); }
        }
        finally { _refresh.Release(); }
    }

    // ------------------------------------------------------------------ requests

    /// <summary>
    /// One API call, with the access token, retried on 429 and 5xx (Box's rate limits say how long to wait) and once
    /// with a fresh token on 401. <paramref name="body"/> makes the content anew for each attempt.
    /// </summary>
    private async Task<JsonNode?> SendAsync(HttpMethod method, string url, Func<HttpContent?>? body, CancellationToken ct, bool nullOn404 = false, Action<HttpRequestMessage>? headers = null)
    {
        var fresh = false;
        for (var attempt = 1; ; attempt++)
        {
            using var req = new HttpRequestMessage(method, url) { Content = body?.Invoke() };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await AccessAsync(ct, force: fresh));
            headers?.Invoke(req);
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
            if (resp.StatusCode == HttpStatusCode.Unauthorized && !fresh) { fresh = true; continue; }
            if ((resp.StatusCode == HttpStatusCode.TooManyRequests || (int)resp.StatusCode >= 500) && attempt < 6)
            {
                var wait = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Pow(2, attempt));
                await Task.Delay(wait, ct);
                continue;
            }
            if (resp.StatusCode == HttpStatusCode.Accepted && resp.Headers.RetryAfter?.Delta is { } later && attempt < 30)
            {
                await Task.Delay(later, ct); // a chunked upload's commit still assembling the parts
                continue;
            }
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (resp.StatusCode == HttpStatusCode.NotFound && nullOn404) return null;
            JsonNode? json = null;
            try { json = text.Length > 0 ? JsonNode.Parse(text) : null; } catch (JsonException) { }
            if (!resp.IsSuccessStatusCode)
                throw new BoxApiException(resp.StatusCode, json?["code"]?.GetValue<string>(),
                    $"Box: {(json?["message"]?.GetValue<string>() ?? resp.ReasonPhrase)} ({(int)resp.StatusCode}{(json?["code"] is { } c ? ", " + c : "")})", json);
            return json;
        }
    }

    private static HttpContent Json(object o) => new StringContent(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json");

    private static string Time(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToString("yyyy-MM-dd'T'HH:mm:ss'+00:00'");

    private static BoxItem ToItem(JsonNode n)
    {
        var type = n["type"]!.GetValue<string>();
        var when = (type == "file" ? n["content_modified_at"] : null) ?? n["modified_at"];
        return new BoxItem(
            n["id"]!.GetValue<string>(), type, n["name"]?.GetValue<string>() ?? "",
            n["size"]?.GetValue<long>() ?? 0, n["sha1"]?.GetValue<string>()?.ToLowerInvariant(),
            when != null ? DateTimeOffset.Parse(when.GetValue<string>()).UtcDateTime : DateTime.MinValue,
            n["parent"]?["id"]?.GetValue<string>() ?? "", n["item_status"]?.GetValue<string>() is "trashed" or "deleted");
    }

    public async Task<(string Login, string? Name, long Used, long Limit)> MeAsync(CancellationToken ct)
    {
        var me = await SendAsync(HttpMethod.Get, Api + "/users/me?fields=login,name,space_amount,space_used", null, ct);
        return (me!["login"]!.GetValue<string>(), me["name"]?.GetValue<string>(), me["space_used"]?.GetValue<long>() ?? 0, me["space_amount"]?.GetValue<long>() ?? 0);
    }

    /// <summary>The files and folders in a folder (web links and Box's trash left out), all pages.</summary>
    public async Task<List<BoxItem>> ListAsync(string folderId, CancellationToken ct)
    {
        var items = new List<BoxItem>();
        string? marker = null;
        do
        {
            var page = await SendAsync(HttpMethod.Get, $"{Api}/folders/{folderId}/items?fields={Fields}&limit=1000&usemarker=true" +
                (marker != null ? "&marker=" + Uri.EscapeDataString(marker) : ""), null, ct);
            foreach (var e in page!["entries"]!.AsArray())
                if (e!["type"]!.GetValue<string>() is "file" or "folder") items.Add(ToItem(e));
            marker = page["next_marker"]?.GetValue<string>();
        } while (!string.IsNullOrEmpty(marker));
        return items;
    }

    public async Task<BoxItem?> GetAsync(string id, bool folder, CancellationToken ct)
    {
        var n = await SendAsync(HttpMethod.Get, $"{Api}/{(folder ? "folders" : "files")}/{id}?fields={Fields}", null, ct, nullOn404: true);
        return n == null ? null : ToItem(n) is { Trashed: false } item ? item : null;
    }

    /// <summary>A folder by its path under All Files ("" = All Files itself, id 0). Throws DirectoryNotFoundException.</summary>
    public async Task<string> ResolveFolderAsync(string path, CancellationToken ct)
    {
        var id = "0";
        foreach (var part in path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var next = (await ListAsync(id, ct)).FirstOrDefault(i => i.IsFolder && i.Name.Equals(part, StringComparison.OrdinalIgnoreCase))
                       ?? throw new DirectoryNotFoundException($"'{path}' does not exist on Box (no folder '{part}').");
            id = next.Id;
        }
        return id;
    }

    /// <summary>The folder, created with any missing parents.</summary>
    public async Task<string> EnsureFolderAsync(string path, CancellationToken ct)
    {
        var id = "0";
        foreach (var part in path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var next = (await ListAsync(id, ct)).FirstOrDefault(i => i.IsFolder && i.Name.Equals(part, StringComparison.OrdinalIgnoreCase));
            id = next?.Id ?? (await CreateFolderAsync(id, part, ct)).Id;
        }
        return id;
    }

    /// <summary>A new folder; one that exists by that name already is returned instead.</summary>
    public async Task<BoxItem> CreateFolderAsync(string parentId, string name, CancellationToken ct)
    {
        try { return ToItem((await SendAsync(HttpMethod.Post, $"{Api}/folders?fields={Fields}", () => Json(new { name, parent = new { id = parentId } }), ct))!); }
        catch (BoxApiException ex) when (ex.Status == HttpStatusCode.Conflict && ConflictId(ex) is { } id)
        {
            return await GetAsync(id, folder: true, ct) ?? throw new IOException($"Box says a folder '{name}' exists, but it cannot be found", ex);
        }
    }

    private static string? ConflictId(BoxApiException ex)
    {
        var c = ex.Body?["context_info"]?["conflicts"];
        return (c is JsonArray a ? a.FirstOrDefault() : c)?["id"]?.GetValue<string>();
    }

    public async Task DownloadAsync(string fileId, string localPath, DateTime modifiedUtc, CancellationToken ct, string? versionId = null)
    {
        var url = $"{Api}/files/{fileId}/content" + (versionId != null ? "?version=" + versionId : "");
        for (var attempt = 1; ; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await AccessAsync(ct, force: attempt == 2));
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (resp.StatusCode == HttpStatusCode.Unauthorized && attempt == 1) continue;
            if ((resp.StatusCode == HttpStatusCode.TooManyRequests || (int)resp.StatusCode >= 500 || resp.StatusCode == HttpStatusCode.Accepted) && attempt < 6)
            {
                await Task.Delay(resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct);
                continue;
            }
            if (!resp.IsSuccessStatusCode) throw new BoxApiException(resp.StatusCode, null, $"Box: download failed ({(int)resp.StatusCode} {resp.ReasonPhrase})", null);
            var tmp = localPath + ".unisono-part";
            try
            {
                await using (var src = new ProgressStream(await resp.Content.ReadAsStreamAsync(ct)))
                await using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                    await src.CopyToAsync(dst, ct);
                File.SetLastWriteTimeUtc(tmp, modifiedUtc);
                File.Move(tmp, localPath, overwrite: true);
            }
            finally { if (File.Exists(tmp)) File.Delete(tmp); }
            return;
        }
    }

    /// <summary>A new file in the folder, or a new version of <paramref name="fileId"/>, with the local file's modified time.</summary>
    public async Task<BoxItem> UploadAsync(string localPath, string? parentId, string? name, string? fileId, CancellationToken ct)
    {
        var info = new FileInfo(localPath);
        var mtime = Time(info.LastWriteTimeUtc);
        if (info.Length >= ChunkedFrom) return await UploadChunkedAsync(localPath, info, parentId, name, fileId, mtime, ct);
        var url = fileId == null ? $"{Upload}/files/content?fields={Fields}" : $"{Upload}/files/{fileId}/content?fields={Fields}";
        object attributes = fileId == null
            ? new { name, parent = new { id = parentId }, content_modified_at = mtime, content_created_at = Time(info.CreationTimeUtc) }
            : new { content_modified_at = mtime };
        try
        {
            var resp = await SendAsync(HttpMethod.Post, url, () =>
            {
                var form = new MultipartFormDataContent();
                form.Add(new StringContent(JsonSerializer.Serialize(attributes), Encoding.UTF8), "attributes"); // must come before the file
                var file = new StreamContent(new ProgressStream(PathUtil.OpenShared(localPath)));
                file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                form.Add(file, "file", name ?? Path.GetFileName(localPath));
                return form;
            }, ct);
            return ToItem(resp!["entries"]![0]!);
        }
        catch (BoxApiException ex) when (fileId == null && ex.Status == HttpStatusCode.Conflict && ConflictId(ex) is { } existing)
        {
            return await UploadAsync(localPath, null, null, existing, ct); // a file by that name is there already: a new version of it
        }
    }

    /// <summary>Box's upload sessions: the file in parts of the size Box chooses, each with its SHA-1, then committed.</summary>
    private async Task<BoxItem> UploadChunkedAsync(string localPath, FileInfo info, string? parentId, string? name, string? fileId, string mtime, CancellationToken ct)
    {
        var session = fileId == null
            ? await SendAsync(HttpMethod.Post, $"{Upload}/files/upload_sessions", () => Json(new { folder_id = parentId, file_size = info.Length, file_name = name }), ct)
            : await SendAsync(HttpMethod.Post, $"{Upload}/files/{fileId}/upload_sessions", () => Json(new { file_size = info.Length }), ct);
        var partSize = session!["part_size"]!.GetValue<long>();
        var partUrl = session["session_endpoints"]!["upload_part"]!.GetValue<string>();
        var commitUrl = session["session_endpoints"]!["commit"]!.GetValue<string>();
        var parts = new JsonArray();
        using var whole = SHA1.Create();
        await using (var fs = new ProgressStream(PathUtil.OpenShared(localPath)))
        {
            var buf = new byte[partSize];
            for (long offset = 0; offset < info.Length; offset += partSize)
            {
                var n = 0;
                while (n < partSize) { var r = await fs.ReadAsync(buf.AsMemory(n, (int)Math.Min(partSize - n, int.MaxValue)), ct); if (r == 0) break; n += r; }
                whole.TransformBlock(buf, 0, n, null, 0);
                var digest = Convert.ToBase64String(SHA1.HashData(buf.AsSpan(0, n)));
                var (start, end, count) = (offset, offset + n - 1, n);
                var part = await SendAsync(HttpMethod.Put, partUrl, () =>
                {
                    var c = new ByteArrayContent(buf, 0, count);
                    c.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                    c.Headers.ContentRange = new ContentRangeHeaderValue(start, end, info.Length);
                    return c;
                }, ct, headers: r => r.Headers.TryAddWithoutValidation("Digest", "sha=" + digest));
                parts.Add(part!["part"]!.DeepClone());
            }
        }
        whole.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        var fileDigest = Convert.ToBase64String(whole.Hash!);
        var done = await SendAsync(HttpMethod.Post, commitUrl, () => new StringContent(
            new JsonObject { ["parts"] = parts.DeepClone(), ["attributes"] = new JsonObject { ["content_modified_at"] = mtime } }.ToJsonString(), Encoding.UTF8, "application/json"),
            ct, headers: r => r.Headers.TryAddWithoutValidation("Digest", "sha=" + fileDigest));
        return ToItem(done!["entries"]![0]!);
    }

    /// <summary>Renames and/or moves a file or folder (a folder with everything in it).</summary>
    public async Task<BoxItem> MoveAsync(string id, bool folder, string name, string parentId, CancellationToken ct) =>
        ToItem((await SendAsync(HttpMethod.Put, $"{Api}/{(folder ? "folders" : "files")}/{id}?fields={Fields}", () => Json(new { name, parent = new { id = parentId } }), ct))!);

    /// <summary>Into Box's trash, from which it can be restored; a folder with everything in it.</summary>
    public Task DeleteAsync(string id, bool folder, CancellationToken ct) =>
        SendAsync(HttpMethod.Delete, $"{Api}/{(folder ? "folders/" + id + "?recursive=true" : "files/" + id)}", null, ct, nullOn404: true);

    /// <summary>A file's earlier versions (newest first), as (version id, SHA-1).</summary>
    public async Task<List<(string Id, string? Sha1)>> VersionsAsync(string fileId, CancellationToken ct)
    {
        var json = await SendAsync(HttpMethod.Get, $"{Api}/files/{fileId}/versions?fields=id,sha1&limit=100", null, ct, nullOn404: true);
        return json?["entries"]?.AsArray().Select(v => (v!["id"]!.GetValue<string>(), v["sha1"]?.GetValue<string>()?.ToLowerInvariant())).ToList() ?? new();
    }

    /// <summary>The event stream's position now: changes after it are what <see cref="ChangedSinceAsync"/> reports.</summary>
    public async Task<string> StreamPositionAsync(CancellationToken ct) =>
        (await SendAsync(HttpMethod.Get, $"{Api}/events?stream_position=now", null, ct))!["next_stream_position"]!.ToString();

    /// <summary>
    /// Whether something changed since <paramref name="position"/> in the folder <paramref name="rootId"/> or below
    /// (any change when the folder is All Files), and the new position. Every change of the account comes through the
    /// stream; those elsewhere are recognised by the item's ancestors.
    /// </summary>
    public async Task<(bool Changed, string Position)> ChangedSinceAsync(string position, string rootId, CancellationToken ct)
    {
        var changed = false;
        while (true)
        {
            var page = await SendAsync(HttpMethod.Get, $"{Api}/events?stream_type=changes&limit=500&stream_position={Uri.EscapeDataString(position)}", null, ct);
            var entries = page!["entries"]!.AsArray();
            position = page["next_stream_position"]!.ToString();
            foreach (var e in entries)
            {
                var source = e?["source"];
                if (rootId == "0" || source == null) { changed = true; continue; }
                if (source["id"]?.ToString() == rootId) { changed = true; continue; }
                var ancestors = source["path_collection"]?["entries"]?.AsArray();
                if (ancestors == null || ancestors.Any(a => a?["id"]?.ToString() == rootId)) changed = true;
            }
            if (entries.Count < 500) return (changed, position);
        }
    }

    public void Dispose() => _refresh.Dispose();
}
