using System.Diagnostics;

namespace Unisono.App;

/// <summary>
/// "Unisono.exe --open &lt;drive|nas|docs&gt; &lt;path&gt;" from Explorer's context menu: finds the pair and the remote item
/// behind a local file or folder (from the pair's sync state) and opens it in the browser. Runs as its own short-lived
/// process; it never starts the tray app.
/// </summary>
static class OpenOnline
{
    public static int Run(string mode, string path)
    {
        try
        {
            var url = Task.Run(() => UrlForAsync(mode, Path.GetFullPath(path.Trim().TrimEnd('\\')))).GetAwaiter().GetResult();
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
        var config = Config.Load();
        var wantNas = mode == "nas";
        // The innermost pair of the right kind ("auto": of any kind) that contains the path (pairs can nest).
        var pair = config.ActivePairs
            .Where(p => (mode == "auto" || (p.Kind == RemoteKind.Synology) == wantNas) && p.LocalPath.Length > 0)
            .Where(p => IsUnder(path, p.LocalFullPath))
            .OrderByDescending(p => p.LocalFullPath.Length)
            .FirstOrDefault()
            ?? throw new InvalidOperationException(mode == "auto"
                ? $"{path} is not inside a folder Unisono syncs."
                : $"{path} is not inside a folder Unisono syncs with {(wantNas ? "a NAS" : "Google Drive")}.");

        var rel = Path.GetRelativePath(pair.LocalFullPath, path).Replace('\\', '/');
        if (rel == ".") rel = "";
        var isDir = Directory.Exists(path);
        var state = SyncState.Load(pair);

        return pair.Kind == RemoteKind.Synology
            ? await NasUrlAsync(pair, rel, state)
            : DriveUrl(pair, rel, isDir, state, mode == "docs");
    }

    private static string DriveUrl(SyncPair pair, string rel, bool isDir, SyncState state, bool docs)
    {
        var account = GoogleAccounts.Resolve(pair.Account)?.Email;
        string Auth(string url) => account == null ? url : url + (url.Contains('?') ? "&" : "?") + "authuser=" + Uri.EscapeDataString(account);

        if (rel.Length == 0)
            return Auth($"https://drive.google.com/drive/folders/{state.RootId ?? throw NotYet(rel)}");

        if (state.Entries.TryGetValue(rel, out var e) && e.Kind is EntryKind.File or EntryKind.Folder)
        {
            if (e.Kind == EntryKind.Folder || isDir) return Auth($"https://drive.google.com/drive/folders/{e.DriveId}");
            // An Office file stored on Drive opens in the Docs editors in Office-compatibility mode.
            if (docs && EditorFor(rel) is { } editor) return Auth($"https://docs.google.com/{editor}/d/{e.DriveId}/edit?rtpof=true");
            return Auth($"https://drive.google.com/file/d/{e.DriveId}/view");
        }
        // An export (Plan.docx) or link stands for a Google Doc: open the original.
        var derived = state.Entries.Values.FirstOrDefault(x => x.LocalName != null && x.LocalName.Equals(rel, StringComparison.OrdinalIgnoreCase));
        if (derived != null)
            return Auth(EditorFor(rel) is { } ed && derived.Kind == EntryKind.Export
                ? $"https://docs.google.com/{ed}/d/{derived.DriveId}/edit"
                : $"https://drive.google.com/open?id={derived.DriveId}");
        throw NotYet(rel);
    }

    private static string? EditorFor(string rel) => Path.GetExtension(rel).ToLowerInvariant() switch
    {
        ".docx" or ".doc" => "document",
        ".xlsx" or ".xls" => "spreadsheets",
        ".pptx" or ".ppt" => "presentation",
        _ => null,
    };

    private static async Task<string> NasUrlAsync(SyncPair pair, string rel, SyncState state)
    {
        string fsPath;
        if (rel.Length == 0) fsPath = pair.RemotePath;
        else if (state.Entries.TryGetValue(rel, out var e) && e.Kind is EntryKind.File or EntryKind.Folder) fsPath = e.DriveId;
        else if (state.Entries.Values.FirstOrDefault(x => x.LocalName != null && x.LocalName.Equals(rel, StringComparison.OrdinalIgnoreCase)) is { } d) fsPath = d.DriveId;
        else throw NotYet(rel);

        var creds = SynologyCredentials.Load(pair.Server!, pair.User)
                    ?? throw new InvalidOperationException($"No stored NAS login for {pair.User} @ {pair.Server}.");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var nas = await SynologyClient.ConnectAsync(creds, creds.AllowInvalidCertificate, null, cts.Token);
        try
        {
            var link = await nas.DrivePermanentLinkAsync(SynologyStore.DrivePathFor(fsPath, creds.User), cts.Token);
            return $"{creds.Server}/d/f/{link}";
        }
        finally
        {
            await nas.LogoutAsync(CancellationToken.None);
        }
    }

    private static Exception NotYet(string rel) =>
        new InvalidOperationException($"'{(rel.Length == 0 ? "This folder" : rel)}' has not been synced yet, so there is nothing online to open. Try again after the next sync.");

    private static bool IsUnder(string path, string folder)
    {
        var f = folder.TrimEnd('\\');
        return path.Equals(f, StringComparison.OrdinalIgnoreCase) || path.StartsWith(f + "\\", StringComparison.OrdinalIgnoreCase);
    }
}
