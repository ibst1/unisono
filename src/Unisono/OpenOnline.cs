using System.Diagnostics;

namespace Unisono.App;

/// <summary>
/// "Unisono.exe --open &lt;drive|nas|docs|auto&gt; &lt;path&gt;" from Explorer's context menu: opens the remote item behind a
/// local file or folder in the browser. Runs as its own short-lived process; it never starts the tray app.
/// </summary>
static class OpenOnline
{
    public static int Run(string mode, string path)
    {
        try
        {
            var url = Task.Run(() => UrlForAsync(mode, path)).GetAwaiter().GetResult();
            // Tests: write the address instead of opening a browser.
            if (Environment.GetEnvironmentVariable("UNISONO_OPEN_ECHO") is { Length: > 0 } echo) { File.WriteAllText(echo, url); return 0; }
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return 0;
        }
        catch (Exception ex)
        {
            MessageBox.Show(Ui.Explain(ex), "Unisono", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 1;
        }
    }

    private static async Task<string> UrlForAsync(string mode, string path)
    {
        var item = ItemLocator.Locate(mode, path);
        return item.Pair.Kind == RemoteKind.Synology
            ? await NasUrlAsync(item)
            : DriveUrl(item, mode == "docs");
    }

    /// <summary>Adds the pair's account, so the browser opens the right one when several are signed in.</summary>
    public static string WithAccount(SyncPair pair, string url)
    {
        var account = GoogleAccounts.Resolve(pair.Account)?.Email;
        return account == null ? url : url + (url.Contains('?') ? "&" : "?") + "authuser=" + Uri.EscapeDataString(account);
    }

    private static string DriveUrl(LocatedItem item, bool docs)
    {
        var id = item.RemoteId;
        if (item.IsDir) return WithAccount(item.Pair, $"https://drive.google.com/drive/folders/{id}");
        var editor = EditorFor(item.Rel);
        if (item.IsDerived)
            // An export stands for a Google Doc: open the original in its editor.
            return WithAccount(item.Pair, editor != null ? $"https://docs.google.com/{editor}/d/{id}/edit" : $"https://drive.google.com/open?id={id}");
        // An Office file stored on Drive opens in the Docs editors in Office-compatibility mode.
        if (docs && editor != null) return WithAccount(item.Pair, $"https://docs.google.com/{editor}/d/{id}/edit?rtpof=true");
        return WithAccount(item.Pair, $"https://drive.google.com/file/d/{id}/view");
    }

    private static string? EditorFor(string rel) => Path.GetExtension(rel).ToLowerInvariant() switch
    {
        ".docx" or ".doc" => "document",
        ".xlsx" or ".xls" => "spreadsheets",
        ".pptx" or ".ppt" => "presentation",
        _ => null,
    };

    private static async Task<string> NasUrlAsync(LocatedItem item)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var nas = await ConnectNasAsync(item.Pair, cts.Token);
        try
        {
            var link = await nas.DrivePermanentLinkAsync(SynologyStore.DrivePathFor(item.RemoteId, item.Pair.User!), cts.Token);
            return $"{item.Pair.Server}/d/f/{link}";
        }
        finally
        {
            await nas.LogoutAsync(CancellationToken.None);
        }
    }

    public static async Task<SynologyClient> ConnectNasAsync(SyncPair pair, CancellationToken ct)
    {
        var creds = SynologyCredentials.Load(pair.Server!, pair.User)
                    ?? throw new InvalidOperationException($"No stored NAS login for {pair.User} @ {pair.Server}.");
        return await SynologyClient.ConnectAsync(creds, creds.AllowInvalidCertificate, null, ct);
    }
}
