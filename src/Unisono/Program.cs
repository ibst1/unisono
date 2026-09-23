namespace Unisono.App;

static class Program
{
    /// <summary>Unisono.exe [--tray]: the tray app. --tray starts with the window hidden (used by autostart).</summary>
    [STAThread]
    static void Main(string[] args)
    {
        // One tray app per data dir. A second start just asks the running one to show its window.
        using var show = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Unisono.Show." + SyncLock.Suffix, out var created);
        if (!created)
        {
            show.Set();
            return;
        }
        AppPaths.EnsureDataDir();
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(startHidden: args.Contains("--tray", StringComparer.OrdinalIgnoreCase), show));
    }
}
