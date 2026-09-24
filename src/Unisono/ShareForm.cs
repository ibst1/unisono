using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Unisono.App;

/// <summary>
/// "Unisono.exe --share &lt;drive|nas&gt; &lt;path&gt;" from Explorer: a small window (ui/share.html) to share the remote item
/// behind a local file or folder. Google Drive: people with viewer/commenter/editor access, general access (restricted,
/// anyone with the link, the organisation), copy link. NAS: File Station sharing links (gofile.me) with optional password
/// and a limit on the number of opens.
/// </summary>
sealed class ShareForm : Form
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly LocatedItem _item;
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private DriveClient? _drive;
    private SynologyClient? _nas;

    public static int Run(string kind, string path)
    {
        LocatedItem item;
        try { item = ItemLocator.Locate(kind, path); }
        catch (Exception ex)
        {
            MessageBox.Show(Ui.Explain(ex), "Unisono", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 1;
        }
        Application.Run(new ShareForm(item));
        return 0;
    }

    private ShareForm(LocatedItem item)
    {
        _item = item;
        Text = $"Share \"{item.Name}\"";
        Size = new Size(640, 720);
        MinimumSize = new Size(480, 420);
        StartPosition = FormStartPosition.CenterScreen;
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { }
        BackColor = Color.FromArgb(32, 32, 32);
        Controls.Add(_web);
        Shown += async (_, _) => await InitAsync();
        FormClosed += (_, _) =>
        {
            if (_nas != null) { try { _nas.LogoutAsync(CancellationToken.None).Wait(3000); } catch { } _nas.Dispose(); }
        };
    }

    private async Task InitAsync()
    {
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(AppPaths.DataDir, "WebView2"));
            await _web.EnsureCoreWebView2Async(env);
            var core = _web.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = Environment.GetEnvironmentVariable("UNISONO_DEVTOOLS") == "1";
            core.Settings.AreDevToolsEnabled = Environment.GetEnvironmentVariable("UNISONO_DEVTOOLS") == "1";
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.NavigationStarting += (_, e) => { if (e.Uri.StartsWith("http", StringComparison.OrdinalIgnoreCase)) { e.Cancel = true; OpenExternal(e.Uri); } };
            core.NewWindowRequested += (_, e) => { e.Handled = true; OpenExternal(e.Uri); };
            core.WebMessageReceived += async (_, e) => await OnMessageAsync(e.WebMessageAsJson);
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Unisono.ui.share.html")!;
            using var reader = new StreamReader(stream);
            _web.NavigateToString(await reader.ReadToEndAsync());
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "The share window could not start: " + ex.Message, "Unisono", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            Close();
        }
    }

    private static void OpenExternal(string uri)
    {
        if (uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); } catch { }
    }

    private async Task OnMessageAsync(string json)
    {
        string? id = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            id = root.TryGetProperty("id", out var i) ? i.ToString() : null;
            var method = root.GetProperty("method").GetString()!;
            var p = root.TryGetProperty("params", out var pp) ? pp.Clone() : default;
            var result = await HandleAsync(method, p);
            Post(new { id, ok = true, result });
        }
        catch (Exception ex)
        {
            Post(new { id, ok = false, error = Explain(ex) });
        }
    }

    private void Post(object message)
    {
        try { _web.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(message, Json)); } catch { }
    }

    private static string Explain(Exception ex) => ex is Google.GoogleApiException g && g.Error?.Message is { Length: > 0 } m ? m : Ui.Explain(ex);

    private async Task<object?> HandleAsync(string method, JsonElement p)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;
        switch (method)
        {
            case "info": return _item.Pair.Kind == RemoteKind.Synology ? await NasInfoAsync(ct) : await DriveInfoAsync(ct);
            case "copy": Clipboard.SetText(Str(p, "text") ?? ""); return null;
            case "open": OpenExternal(Str(p, "url") ?? ""); return null;
            case "close": Close(); return null;

            case "drive.add":
            {
                var drive = await DriveAsync(ct);
                var role = Role(Str(p, "role"));
                var emails = (Str(p, "emails") ?? "").Split(new[] { ',', ';', ' ', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (emails.Length == 0) throw new InvalidOperationException("Enter at least one e-mail address.");
                var notify = p.TryGetProperty("notify", out var n) && n.GetBoolean();
                foreach (var email in emails)
                    await drive.CreatePermissionAsync(_item.RemoteId, "user", role, email, null, notify, Str(p, "message"), ct);
                return await DriveInfoAsync(ct);
            }
            case "drive.role":
            {
                var drive = await DriveAsync(ct);
                var permId = Str(p, "permissionId")!;
                if (Str(p, "role") == "remove") await drive.DeletePermissionAsync(_item.RemoteId, permId, ct);
                else await drive.UpdatePermissionAsync(_item.RemoteId, permId, Role(Str(p, "role")), ct);
                return await DriveInfoAsync(ct);
            }
            case "drive.general":
            {
                // One general-access permission at a time: remove the current one, then add the chosen one.
                var drive = await DriveAsync(ct);
                foreach (var perm in (await drive.ListPermissionsAsync(_item.RemoteId, ct)).Where(x => x.Type is "anyone" or "domain"))
                    await drive.DeletePermissionAsync(_item.RemoteId, perm.Id, ct);
                var mode = Str(p, "mode");
                var role = Role(Str(p, "role"));
                if (mode == "anyone") await drive.CreatePermissionAsync(_item.RemoteId, "anyone", role, null, null, false, null, ct);
                else if (mode == "domain") await drive.CreatePermissionAsync(_item.RemoteId, "domain", role, null, AccountDomain(), false, null, ct);
                return await DriveInfoAsync(ct);
            }

            case "nas.create":
            {
                var nas = await NasAsync(ct);
                var maxOpens = p.TryGetProperty("maxOpens", out var mo) && mo.TryGetInt32(out var m) ? Math.Max(0, m) : 0;
                await nas.CreateSharingLinkAsync(_item.RemoteId, Str(p, "password"), maxOpens, ct);
                return await NasInfoAsync(ct);
            }
            case "nas.delete":
            {
                var nas = await NasAsync(ct);
                await nas.DeleteSharingLinkAsync(Str(p, "id")!, ct);
                return await NasInfoAsync(ct);
            }
            default: throw new InvalidOperationException("unknown request " + method);
        }
    }

    private static string Role(string? r) => r is "reader" or "commenter" or "writer" ? r : "reader";

    // ------------------------------------------------------------------ Drive

    private async Task<DriveClient> DriveAsync(CancellationToken ct)
    {
        if (_drive != null) return _drive;
        var acct = GoogleAccounts.Resolve(_item.Pair.Account) ?? throw new InvalidOperationException("The pair's Google account is not signed in on this computer.");
        if (!DriveClient.HasStoredToken(acct.Id)) throw new InvalidOperationException($"{acct.Email ?? acct.Id} needs to sign in again (Unisono, Accounts).");
        return _drive = await DriveClient.ConnectAsync(acct.Id, ct);
    }

    private string? AccountEmail() => GoogleAccounts.Resolve(_item.Pair.Account)?.Email;

    /// <summary>A Google Workspace domain can be shared with as a whole; a consumer Gmail address has none.</summary>
    private string? AccountDomain()
    {
        var email = AccountEmail();
        var at = email?.IndexOf('@') ?? -1;
        if (at < 0) return null;
        var domain = email![(at + 1)..].ToLowerInvariant();
        return domain is "gmail.com" or "googlemail.com" ? null : domain;
    }

    private async Task<object> DriveInfoAsync(CancellationToken ct)
    {
        var drive = await DriveAsync(ct);
        var file = await drive.GetShareInfoAsync(_item.RemoteId, ct);
        var perms = await drive.ListPermissionsAsync(_item.RemoteId, ct);
        var me = AccountEmail();
        var general = perms.FirstOrDefault(x => x.Type == "anyone") ?? perms.FirstOrDefault(x => x.Type == "domain");
        return new
        {
            kind = "drive",
            name = file.Name,
            isFolder = file.MimeType == DriveClient.FolderMime,
            isDerived = _item.IsDerived,
            canShare = file.Capabilities?.CanShare ?? false,
            link = OpenOnline.WithAccount(_item.Pair, file.WebViewLink ?? $"https://drive.google.com/open?id={file.Id}"),
            account = me,
            domain = AccountDomain(),
            general = new
            {
                mode = general?.Type switch { "anyone" => "anyone", "domain" => "domain", _ => "restricted" },
                role = general?.Role ?? "reader",
                domain = general?.Domain,
            },
            people = perms.Where(x => x.Type is "user" or "group" && x.Deleted != true)
                .OrderBy(x => x.Role == "owner" ? 0 : 1).ThenBy(x => x.EmailAddress)
                .Select(x => new
                {
                    id = x.Id, email = x.EmailAddress, name = x.DisplayName, role = x.Role, type = x.Type,
                    isMe = me != null && string.Equals(x.EmailAddress, me, StringComparison.OrdinalIgnoreCase),
                }),
        };
    }

    // -------------------------------------------------------------------- NAS

    private async Task<SynologyClient> NasAsync(CancellationToken ct) =>
        _nas ??= await OpenOnline.ConnectNasAsync(_item.Pair, ct);

    private async Task<object> NasInfoAsync(CancellationToken ct)
    {
        var nas = await NasAsync(ct);
        var links = await nas.SharingLinksAsync(_item.RemoteId, ct);
        return new
        {
            kind = "nas",
            name = _item.Name,
            isFolder = _item.IsDir,
            path = _item.RemoteId,
            links = links.Select(l => new { id = l.Id, url = l.Url, maxOpens = l.MaxOpens, hasPassword = l.HasPassword }),
        };
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
