using Microsoft.Win32;

namespace Unisono.App;

/// <summary>
/// Explorer context-menu entries, registered per user under HKCU\Software\Classes (no admin), and shown only inside
/// synced folders thanks to an AppliesTo condition on the item's path:
///   files and folders in a Drive pair:          "Open in Google Drive"
///   the same in a NAS pair:                      "Open in Synology Drive"
///   .docx/.xlsx/.pptx in a Drive pair:           "Open in Google Docs / Sheets / Slides"
///   files and folders in a Drive / NAS pair:     "Share on Google Drive..." / "Share via Synology..."
///   the background of any folder, Shift only:    "Open this folder online (Unisono)"
/// Explorer ignores AppliesTo for background entries (tested: with a condition they never show), so that one cannot be
/// limited to synced folders; it is an extended verb (Shift+right-click) and says so when the folder is not synced.
/// Each runs "Unisono.exe --open &lt;mode&gt; &lt;path&gt;". Windows 11 lists them under "Show more options": its compact menu
/// only takes entries from packaged apps, which cannot be installed without admin.
/// </summary>
static class ExplorerMenu
{
    private const string Classes = @"Software\Classes";
    private const string DriveVerb = "Unisono.OpenGoogleDrive";
    private const string NasVerb = "Unisono.OpenSynologyDrive";
    private const string DocsVerb = "Unisono.OpenGoogleDocs";
    private const string BackgroundVerb = "Unisono.OpenFolderOnline";
    private const string DriveShareVerb = "Unisono.ShareGoogleDrive";
    private const string NasShareVerb = "Unisono.ShareSynology";
    private const string BackgroundKey = @"Directory\Background\shell";

    private static readonly (string Ext, string Label)[] OfficeTypes =
    {
        (".docx", "Open in Google Docs"), (".xlsx", "Open in Google Sheets"), (".pptx", "Open in Google Slides"),
    };

    // Where the path-conditioned verbs live ("%1" is the clicked item).
    private static readonly string[] Targets = { @"*\shell", @"Directory\shell" };

    public static void Update(Config config, Log log)
    {
        try
        {
            Remove();
            if (!config.Options.ExplorerMenu) return;
            var exe = Environment.ProcessPath ?? throw new InvalidOperationException("unknown exe path");
            var pairs = config.ActivePairs.Where(p => p.LocalPath.Length > 0).ToList();

            var drive = AppliesTo(pairs.Where(p => p.Kind == RemoteKind.Drive));
            var nas = AppliesTo(pairs.Where(p => p.Kind == RemoteKind.Synology));
            foreach (var key in Targets)
            {
                if (drive != null) Add($@"{key}\{DriveVerb}", "Open in Google Drive", drive, $"\"{exe}\" --open drive \"%1\"", exe);
                if (nas != null) Add($@"{key}\{NasVerb}", "Open in Synology Drive", nas, $"\"{exe}\" --open nas \"%1\"", exe);
                if (drive != null) Add($@"{key}\{DriveShareVerb}", "Share on Google Drive...", drive, $"\"{exe}\" --share drive \"%1\"", exe);
                if (nas != null) Add($@"{key}\{NasShareVerb}", "Share via Synology...", nas, $"\"{exe}\" --share nas \"%1\"", exe);
            }
            if (drive != null || nas != null)
            {
                using var bg = Registry.CurrentUser.CreateSubKey($@"{Classes}\{BackgroundKey}\{BackgroundVerb}");
                bg.SetValue("MUIVerb", "Open this folder online (Unisono)");
                bg.SetValue("Icon", $"\"{exe}\",0");
                bg.SetValue("Extended", ""); // only with Shift+right-click
                using var cmd = bg.CreateSubKey("command");
                cmd.SetValue("", $"\"{exe}\" --open auto \"%V\"");
            }
            if (drive != null)
                foreach (var (ext, label) in OfficeTypes)
                    Add($@"SystemFileAssociations\{ext}\shell\{DocsVerb}", label, drive, $"\"{exe}\" --open docs \"%1\"", exe);
        }
        catch (Exception ex)
        {
            log.Warn("could not update the Explorer menu: " + ex.Message);
        }
    }

    /// <summary>
    /// An Advanced Query Syntax condition matching the pair folders themselves and everything below them.
    /// Null when there are no such folders (then the verb is not registered at all).
    /// </summary>
    private static string? AppliesTo(IEnumerable<SyncPair> pairs)
    {
        var parts = pairs.Select(p => p.LocalFullPath.TrimEnd('\\'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(p => $"System.ItemPathDisplay:=\"{p}\" OR System.ItemPathDisplay:~<\"{p}\\\"")
            .ToList();
        return parts.Count == 0 ? null : string.Join(" OR ", parts);
    }

    private static void Add(string subKey, string label, string appliesTo, string command, string exe)
    {
        using var key = Registry.CurrentUser.CreateSubKey($@"{Classes}\{subKey}");
        key.SetValue("MUIVerb", label);
        key.SetValue("Icon", $"\"{exe}\",0");
        key.SetValue("AppliesTo", appliesTo);
        using var cmd = key.CreateSubKey("command");
        cmd.SetValue("", command);
    }

    public static void Remove()
    {
        foreach (var key in Targets.Append(BackgroundKey))
            foreach (var verb in new[] { DriveVerb, NasVerb, BackgroundVerb, DriveShareVerb, NasShareVerb })
                Registry.CurrentUser.DeleteSubKeyTree($@"{Classes}\{key}\{verb}", throwOnMissingSubKey: false);
        foreach (var (ext, _) in OfficeTypes)
            Registry.CurrentUser.DeleteSubKeyTree($@"{Classes}\SystemFileAssociations\{ext}\shell\{DocsVerb}", throwOnMissingSubKey: false);
    }
}
