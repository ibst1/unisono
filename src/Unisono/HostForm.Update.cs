using System.Diagnostics;

namespace Unisono.App;

// A new Unisono.exe where this one was started from - brought by Unisono's own sync (the running file moved aside), by
// OneDrive or by a build - is noticed here: the tray menu offers to restart into it and a balloon says so, once per
// version. Restarting starts the new file with --after, which waits for this process to be gone (one tray app only).
sealed partial class HostForm
{
    private static readonly string? ExePath = Environment.ProcessPath; // read at start, before any rename
    private readonly (long Size, DateTime Written)? _exeAtStart = ExeStamp();
    private readonly System.Windows.Forms.Timer _updateTimer = new() { Interval = 60_000 };
    private readonly ToolStripMenuItem _trayRestart = new() { Visible = false, Font = new Font(SystemFonts.MenuFont ?? SystemFonts.DefaultFont, FontStyle.Bold) };
    private readonly ToolStripSeparator _trayRestartSep = new() { Visible = false };
    private string? _updateVersion;
    private (long, DateTime)? _lastLooked;

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
        _updateTimer.Tick += (_, _) => CheckForUpdate();
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
        _log.Info($"a new Unisono.exe is in place ({version}; running {Ui.Version}); restart to use it");
        _tray.ShowBalloonTip(15000, "Unisono: new version", $"Unisono {shortVersion} has arrived. Choose \"{_trayRestart.Text}\" in the tray menu.", ToolTipIcon.Info);
    }

    private void RestartIntoUpdate()
    {
        if (ExePath == null) return;
        try
        {
            Process.Start(new ProcessStartInfo(ExePath) { UseShellExecute = false, ArgumentList = { "--after", Environment.ProcessId.ToString(), "--tray" } });
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

    /// <summary>--after &lt;pid&gt;: a restart; wait (up to a minute) until the Unisono that started this one has exited.</summary>
    public static void WaitForPrevious(string[] args)
    {
        var i = Array.IndexOf(args, "--after");
        if (i < 0 || i + 1 >= args.Length || !int.TryParse(args[i + 1], out var pid)) return;
        try { using var p = Process.GetProcessById(pid); p.WaitForExit(60_000); }
        catch { /* already gone */ }
    }
}
