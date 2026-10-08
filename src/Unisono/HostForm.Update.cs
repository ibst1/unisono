using System.Diagnostics;
using System.Text.Json;

namespace Unisono.App;

// A new Unisono.exe where this one was started from - brought by Unisono's own sync (the running file moved aside), by
// OneDrive or by a build - is noticed here. With AutoUpdate (the default) Unisono restarts into it by itself a couple
// of minutes later, once the window is closed and nothing is being transferred; otherwise, and meanwhile, the tray
// menu offers to restart now and a balloon says so, once per version. Restarting starts the new file with --after,
// which waits for this process to be gone (one tray app only), and --updated, which tells the user it happened.
// Every computer also writes its version next to the exe (machines\<computer>.json): where the install folder is
// synced between computers, the window shows which version each runs.
sealed partial class HostForm
{
    private static readonly string? ExePath = Environment.ProcessPath; // read at start, before any rename
    private readonly (long Size, DateTime Written)? _exeAtStart = ExeStamp();
    private readonly System.Windows.Forms.Timer _updateTimer = new() { Interval = 15_000 };
    private readonly ToolStripMenuItem _trayRestart = new() { Visible = false, Font = new Font(SystemFonts.MenuFont ?? SystemFonts.DefaultFont, FontStyle.Bold) };
    private readonly ToolStripSeparator _trayRestartSep = new() { Visible = false };
    private string? _updateVersion;
    private (long, DateTime)? _lastLooked;
    private DateTime _lastCheckedExe = DateTime.MinValue;
    /// <summary>When an automatic restart into the new version is due (it still waits for an idle moment).</summary>
    private DateTime? _autoRestartAt;
    private static readonly TimeSpan AutoRestartDelay = TimeSpan.FromSeconds(int.TryParse(Environment.GetEnvironmentVariable("UNISONO_AUTOUPDATE_SECONDS"), out var s) && s >= 0 ? s : 120);
    private DateTime _lastSeenWritten = DateTime.MinValue;

    private static (long, DateTime)? ExeStamp()
    {
        try { return ExePath is { } p && new FileInfo(p) is { Exists: true } f ? (f.Length, f.LastWriteTimeUtc) : null; }
        catch { return null; }
    }

    /// <summary>Adds the (hidden) restart entry at the top of the tray menu and starts looking for a new version.</summary>
    private void SetUpUpdateCheck(ContextMenuStrip menu)
    {
        _trayRestart.Click += (_, _) => RestartIntoUpdate();
        menu.Items.Insert(0, _trayRestartSep);
        menu.Items.Insert(0, _trayRestart);
        _updateTimer.Tick += (_, _) =>
        {
            if (DateTime.UtcNow - _lastCheckedExe > TimeSpan.FromMinutes(1)) { _lastCheckedExe = DateTime.UtcNow; CheckForUpdate(); }
            MaybeRestartByItself();
            WriteMachineVersion();
        };
        _updateTimer.Start();
    }

    private void CheckForUpdate()
    {
        if (ExePath == null || _exeAtStart == null) return;
        var now = ExeStamp();
        if (now == null || now == _exeAtStart || now == _lastLooked) return;
        // Still being written (a download or copy in progress): look again next time.
        if (DateTime.UtcNow - now.Value.Item2 < TimeSpan.FromSeconds(10)) return;
        _lastLooked = now;
        // Asked for by this path, Windows answers from the image this process runs, i.e. the old version: read a copy.
        var copy = Path.Combine(Path.GetTempPath(), $"Unisono-version-{Environment.ProcessId}.exe");
        string version;
        try
        {
            File.Copy(ExePath, copy, overwrite: true);
            version = FileVersionInfo.GetVersionInfo(copy).ProductVersion ?? "";
        }
        catch (Exception ex) { _lastLooked = null; _log.Debug($"could not read the version of {ExePath}: {ex.Message}"); return; } // next time
        finally { try { File.Delete(copy); } catch { } }
        if (version.Length == 0 || version == Ui.Version || version == _updateVersion) return;
        _updateVersion = version;
        var shortVersion = version.Split('+')[0];
        _trayRestart.Text = $"Restart to update to {shortVersion}";
        _trayRestart.Visible = _trayRestartSep.Visible = true;
        var auto = _bridge.Config.Options.AutoUpdate;
        _autoRestartAt = auto ? DateTime.UtcNow + AutoRestartDelay : null;
        _log.Info($"a new Unisono.exe is in place ({version}; running {Ui.Version}); " + (auto ? "restarting into it by itself once idle" : "restart to use it"));
        _tray.ShowBalloonTip(15000, "Unisono: new version", auto
            ? $"Unisono {shortVersion} has arrived. Unisono restarts into it by itself in a moment, when its window is closed and nothing is being transferred (or now: \"{_trayRestart.Text}\" in the tray menu)."
            : $"Unisono {shortVersion} has arrived. Choose \"{_trayRestart.Text}\" in the tray menu.", ToolTipIcon.Info);
    }

    /// <summary>The automatic restart: when due, with the window closed (nobody working in it) and no transfer running.</summary>
    private void MaybeRestartByItself()
    {
        if (_autoRestartAt is not { } due || DateTime.UtcNow < due) return;
        if (!_bridge.Config.Options.AutoUpdate) { _autoRestartAt = null; return; }
        if (Visible || SyncActivity.Now().Active.Count > 0) return; // next tick
        _autoRestartAt = null;
        RestartIntoUpdate();
    }

    private void RestartIntoUpdate()
    {
        if (ExePath == null) return;
        try
        {
            Process.Start(new ProcessStartInfo(ExePath) { UseShellExecute = false, ArgumentList = { "--after", Environment.ProcessId.ToString(), "--tray", "--updated", Ui.Version } });
        }
        catch (Exception ex)
        {
            _log.Error($"could not start the new Unisono.exe: {ex.Message}");
            return;
        }
        _log.Info($"restarting into {_updateVersion}");
        _exiting = true;
        Close();
    }

    /// <summary>After a restart into a new version (--updated &lt;old&gt;): says so, once.</summary>
    private void TellIfUpdated(string[] args)
    {
        var i = Array.IndexOf(args, "--updated");
        if (i < 0 || i + 1 >= args.Length || args[i + 1] == Ui.Version) return;
        _log.Info($"updated from {args[i + 1]} to {Ui.Version}");
        _tray.ShowBalloonTip(8000, "Unisono updated", $"Unisono runs {Ui.Version.Split('+')[0]} now (was {args[i + 1].Split('+')[0]}).", ToolTipIcon.Info);
    }

    // ------------------------------------------------------------- versions per computer

    /// <summary>One computer's entry next to the exe (machines\&lt;computer&gt;.json).</summary>
    public sealed record MachineVersion(string Machine, string Version, DateTime Started, DateTime Seen);

    private static string? MachinesDir => ExePath == null ? null : Path.Combine(Path.GetDirectoryName(ExePath)!, "machines");
    private static readonly DateTime StartedUtc = DateTime.UtcNow;

    /// <summary>
    /// This computer's version and when it was last seen running: at start and then hourly (rarely enough not to be a
    /// file that "changes very often" for the pair that syncs the install folder).
    /// </summary>
    private void WriteMachineVersion()
    {
        if (DateTime.UtcNow - _lastSeenWritten < TimeSpan.FromHours(1) || MachinesDir is not { } dir) return;
        _lastSeenWritten = DateTime.UtcNow;
        try
        {
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, Environment.MachineName + ".json");
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(new MachineVersion(Environment.MachineName, Ui.Version, StartedUtc, DateTime.UtcNow)));
            File.Move(path + ".tmp", path, overwrite: true);
        }
        catch (Exception ex) { _log.Debug("could not note this computer's version: " + ex.Message); }
    }

    /// <summary>Every computer's entry, this one's first.</summary>
    public static List<MachineVersion> MachineVersions()
    {
        var list = new List<MachineVersion>();
        if (MachinesDir is not { } dir || !Directory.Exists(dir)) return list;
        foreach (var f in Directory.EnumerateFiles(dir, "*.json"))
        {
            try { if (JsonSerializer.Deserialize<MachineVersion>(File.ReadAllText(f)) is { } m) list.Add(m); }
            catch { /* half-synced or damaged: skip */ }
        }
        return list.OrderByDescending(m => m.Machine == Environment.MachineName).ThenBy(m => m.Machine).ToList();
    }

    /// <summary>--after &lt;pid&gt;: a restart; wait (up to a minute) until the Unisono that started this one has exited.</summary>
    public static void WaitForPrevious(string[] args)
    {
        var i = Array.IndexOf(args, "--after");
        if (i < 0 || i + 1 >= args.Length || !int.TryParse(args[i + 1], out var pid)) return;
        try { using var p = Process.GetProcessById(pid); p.WaitForExit(60_000); }
        catch { /* already gone */ }
    }
}
