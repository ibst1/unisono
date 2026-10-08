namespace Unisono.App;

static class Program
{
    /// <summary>Unisono.exe [--tray] [--after pid] [--updated old-version]: the tray app. --tray starts with the window hidden
    /// (used by autostart); --after waits for that process (the version this one replaces) to exit first; --updated says
    /// in a balloon that this version replaced that one.</summary>
    [STAThread]
    static int Main(string[] args)
    {
        CloudFiles.ExposePlaceholders();
        // Explorer's context menu: open the item online and exit, without touching the tray app.
        if (args.Length >= 3 && args[0] == "--open")
        {
            ApplicationConfiguration.Initialize();
            return OpenOnline.Run(args[1], args[2]);
        }
        if (args.Length >= 2 && args[0] == "--ignore")
        {
            ApplicationConfiguration.Initialize();
            return DontSync.Run(args[1]);
        }
        if (args.Length >= 3 && args[0] == "--share")
        {
            ApplicationConfiguration.Initialize();
            return ShareForm.Run(args[1], args[2]);
        }
        // A restart into a new version: the old one is still closing.
        HostForm.WaitForPrevious(args);
        // One tray app per data dir. A second start just asks the running one to show its window.
        using var show = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Unisono.Show." + SyncLock.Suffix, out var created);
        if (!created)
        {
            show.Set();
            return 0;
        }
        AppPaths.EnsureDataDir();
        ApplicationConfiguration.Initialize();
        Application.Run(new HostForm(startHidden: args.Contains("--tray", StringComparer.OrdinalIgnoreCase), show, args));
        return 0;
    }
}
