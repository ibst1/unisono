using System.Text;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Unisono;

public sealed record SynologyEntry(string Name, string Path, bool IsDir, long Size, DateTime MtimeUtc);

public sealed class SynologyApiException : Exception
{
    public int Code { get; }
    public SynologyApiException(string api, int code) : base($"{api} failed: {Describe(api, code)} (code {code})") => Code = code;

    // DSM reuses the 400s: the same number means different things for the login API and for File Station.
    private static string Describe(string api, int code) => code switch
    {
        100 => "unknown error", 101 => "invalid parameter", 102 => "API does not exist", 103 => "method does not exist",
        104 => "version not supported", 105 => "insufficient user privilege", 106 => "session timeout", 107 => "session interrupted",
        119 => "session id not found",
        _ when api.StartsWith("SYNO.API.Auth", StringComparison.Ordinal) => code switch
        {
            400 => "wrong account or password", 401 => "account disabled", 402 => "permission denied",
            403 => "two-factor code required", 404 => "two-factor code failed", 406 => "two-factor enforced",
            407 => "IP address blocked (DSM auto block after failed logins)", 408 => "password expired", 409 => "password expired",
            410 => "password must be changed", _ => "login error",
        },
        _ => code switch
        {
            400 => "invalid parameter", 401 => "unknown file operation error", 402 => "NAS too busy",
            403 => "this user may not do that", 404 => "this group may not do that", 407 => "operation not permitted",
            408 => "no such file or folder", 409 => "file system not supported", 411 => "read-only file system",
            412 or 413 => "name too long", 414 => "already exists", 415 => "disk quota exceeded", 416 => "no space left on the NAS",
            417 => "I/O error", 418 => "illegal name or path", 419 => "illegal file name", 421 => "file busy",
            900 => "delete failed", 1100 or 1101 => "could not create the folder",
            1800 or 1801 or 1802 or 1803 or 1804 or 1805 => "upload failed", _ => "file operation error",
        },
    };
}
/// <summary>
/// Minimal Synology File Station client over DSM's HTTPS port (5001): login with optional two-factor + device
/// token, list, getinfo, download, upload with mtime, create folder, delete. Works on DSM 6 and 7; versions are
/// read from SYNO.API.Info. A QuickConnect address is resolved by following its redirect once.
/// </summary>
public sealed class SynologyClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly SynologyCredentials _creds;
    private readonly Func<CancellationToken, Task<string?>>? _otpPrompt;
    private string _base = "";
    private string _authPath = "auth.cgi";
    private int _authVersion = 6;
    private string _fsPath = "entry.cgi";
    private int _listVersion = 2, _downloadVersion = 2, _uploadVersion = 2, _createVersion = 2, _deleteVersion = 2;
    private string _sid = "";

    public string BaseUrl => _base;

    private readonly Action<string>? _trace;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, CertificateInfo> _rejected = new();

    private SynologyClient(SynologyCredentials creds, bool allowInvalidCertificate, Func<CancellationToken, Task<string?>>? otpPrompt, Action<string>? trace)
    {
        _creds = creds;
        _otpPrompt = otpPrompt;
        _trace = trace;
        var handler = new HttpClientHandler { AllowAutoRedirect = true, UseCookies = false };
        handler.ServerCertificateCustomValidationCallback = (req, cert, chain, errors) =>
        {
            if (errors == System.Net.Security.SslPolicyErrors.None || allowInvalidCertificate) return true;
            if (cert == null) return false;
            var sha = cert.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256);
            if (_creds.PinnedCertificates.Contains(sha, StringComparer.OrdinalIgnoreCase)) return true;
            var address = req.RequestUri?.GetLeftPart(UriPartial.Authority) ?? "?";
            _rejected[address] = CertificateInfo.From(address, cert, chain, errors);
            return false;
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(30) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Unisono");
    }

    /// <summary>Connects and logs in. <paramref name="otpPrompt"/> is asked for a code when the account has two-factor on and no device token yet.</summary>
    public static async Task<SynologyClient> ConnectAsync(SynologyCredentials creds, bool allowInvalidCertificate, Func<CancellationToken, Task<string?>>? otpPrompt,
        CancellationToken ct, Action<string>? trace = null)
    {
        var c = new SynologyClient(creds, allowInvalidCertificate, otpPrompt, trace);
        try
        {
            try
            {
                await c.ResolveBaseAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && !c._rejected.IsEmpty)
            {
                // Something answered but was turned away over its certificate: let the user decide about exactly that one.
                throw new UntrustedCertificateException(c._rejected.Values.ToList(), ex);
            }
            await c.QueryApiInfoAsync(ct);
            await c.LoginAsync(ct);
            return c;
        }
        catch
        {
            c.Dispose();
            throw;
        }
    }

    /// <summary>Canonical form for a server as typed by a person: a QuickConnect ID becomes https://id.quickconnect.to, anything else scheme://host:port.</summary>
    public static string NormalizeServer(string input)
    {
        if (QuickConnect.IdFrom(input) is { } id) return $"https://{id.ToLowerInvariant()}.quickconnect.to";
        var s = input.Trim().TrimEnd('/');
        if (!s.Contains("://")) s = "https://" + s;
        return new Uri(s).GetLeftPart(UriPartial.Authority);
    }

    private async Task ResolveBaseAsync(CancellationToken ct)
    {
        // The last address that worked is tried first; at home that is often the LAN, elsewhere a DDNS name or the relay.
        if (_creds.ResolvedBase is { Length: > 0 } known && await ProbeAsync(known, ct))
        {
            _base = known;
            return;
        }
        if (QuickConnect.IdFrom(_creds.Server) is { } id)
        {
            _base = await QuickConnect.ResolveAsync(_http, id, ProbeAsync, _trace, ct);
            _trace?.Invoke($"QuickConnect '{id}' answered at {_base}");
        }
        else
        {
            var start = NormalizeServer(_creds.Server);
            if (!await ProbeAsync(start, ct))
            {
                // Say why: a web page instead of the API usually means the wrong port or a web server in front.
                string? kind = null;
                try
                {
                    using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    probeCts.CancelAfter(TimeSpan.FromSeconds(10));
                    using var res = await _http.GetAsync(start + "/webapi/query.cgi?api=SYNO.API.Info&version=1&method=query", probeCts.Token);
                    kind = $"HTTP {(int)res.StatusCode}, {res.Content.Headers.ContentType?.MediaType ?? "no content type"}";
                }
                catch (HttpRequestException) { throw; }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException($"{start} did not answer."); }
                throw new InvalidOperationException(
                    $"{start} answered ({kind}) but not as the DSM API. Use the DSM address with its HTTPS port " +
                    "(usually https://your-nas:5001) or just the QuickConnect ID.");
            }
            _base = start;
        }
        _creds.ResolvedBase = _base;
    }

    /// <summary>True when the DSM API answers with JSON at <paramref name="baseUrl"/> within a few seconds.</summary>
    private async Task<bool> ProbeAsync(string baseUrl, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(6));
            using var res = await _http.GetAsync(baseUrl + "/webapi/query.cgi?api=SYNO.API.Info&version=1&method=query&query=SYNO.API.Auth", cts.Token);
            if (!res.IsSuccessStatusCode) return false;
            var text = (await res.Content.ReadAsStringAsync(cts.Token)).TrimStart();
            return text.StartsWith('{') && text.Contains("\"success\":true");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }
        catch (HttpRequestException ex) when (ex.InnerException is System.Security.Authentication.AuthenticationException)
        {
            _trace?.Invoke($"{baseUrl}: certificate not trusted");
            return false;
        }
        catch (HttpRequestException) { return false; }
    }
    private async Task QueryApiInfoAsync(CancellationToken ct)
    {
        var doc = await CallAsync("query.cgi", new Dictionary<string, string>
        {
            ["api"] = "SYNO.API.Info", ["version"] = "1", ["method"] = "query",
            ["query"] = "SYNO.API.Auth,SYNO.FileStation.List,SYNO.FileStation.Download,SYNO.FileStation.Upload,SYNO.FileStation.CreateFolder,SYNO.FileStation.Delete",
        }, ct, withSid: false);
        var data = doc.RootElement.GetProperty("data");
        (string path, int max) Info(string api) =>
            data.TryGetProperty(api, out var e)
                ? (e.GetProperty("path").GetString() ?? "entry.cgi", e.GetProperty("maxVersion").GetInt32())
                : throw new InvalidOperationException($"{api} is not available on this DSM; is File Station enabled?");
        var auth = Info("SYNO.API.Auth");
        _authPath = auth.path; _authVersion = Math.Min(auth.max, 7);
        var list = Info("SYNO.FileStation.List");
        _fsPath = list.path; _listVersion = Math.Min(list.max, 2);
        _downloadVersion = Math.Min(Info("SYNO.FileStation.Download").max, 2);
        _uploadVersion = Math.Min(Info("SYNO.FileStation.Upload").max, 2);
        _createVersion = Math.Min(Info("SYNO.FileStation.CreateFolder").max, 2);
        _deleteVersion = Math.Min(Info("SYNO.FileStation.Delete").max, 2);
    }

    private async Task LoginAsync(CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["api"] = "SYNO.API.Auth", ["version"] = _authVersion.ToString(), ["method"] = "login",
            ["account"] = _creds.User, ["passwd"] = _creds.Password, ["session"] = "FileStation", ["format"] = "sid",
        };
        if (_creds.DeviceId != null) form["device_id"] = _creds.DeviceId;
        try
        {
            var doc = await CallAsync(_authPath, form, ct, withSid: false);
            _sid = doc.RootElement.GetProperty("data").GetProperty("sid").GetString()!;
            return;
        }
        catch (SynologyApiException ex) when (ex.Code is 403 or 404 && _otpPrompt != null)
        {
            var code = await _otpPrompt(ct);
            if (string.IsNullOrWhiteSpace(code)) throw;
            form["otp_code"] = code.Trim();
            form["enable_device_token"] = "yes";
            form["device_name"] = "Unisono on " + Environment.MachineName;
            form.Remove("device_id");
            var doc = await CallAsync(_authPath, form, ct, withSid: false);
            var data = doc.RootElement.GetProperty("data");
            _sid = data.GetProperty("sid").GetString()!;
            if (data.TryGetProperty("did", out var did)) _creds.DeviceId = did.GetString();
        }
    }

    public async Task LogoutAsync(CancellationToken ct)
    {
        if (_sid.Length == 0) return;
        try
        {
            await CallAsync(_authPath, new Dictionary<string, string>
                { ["api"] = "SYNO.API.Auth", ["version"] = _authVersion.ToString(), ["method"] = "logout", ["session"] = "FileStation" }, ct);
        }
        catch { /* best effort */ }
        _sid = "";
    }

    // ------------------------------------------------------------ operations

    /// <summary>Children of a folder. "/" lists the shared folders the user can see (File Station has a separate method for that).</summary>
    public async Task<List<SynologyEntry>> ListAsync(string folderPath, CancellationToken ct)
    {
        if (folderPath.Trim('/').Length == 0) return await ListSharesAsync(ct);
        var all = new List<SynologyEntry>();
        var offset = 0;
        while (true)
        {
            var doc = await CallAsync(_fsPath, new Dictionary<string, string>
            {
                ["api"] = "SYNO.FileStation.List", ["version"] = _listVersion.ToString(), ["method"] = "list",
                ["folder_path"] = folderPath, ["additional"] = "[\"size\",\"time\"]", ["limit"] = "1000", ["offset"] = offset.ToString(),
            }, ct);
            var data = doc.RootElement.GetProperty("data");
            var files = data.GetProperty("files");
            foreach (var f in files.EnumerateArray()) all.Add(ToEntry(f));
            var total = data.TryGetProperty("total", out var t) ? t.GetInt32() : all.Count;
            offset += files.GetArrayLength();
            if (offset >= total || files.GetArrayLength() == 0) break;
        }
        return all;
    }

    private async Task<List<SynologyEntry>> ListSharesAsync(CancellationToken ct)
    {
        var all = new List<SynologyEntry>();
        var offset = 0;
        while (true)
        {
            var doc = await CallAsync(_fsPath, new Dictionary<string, string>
            {
                ["api"] = "SYNO.FileStation.List", ["version"] = _listVersion.ToString(), ["method"] = "list_share",
                ["additional"] = "[\"time\"]", ["limit"] = "1000", ["offset"] = offset.ToString(),
            }, ct);
            var data = doc.RootElement.GetProperty("data");
            var shares = data.GetProperty("shares");
            foreach (var f in shares.EnumerateArray()) all.Add(ToEntry(f));
            var total = data.TryGetProperty("total", out var t) ? t.GetInt32() : all.Count;
            offset += shares.GetArrayLength();
            if (offset >= total || shares.GetArrayLength() == 0) break;
        }
        return all;
    }

    public async Task<SynologyEntry?> GetInfoAsync(string path, CancellationToken ct)
    {
        var doc = await CallAsync(_fsPath, new Dictionary<string, string>
        {
            ["api"] = "SYNO.FileStation.List", ["version"] = _listVersion.ToString(), ["method"] = "getinfo",
            ["path"] = JsonArray(path), ["additional"] = "[\"size\",\"time\"]",
        }, ct);
        var files = doc.RootElement.GetProperty("data").GetProperty("files");
        if (files.GetArrayLength() == 0) return null;
        var f = files[0];
        if (f.TryGetProperty("code", out var code)) { if (code.GetInt32() == 408) return null; throw new SynologyApiException("getinfo", code.GetInt32()); }
        return ToEntry(f);
    }

    public async Task DownloadAsync(string path, string localPath, DateTime mtimeUtc, CancellationToken ct)
    {
        var url = $"{_base}/webapi/{_fsPath}?api=SYNO.FileStation.Download&version={_downloadVersion}&method=download&mode=download&path={Uri.EscapeDataString(JsonArray(path))}&_sid={Uri.EscapeDataString(_sid)}";
        var tmp = localPath + LocalScanner.PartSuffix;
        try
        {
            using var res = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            res.EnsureSuccessStatusCode();
            if (res.Content.Headers.ContentType?.MediaType == "application/json")
            {
                using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
                ThrowIfError("download", doc);
            }
            await using (var fs = new ProgressStream(File.Create(tmp)))
            await using (var s = await res.Content.ReadAsStreamAsync(ct))
                await s.CopyToAsync(fs, ct);
            if (File.Exists(localPath)) File.SetAttributes(localPath, FileAttributes.Normal);
            File.Move(tmp, localPath, overwrite: true);
            File.SetLastWriteTimeUtc(localPath, mtimeUtc);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    /// <summary>Uploads with the local modified time; overwrites an existing file of the same name.</summary>
    public async Task UploadAsync(string localPath, string destFolder, string name, CancellationToken ct)
    {
        var mtimeMs = new DateTimeOffset(File.GetLastWriteTimeUtc(localPath)).ToUnixTimeMilliseconds();
        var url = $"{_base}/webapi/{_fsPath}?api=SYNO.FileStation.Upload&version={_uploadVersion}&method=upload&_sid={Uri.EscapeDataString(_sid)}";
        await using var fs = PathUtil.OpenShared(localPath);
        // Built the way DSM's own web client sends it: quoted field names, no per-field content type, and the file name
        // as plain UTF-8 (.NET's default form quotes nothing and encodes the name as filename*=, which DSM rejects).
        var boundary = "----UnisonoBoundary" + Guid.NewGuid().ToString("N");
        using var content = new MultipartFormDataContent(boundary);
        // .NET quotes the boundary in Content-Type; DSM then never finds the end of the body and fails after ~10 s.
        content.Headers.Remove("Content-Type");
        content.Headers.TryAddWithoutValidation("Content-Type", "multipart/form-data; boundary=" + boundary);
        content.HeaderEncodingSelector = (_, _) => Encoding.UTF8;
        void Field(string field, string value)
        {
            var part = new ByteArrayContent(Encoding.UTF8.GetBytes(value));
            part.Headers.TryAddWithoutValidation("Content-Disposition", $"form-data; name=\"{field}\"");
            content.Add(part);
        }
        Field("path", destFolder);
        Field("create_parents", "true");
        Field("overwrite", "true");
        Field("mtime", mtimeMs.ToString());
        var fileContent = new StreamContent(new ProgressStream(fs));
        fileContent.Headers.TryAddWithoutValidation("Content-Disposition", $"form-data; name=\"file\"; filename=\"{name.Replace("\"", "%22")}\"");
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(fileContent); // must be the last part
        using var res = await _http.PostAsync(url, content, ct);
        res.EnsureSuccessStatusCode();
        using var doc = ParseApi(await res.Content.ReadAsStringAsync(ct), "upload");
        ThrowIfError("upload", doc);
    }

    public async Task<SynologyEntry> CreateFolderAsync(string parentPath, string name, CancellationToken ct)
    {
        var doc = await CallAsync(_fsPath, new Dictionary<string, string>
        {
            ["api"] = "SYNO.FileStation.CreateFolder", ["version"] = _createVersion.ToString(), ["method"] = "create",
            // Plain values: this DSM refuses JSON arrays here with 407 (getinfo and download accept them).
            ["folder_path"] = parentPath, ["name"] = name, ["force_parent"] = "true",
        }, ct);
        var f = doc.RootElement.GetProperty("data").GetProperty("folders")[0];
        if (f.TryGetProperty("code", out var code)) throw new SynologyApiException("create folder", code.GetInt32());
        var path = f.GetProperty("path").GetString()!;
        return (await GetInfoAsync(path, ct)) ?? new SynologyEntry(name, path, true, 0, DateTime.UtcNow);
    }

    /// <summary>Goes to the shared folder's #recycle when the recycle bin is enabled in DSM; otherwise permanent.</summary>
    public async Task DeleteAsync(string path, CancellationToken ct)
    {
        await CallAsync(_fsPath, new Dictionary<string, string>
        {
            ["api"] = "SYNO.FileStation.Delete", ["version"] = _deleteVersion.ToString(), ["method"] = "delete",
            ["path"] = path, ["recursive"] = "true",
        }, ct);
    }

    // --------------------------------------------------------------- plumbing

    private static SynologyEntry ToEntry(JsonElement f)
    {
        var add = f.TryGetProperty("additional", out var a) ? a : default;
        long size = add.ValueKind == JsonValueKind.Object && add.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
        long mtime = add.ValueKind == JsonValueKind.Object && add.TryGetProperty("time", out var t) && t.TryGetProperty("mtime", out var m) ? m.GetInt64() : 0;
        return new SynologyEntry(
            f.GetProperty("name").GetString()!,
            f.GetProperty("path").GetString()!,
            f.TryGetProperty("isdir", out var isdir) ? isdir.GetBoolean() : true, // share entries may omit it
            size,
            DateTimeOffset.FromUnixTimeSeconds(mtime).UtcDateTime);
    }

    private static string JsonArray(string value) => JsonSerializer.Serialize(new[] { value });

    /// <summary>Renames within the same folder; returns the new path.</summary>
    public async Task<string> RenameAsync(string path, string newName, CancellationToken ct)
    {
        var doc = await CallAsync(_fsPath, new Dictionary<string, string>
        {
            ["api"] = "SYNO.FileStation.Rename", ["version"] = "2", ["method"] = "rename", ["path"] = path, ["name"] = newName,
        }, ct);
        var f = doc.RootElement.GetProperty("data").GetProperty("files")[0];
        if (f.TryGetProperty("code", out var code)) throw new SynologyApiException("rename", code.GetInt32());
        return f.GetProperty("path").GetString()!;
    }

    /// <summary>Moves into another folder (a background task on the NAS, polled until done); returns the new path.</summary>
    public async Task<string> MoveAsync(string path, string destFolder, CancellationToken ct)
    {
        var doc = await CallAsync(_fsPath, new Dictionary<string, string>
        {
            ["api"] = "SYNO.FileStation.CopyMove", ["version"] = "3", ["method"] = "start", ["path"] = path,
            ["dest_folder_path"] = destFolder, ["overwrite"] = "false", ["remove_src"] = "true",
        }, ct);
        var task = doc.RootElement.GetProperty("data").GetProperty("taskid").GetString()!;
        for (var i = 0; ; i++)
        {
            await Task.Delay(i == 0 ? 300 : 1000, ct);
            var st = await CallAsync(_fsPath, new Dictionary<string, string>
            {
                ["api"] = "SYNO.FileStation.CopyMove", ["version"] = "3", ["method"] = "status", ["taskid"] = task,
            }, ct);
            var data = st.RootElement.GetProperty("data");
            if (data.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object && err.TryGetProperty("code", out var ec))
                throw new SynologyApiException("move", ec.GetInt32());
            if (data.TryGetProperty("finished", out var fin) && fin.GetBoolean()) break;
            if (i > 600) throw new TimeoutException($"moving {path} did not finish");
        }
        return destFolder.TrimEnd('/') + "/" + PathUtil.NameOf(path.TrimEnd('/'));
    }

    private async Task<JsonDocument> CallAsync(string cgi, Dictionary<string, string> form, CancellationToken ct, bool withSid = true, bool retry = true)
    {
        if (withSid) form["_sid"] = _sid;
        using var res = await _http.PostAsync($"{_base}/webapi/{cgi}", new FormUrlEncodedContent(form), ct);
        res.EnsureSuccessStatusCode();
        var doc = ParseApi(await res.Content.ReadAsStringAsync(ct), cgi);
        try
        {
            ThrowIfError(form["api"], doc);
        }
        catch (SynologyApiException ex) when (retry && withSid && ex.Code is 105 or 106 or 107 or 119)
        {
            doc.Dispose();
            await LoginAsync(ct); // session expired: sign in again and retry once
            return await CallAsync(cgi, form, ct, withSid, retry: false);
        }
        return doc;
    }

    public string User => _creds.User;

    /// <summary>The address as the user entered it (a QuickConnect address works in any browser, wherever it is).</summary>
    public string Server => _creds.Server;

    /// <summary>
    /// Exports a Synology Office document (.odoc/.osheet/.oslides) to docx/xlsx/pptx via SYNO.Office.Export. It takes
    /// Synology Drive's path ("/mydrive/..."), not the File Station path; the format follows the document type.
    /// </summary>
    public async Task ExportOfficeAsync(string drivePath, string localPath, DateTime mtimeUtc, CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["api"] = "SYNO.Office.Export", ["version"] = "1", ["method"] = "download", ["path"] = drivePath, ["_sid"] = _sid,
        };
        var tmp = localPath + LocalScanner.PartSuffix;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{_base}/webapi/entry.cgi") { Content = new FormUrlEncodedContent(form) };
            using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            res.EnsureSuccessStatusCode();
            if (res.Content.Headers.ContentType?.MediaType == "application/json")
            {
                using var doc = ParseApi(await res.Content.ReadAsStringAsync(ct), "SYNO.Office.Export");
                ThrowIfError("SYNO.Office.Export", doc);
                throw new IOException("Synology Office returned no document");
            }
            await using (var fs = new ProgressStream(File.Create(tmp)))
            await using (var s = await res.Content.ReadAsStreamAsync(ct))
                await s.CopyToAsync(fs, ct);
            if (File.Exists(localPath)) File.SetAttributes(localPath, FileAttributes.Normal);
            File.Move(tmp, localPath, overwrite: true);
            File.SetLastWriteTimeUtc(localPath, mtimeUtc);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    /// <summary>A File Station sharing link (https://gofile.me/...).</summary>
    public sealed record ShareLink(string Id, string Url, string Path, int MaxOpens, bool HasPassword);

    private static ShareLink ToLink(JsonElement l) => new(
        l.GetProperty("id").GetString()!, l.GetProperty("url").GetString()!, l.GetProperty("path").GetString()!,
        l.TryGetProperty("expire_times", out var t) && t.TryGetInt32(out var times) ? times : 0,
        l.TryGetProperty("has_password", out var hp) && hp.GetBoolean());

    /// <summary>Existing sharing links for exactly this File Station path.</summary>
    public async Task<List<ShareLink>> SharingLinksAsync(string path, CancellationToken ct)
    {
        var doc = await CallAsync(_fsPath, new Dictionary<string, string>
        {
            ["api"] = "SYNO.FileStation.Sharing", ["version"] = "3", ["method"] = "list", ["offset"] = "0", ["limit"] = "1000",
        }, ct);
        return doc.RootElement.GetProperty("data").GetProperty("links").EnumerateArray().Select(ToLink)
            .Where(l => l.Path.Equals(path, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>
    /// Creates a link, optionally with a password and a maximum number of opens. The path goes as plain text (a JSON
    /// array is refused). DSM 7 accepts but ignores date_expired here (tested with create and edit), so no expiry date.
    /// </summary>
    public async Task<ShareLink> CreateSharingLinkAsync(string path, string? password, int maxOpens, CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["api"] = "SYNO.FileStation.Sharing", ["version"] = "3", ["method"] = "create", ["path"] = path,
        };
        if (!string.IsNullOrEmpty(password)) form["password"] = password;
        if (maxOpens > 0) form["expire_times"] = maxOpens.ToString();
        var doc = await CallAsync(_fsPath, form, ct);
        return ToLink(doc.RootElement.GetProperty("data").GetProperty("links")[0]);
    }

    public async Task DeleteSharingLinkAsync(string id, CancellationToken ct) =>
        await CallAsync(_fsPath, new Dictionary<string, string>
        {
            ["api"] = "SYNO.FileStation.Sharing", ["version"] = "3", ["method"] = "delete", ["id"] = id,
        }, ct);

    /// <summary>Synology Drive's permanent link id for a file (the stable part of https://nas/d/f/&lt;id&gt;).</summary>
    public async Task<string> DrivePermanentLinkAsync(string drivePath, CancellationToken ct)
    {
        var doc = await CallAsync(_fsPath, new Dictionary<string, string>
        {
            ["api"] = "SYNO.SynologyDrive.Files", ["version"] = "2", ["method"] = "get", ["path"] = drivePath,
        }, ct);
        return doc.RootElement.GetProperty("data").GetProperty("permanent_link").GetString()
               ?? throw new IOException($"Synology Drive has no link for {drivePath}");
    }

    /// <summary>Diagnostics: any API call with the session attached, raw answer returned (no error handling).</summary>
    public async Task<(string ContentType, byte[] Body)> RawAsync(string cgi, Dictionary<string, string> form, CancellationToken ct)
    {
        form["_sid"] = _sid;
        using var res = await _http.PostAsync($"{_base}/webapi/{cgi}", new FormUrlEncodedContent(form), ct);
        return (res.Content.Headers.ContentType?.ToString() ?? "", await res.Content.ReadAsByteArrayAsync(ct));
    }

    /// <summary>DSM answers JSON; a web page here means a proxy, portal or login page stood in between.</summary>
    private JsonDocument ParseApi(string text, string what)
    {
        var t = text.TrimStart();
        if (!t.StartsWith('{'))
            throw new InvalidOperationException(
                $"{_base} answered {what} with a web page instead of the DSM API. Use the DSM address with its HTTPS port (usually :5001) or the QuickConnect ID.");
        return JsonDocument.Parse(t);
    }

    private static void ThrowIfError(string api, JsonDocument doc)
    {
        if (doc.RootElement.TryGetProperty("success", out var ok) && ok.GetBoolean()) return;
        var code = doc.RootElement.TryGetProperty("error", out var e) && e.TryGetProperty("code", out var c) ? c.GetInt32() : 100;
        throw new SynologyApiException(api, code);
    }

    public void Dispose() => _http.Dispose();
}
