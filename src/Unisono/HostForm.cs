using System.Reflection;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Unisono.App;

/// <summary>
/// The Unisono window: a WebView2 page (ui/index.html) for everything visible, plus what must stay native: the tray
/// icon, the sync host, window placement and dialogs. Closing the window hides it; syncing continues until Exit.
/// </summary>
sealed class HostForm : Form
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly bool _startHidden;
    private bool _allowShow;
    private bool _exiting;
    private bool _toldAboutTray;
    private readonly Log _log = new() { ToConsole = false };
    private readonly SyncHost _host;
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly Bridge _bridge;
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private readonly NotifyIcon _tray = new();
    private readonly ToolStripMenuItem _trayPause = new("Pause");
    private readonly Dictionary<Color, Icon> _icons = new();
    private readonly List<object> _history = new();
    private readonly System.Windows.Forms.Timer _stateTimer = new() { Interval = 250 };
    private bool _webReady;
    private FileSystemWatcher? _sharedWatcher;
    private readonly System.Windows.Forms.Timer _reloadTimer = new() { Interval = 3000 };

    public HostForm(bool startHidden, EventWaitHandle showSignal)
    {
        _startHidden = startHidden;
        _log.Verbose = _settings.VerboseLog;
        _host = new SyncHost(_log);
        _bridge = new Bridge(this, _log, _host, _settings, StartHostAsync, QueueState);
        _host.AutoPauseReason = () => AutoPause.Reason(_bridge.Config.Options);

        Text = "Unisono";
        Icon = IconFor(Ui.Grey);
        MinimumSize = new Size(760, 520);
        ApplySavedBounds();
        BackColor = Color.FromArgb(32, 32, 32);
        Controls.Add(_web);
        BuildTray();

        _log.Written += (level, line) =>
        {
            var entry = new { level = level.ToString().ToLowerInvariant(), line };
            lock (_history) { _history.Add(entry); if (_history.Count > 3000) _history.RemoveRange(0, 500); }
            OnUi(() => Post(new { @event = "log", data = entry }));
        };
        _host.StateChanged += () => OnUi(() => { UpdateTray(); QueueState(); });
        _host.PairChanged += _ => OnUi(QueueState);
        _host.CycleCompleted += (ps, stats) =>
        {
            if (stats.Conflicts > 0)
                OnUi(() => _tray.ShowBalloonTip(8000, "Unisono: conflict",
                    $"{stats.Conflicts} file(s) in {ps.Pair.Name} changed on both sides. Both versions were kept (look for 'conflict' in the name).",
                    ToolTipIcon.Warning));
        };
        // State pushes are coalesced: a cycle over many pairs fires many small events.
        _stateTimer.Tick += (_, _) => { _stateTimer.Stop(); Post(new { @event = "state", data = _bridge.State() }); };

        // The shared pair list may be changed by another computer (through whatever syncs that file).
        _reloadTimer.Tick += async (_, _) =>
        {
            _reloadTimer.Stop();
            var before = System.Text.Json.JsonSerializer.Serialize(_bridge.Config.Pairs);
            _bridge.Reload();
            if (System.Text.Json.JsonSerializer.Serialize(_bridge.Config.Pairs) == before) return; // our own save
            _log.Info("the shared pair list changed; reloading");
            await StartHostAsync();
        };

        // A second start of Unisono only signals this one to show itself.
        ThreadPool.RegisterWaitForSingleObject(showSignal, (_, _) => OnUi(ShowFromTray), null, Timeout.Infinite, executeOnlyOnce: false);

        _ = Handle; // BeginInvoke needs a handle even while the window stays hidden
        BeginInvoke(new Action(async () => await InitAsync()));
    }

    private async Task InitAsync()
    {
        _log.Info($"Unisono {Ui.Version} started; settings in {AppPaths.DataDir}");
        await StartHostAsync();
        _ = _bridge.RefreshGoogleInfoAsync();
        try
        {
            await InitWebAsync();
        }
        catch (Exception ex)
        {
            // Without the WebView2 runtime there is no window, but syncing still runs from the tray.
            _log.Error("the window could not start: " + ex.Message);
            Controls.Remove(_web);
            Controls.Add(new Label
            {
                Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.White,
                Text = "The Unisono window needs the Microsoft Edge WebView2 Runtime, which could not be started:\n\n" + ex.Message +
                       "\n\nSyncing continues in the background (tray icon).",
            });
        }
    }

    private async Task InitWebAsync()
    {
        var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(AppPaths.DataDir, "WebView2"));
        await _web.EnsureCoreWebView2Async(env);
        var core = _web.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = Environment.GetEnvironmentVariable("UNISONO_DEVTOOLS") == "1";
        core.Settings.AreDevToolsEnabled = Environment.GetEnvironmentVariable("UNISONO_DEVTOOLS") == "1";
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        // The page is the app: links go to the real browser, never into this window.
        core.NavigationStarting += (_, e) =>
        {
            if (e.Uri.StartsWith("http", StringComparison.OrdinalIgnoreCase)) { e.Cancel = true; OpenExternal(e.Uri); }
        };
        core.NewWindowRequested += (_, e) => { e.Handled = true; OpenExternal(e.Uri); };
        core.WebMessageReceived += async (_, e) => await OnMessageAsync(e.WebMessageAsJson);

        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Unisono.ui.index.html")
                           ?? throw new InvalidOperationException("ui/index.html missing from the build");
        using var reader = new StreamReader(stream);
        _web.NavigateToString(await reader.ReadToEndAsync());
        _webReady = true;
    }

    private static void OpenExternal(string uri)
    {
        if (uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri) { UseShellExecute = true }); } catch { }
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
            object? result = method == "hello"
                ? new { state = _bridge.State(), log = Snapshot() }
                : await _bridge.HandleAsync(method, p);
            Post(new { id, ok = true, result });
        }
        catch (Exception ex)
        {
            Post(new { id, ok = false, error = Ui.Explain(ex) });
        }
    }

    private List<object> Snapshot() { lock (_history) return _history.ToList(); }

    private void Post(object message)
    {
        if (!_webReady || _web.CoreWebView2 == null || IsDisposed) return;
        try { _web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message, Json)); } catch { }
    }

    private void QueueState()
    {
        if (!_stateTimer.Enabled) _stateTimer.Start();
    }

    private async Task StartHostAsync()
    {
        try
        {
            await _host.StartAsync(_bridge.Config);
        }
        catch (Exception ex)
        {
            _log.Error("not syncing: " + ex.Message);
        }
        // The menu entries name the synced folders, so they follow every change of the pair list.
        ExplorerMenu.Update(_bridge.Config, _log);
        WatchSharedFile();
        UpdateTray();
        QueueState();
    }

    private void WatchSharedFile()
    {
        _sharedWatcher?.Dispose();
        _sharedWatcher = null;
        if (_bridge.Config.SharedFile is not { } file) return;
        var full = Path.GetFullPath(file);
        var dir = Path.GetDirectoryName(full);
        if (dir == null || !Directory.Exists(dir)) return;
        _sharedWatcher = new FileSystemWatcher(dir, Path.GetFileName(full))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
        };
        FileSystemEventHandler kick = (_, _) => OnUi(() => { _reloadTimer.Stop(); _reloadTimer.Start(); });
        _sharedWatcher.Changed += kick; _sharedWatcher.Created += kick; _sharedWatcher.Renamed += (_, _) => OnUi(() => { _reloadTimer.Stop(); _reloadTimer.Start(); });
        _sharedWatcher.EnableRaisingEvents = true;
    }

    // ------------------------------------------------------------------ tray

    private void BuildTray()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open Unisono", null, (_, _) => ShowFromTray());
        menu.Items.Add("Sync now", null, (_, _) => _host.SyncNow());
        _trayPause.Click += (_, _) => { _host.Paused = !_host.Paused; UpdateTray(); QueueState(); };
        menu.Items.Add(_trayPause);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => { _exiting = true; Close(); });
        _tray.ContextMenuStrip = menu;
        _tray.Icon = IconFor(Ui.Grey);
        _tray.Text = "Unisono";
        _tray.Visible = true;
        _tray.DoubleClick += (_, _) => ShowFromTray();
        _tray.BalloonTipClicked += (_, _) => ShowFromTray();
    }

    private void UpdateTray()
    {
        var (text, color) = _host.State switch
        {
            HostState.Syncing => ("Syncing...", Ui.Blue),
            HostState.Paused => ("Paused", Ui.Amber),
            HostState.Trouble => ("Problems, retrying (see log)", Ui.Red),
            HostState.Idle when _bridge.Config.Pairs.Count == 0 => ("No sync pairs", Ui.Grey),
            HostState.Idle => ("Up to date", Ui.Green),
            HostState.Starting => ("Starting...", Ui.Blue),
            _ => ("Not syncing", Ui.Grey),
        };
        if (_host.Paused && _host.State != HostState.Syncing) (text, color) = ("Paused", Ui.Amber);
        else if (_host.PausedBecause is { } why && _host.State != HostState.Syncing) (text, color) = ("Paused: " + why, Ui.Amber);
        _tray.Text = ("Unisono: " + text) is var t && t.Length > 63 ? t[..63] : t;
        _tray.Icon = IconFor(color);
        Icon = IconFor(color);
        _trayPause.Text = _host.Paused ? "Resume" : "Pause";
    }

    private Icon IconFor(Color c)
    {
        if (!_icons.TryGetValue(c, out var icon)) _icons[c] = icon = Ui.StatusIcon(c);
        return icon;
    }

    // ------------------------------------------------------ window lifecycle

    private void ApplySavedBounds()
    {
        var r = new Rectangle(_settings.X, _settings.Y, _settings.Width, _settings.Height);
        if (_settings.X != int.MinValue && Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(r)))
        {
            StartPosition = FormStartPosition.Manual;
            Bounds = r;
            if (_settings.Maximized) WindowState = FormWindowState.Maximized;
        }
        else
        {
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(_settings.Width, _settings.Height);
        }
    }

    private void RememberBounds()
    {
        _settings.Maximized = WindowState == FormWindowState.Maximized;
        var b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        if (WindowState != FormWindowState.Minimized && b.Width > 0)
            (_settings.X, _settings.Y, _settings.Width, _settings.Height) = (b.X, b.Y, b.Width, b.Height);
        _settings.Save();
    }

    protected override void OnResizeEnd(EventArgs e) { base.OnResizeEnd(e); RememberBounds(); }

    protected override void SetVisibleCore(bool value)
    {
        if (_startHidden && !_allowShow) value = false;
        base.SetVisibleCore(value);
    }

    private void ShowFromTray()
    {
        _allowShow = true;
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        RememberBounds();
        if (!_exiting && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            if (!_toldAboutTray)
            {
                _toldAboutTray = true;
                _tray.ShowBalloonTip(4000, "Unisono", "Still syncing in the background. Right-click the tray icon to exit.", ToolTipIcon.Info);
            }
            return;
        }
        _tray.Visible = false;
        // Off the UI thread: the host's awaits must not need this (blocked) thread to finish.
        try { Task.Run(async () => { await _host.DisposeAsync(); await _bridge.DisposeAsync(); }).Wait(TimeSpan.FromSeconds(10)); } catch { }
        base.OnFormClosing(e);
    }

    private void OnUi(Action action)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(action); } catch (InvalidOperationException) { /* closing */ }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tray.Dispose();
            _stateTimer.Dispose();
            foreach (var i in _icons.Values) i.Dispose();
        }
        base.Dispose(disposing);
    }
}
