using System.Reflection;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Unisono.App;

/// <summary>
/// The Unisono window: a WebView2 page (ui/index.html) for everything visible, plus what must stay native: the tray
/// icon, the sync host, window placement and dialogs. Closing the window hides it; syncing continues until Exit.
/// </summary>
sealed partial class HostForm : Form
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
    // The page lives only while the window is open: hidden in the tray, WebView2's browser processes (some 400 MB)
    // are closed a little while after the window is, and started again when it is shown.
    private WebView2? _web;
    private bool _webFailed;
    private readonly System.Windows.Forms.Timer _webIdleTimer = new() { Interval = 30_000 };
    private readonly NotifyIcon _tray = new();
    private readonly ToolStripMenuItem _trayPause = new("Pause");
    private readonly Dictionary<(Color, string), Icon> _icons = new();
    private readonly System.Windows.Forms.Timer _trayTimer = new() { Interval = 300 };
    private readonly List<object> _history = new();
    private readonly System.Windows.Forms.Timer _stateTimer = new() { Interval = 250 };
    private readonly System.Windows.Forms.Timer _activityTimer = new() { Interval = 1000 };
    private readonly System.Windows.Forms.Timer _groupTimer = new() { Interval = 60_000 };
    private bool _scanningGroups;
    private bool _webReady;
    // The page said hello (it is loaded and listens); until then a request to open the Conflicts window waits.
    private bool _pageLoaded;
    private bool _pendingConflicts;
    // The tab the last balloon about a pair or a group leads to ("pairs" or "groups"); and the one to open once the page
    // has loaded, when it was clicked before that.
    private string _balloonTab = "pairs";
    private string? _pendingTab;
    private const string ConflictBalloonTitle = "Unisono: conflict";

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
        BuildTray();
        _webIdleTimer.Tick += (_, _) => { _webIdleTimer.Stop(); if (!Visible) CloseWeb(); };

        _log.Written += (level, line) =>
        {
            var entry = new { level = level.ToString().ToLowerInvariant(), line };
            lock (_history) { _history.Add(entry); if (_history.Count > 3000) _history.RemoveRange(0, 500); }
            OnUi(() => Post(new { @event = "log", data = entry }));
        };
        _host.StateChanged += () => OnUi(() => { UpdateTray(); QueueState(); });
        // Transfers start and end in bursts: redraw the tray icon at most a few times a second.
        _trayTimer.Tick += (_, _) => { _trayTimer.Stop(); UpdateTray(); PostTransfers(); };
        // New activity: the page reloads its list when it shows today (at most once a second).
        _activityTimer.Tick += (_, _) => { _activityTimer.Stop(); Post(new { @event = "activity" }); };
        ActivityJournal.Added += () => OnUi(() => { if (!_activityTimer.Enabled) _activityTimer.Start(); });
        SyncActivity.Changed += () => OnUi(() => { if (!_trayTimer.Enabled) _trayTimer.Start(); });
        _host.PairChanged += _ => OnUi(QueueState);
        _host.CycleCompleted += (ps, stats) =>
        {
            if (stats.Notices.Count > 0)
                OnUi(() => _tray.ShowBalloonTip(10000, "Unisono", string.Join("\n", stats.Notices.Take(2)), ToolTipIcon.Info));
            else if (stats.Conflicts > 0)
                OnUi(() => _tray.ShowBalloonTip(10000, ConflictBalloonTitle,
                    $"{stats.Conflicts} file(s) in {ps.Pair.Name} changed on both sides in the same place. Both versions were kept. Click to compare them and keep one or merge them.",
                    ToolTipIcon.Warning));
        };
        _host.HealthProblem += ps => OnUi(() =>
            _tray.ShowBalloonTip(15000, $"Unisono: {ps.Pair.Name} is not syncing", ps.Health ?? "", ToolTipIcon.Warning));
        _host.Converted += ps => OnUi(() =>
            _tray.ShowBalloonTip(20000, $"Unisono: {ps.Pair.Name} is encrypted",
                "Everything is in the Cryptomator vault. Unencrypted copies can still be in the trash on the other side (Google Drive, the NAS's recycle bin) until it is emptied.",
                ToolTipIcon.Info));
        // Other computers change sync groups through the file in the hub, and a hub that arrives here can be joined.
        _groupTimer.Tick += async (_, _) => await ScanGroupsAsync();
        _host.ContentProblem += ps => OnUi(() =>
            _tray.ShowBalloonTip(15000, $"Unisono: content check of {ps.Pair.Name}",
                $"{ps.LastCheck?.Problems.Count} file(s) changed without being edited (disk or transfer damage?). Open Unisono to keep both versions.", ToolTipIcon.Warning));
        // State pushes are coalesced: a cycle over many pairs fires many small events.
        _stateTimer.Tick += (_, _) => { _stateTimer.Stop(); Post(new { @event = "state", data = _bridge.State() }); };

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
        if (!_startHidden) await OpenWebAsync();
        await ScanGroupsAsync();
        _groupTimer.Start();
    }

    private async Task ScanGroupsAsync()
    {
        if (_scanningGroups) return;
        _scanningGroups = true;
        try
        {
            foreach (var o in await _bridge.ScanGroupsAsync())
            {
                _balloonTab = "groups";
                _tray.ShowBalloonTip(20000, "Unisono: sync group found",
                    $"The group '{o.Name}' keeps {o.Members.Count} folders in step ({o.MembersHere.Count} on this computer). Click to join it here.",
                    ToolTipIcon.Info);
            }
        }
        catch (Exception ex) { _log.Warn("looking for sync groups: " + ex.Message); }
        finally { _scanningGroups = false; }
    }

    private async Task OpenWebAsync()
    {
        if (_web != null || _webFailed) return;
        _web = new WebView2 { Dock = DockStyle.Fill };
        Controls.Add(_web);
        try
        {
            await InitWebAsync(_web);
        }
        catch (Exception ex)
        {
            // Without the WebView2 runtime there is no window, but syncing still runs from the tray.
            _log.Error("the window could not start: " + ex.Message);
            _webFailed = true;
            CloseWeb();
            Controls.Add(new Label
            {
                Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.White,
                Text = "The Unisono window needs the Microsoft Edge WebView2 Runtime, which could not be started:\n\n" + ex.Message +
                       "\n\nSyncing continues in the background (tray icon).",
            });
        }
    }

    private void CloseWeb()
    {
        _webReady = false;
        _pageLoaded = false;
        if (_web == null) return;
        Controls.Remove(_web);
        _web.Dispose();
        _web = null;
    }

    private async Task InitWebAsync(WebView2 web)
    {
        var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(AppPaths.DataDir, "WebView2"));
        await web.EnsureCoreWebView2Async(env);
        if (web != _web) return; // closed again meanwhile
        var core = web.CoreWebView2;
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
        var html = await reader.ReadToEndAsync();
        if (web != _web) return;
        web.NavigateToString(html);
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
            object? result;
            if (method == "hello")
            {
                _pageLoaded = true;
                result = new { state = _bridge.State(), log = Snapshot(), openConflicts = _pendingConflicts, showTab = _pendingTab };
                _pendingConflicts = false;
                _pendingTab = null;
            }
            else result = await _bridge.HandleAsync(method, p);
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
        if (!_webReady || _web?.CoreWebView2 == null || IsDisposed) return;
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
        UpdateTray();
        QueueState();
    }

    // ------------------------------------------------------------------ tray

    private void BuildTray()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open Unisono", null, (_, _) => ShowFromTray());
        menu.Items.Add("Sync now", null, (_, _) => _host.SyncNow());
        menu.Items.Add("Conflicts...", null, (_, _) => OpenConflicts());
        _trayPause.Click += (_, _) => { _host.Paused = !_host.Paused; UpdateTray(); QueueState(); };
        menu.Items.Add(_trayPause);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Unisono " + Ui.Version) { Enabled = false });
        menu.Items.Add("Exit", null, (_, _) => { _exiting = true; Close(); });
        SetUpUpdateCheck(menu);
        _tray.ContextMenuStrip = menu;
        _tray.Icon = IconFor(Ui.Grey);
        _tray.Text = "Unisono";
        _tray.Visible = true;
        _tray.DoubleClick += (_, _) => ShowFromTray();
        _tray.BalloonTipClicked += (_, _) =>
        {
            if (_tray.BalloonTipTitle == ConflictBalloonTitle) OpenConflicts();
            else if (_tray.BalloonTipTitle is "Unisono: sync group found" or "Unisono: folder deleted")
            {
                ShowFromTray();
                if (_pageLoaded) Post(new { @event = "showTab", tab = _balloonTab });
                else _pendingTab = _balloonTab;
            }
            else ShowFromTray();
        };
    }

    /// <summary>Shows the window with the Conflicts dialog open (once the page has loaded, if it has not yet).</summary>
    private void OpenConflicts()
    {
        ShowFromTray();
        if (_pageLoaded) Post(new { @event = "openConflicts" });
        else _pendingConflicts = true;
    }

    /// <summary>
    /// The tray icon: arrows while files go up or down, otherwise a coloured U for the overall state. Hovering shows
    /// what is being transferred, or when the last sync ran, what has moved since start and which pair has trouble.
    /// </summary>
    /// <summary>The running transfers for the window's list (sent with the tray updates, a few times a second at most).</summary>
    private void PostTransfers()
    {
        if (!_webReady || !Visible) return;
        var snap = SyncActivity.Now();
        Post(new
        {
            @event = "transfers",
            data = snap.Active.OrderByDescending(t => t.Size).Select(t => new
            {
                dir = t.Dir == SyncActivity.Direction.Up ? "up" : "down", pair = t.Pair, rel = t.Rel, size = t.Size,
                done = Math.Min(t.Done, t.Size), speed = (long)t.Speed,
            }).ToList(),
        });
    }

    private void UpdateTray()
    {
        var snap = SyncActivity.Now();
        if (snap.Active.Count > 0)
        {
            var ups = snap.Active.Count(t => t.Dir == SyncActivity.Direction.Up);
            var downs = snap.Active.Count - ups;
            var glyph = ups > 0 && downs > 0 ? "both" : ups > 0 ? "up" : "down";
            var lines = new List<string> { "Unisono: " + string.Join(", ", new[] { downs > 0 ? $"downloading {downs}" : null, ups > 0 ? $"uploading {ups}" : null }.Where(s => s != null)) };
            // The largest first: that is where progress and speed say something.
            lines.AddRange(snap.Active.OrderByDescending(t => t.Size).Take(4).Select(t =>
                $"{(t.Dir == SyncActivity.Direction.Up ? "\u2191" : "\u2193")} {Shorten(PathUtil.NameOf(t.Rel), 30)} " +
                (t.Size >= 1 << 20 && t.Done > 0 ? $"{Math.Min(100, 100 * t.Done / t.Size)}% {Ui.Size((long)t.Speed)}/s" : $"({Ui.Size(t.Size)})")));
            if (snap.Active.Count > 4) lines.Add($"and {snap.Active.Count - 4} more");
            SetTray(lines, Ui.Blue, glyph);
            return;
        }
        var (text, color) = _host.State switch
        {
            HostState.Syncing => ("Syncing...", Ui.Blue),
            HostState.Paused => ("Paused", Ui.Amber),
            HostState.Trouble => ("Problems, retrying (see log)", Ui.Red),
            HostState.Idle when _bridge.Config.Pairs.Count == 0 => ("No sync pairs", Ui.Grey),
            HostState.Idle => ("Up to date", Ui.Green),
            HostState.Starting => ("Starting...", Ui.Blue),
            HostState.Checking => ("Checking all pairs...", Ui.Green),
            _ => ("Not syncing", Ui.Grey),
        };
        if (_host.Paused && _host.State != HostState.Syncing) (text, color) = ("Paused", Ui.Amber);
        else if (_host.PausedBecause is { } why && _host.State != HostState.Syncing) (text, color) = ("Paused: " + why, Ui.Amber);
        else if (_host.Offline && _host.State is not (HostState.Syncing or HostState.Trouble)) (text, color) = ("Offline, waiting for the network", Ui.Amber);
        // Conflict copies wait for a decision: amber until they are sorted out (tray menu or window: Conflicts...).
        var copies = _host.Pairs.Sum(p => p.ConflictCopies);
        if (copies > 0 && color == Ui.Green) (text, color) = ($"{text}; {copies} conflict cop{(copies == 1 ? "y" : "ies")} to sort out", Ui.Amber);
        var details = new List<string> { "Unisono: " + text };
        var last = _host.Pairs.Where(p => p.LastSync != null).Select(p => p.LastSync!.Value).DefaultIfEmpty().Max();
        if (last != default) details.Add($"Last sync {last:HH:mm}" + (_host.Pairs.Count > 1 ? $", {_host.Pairs.Count} pairs" : ""));
        if (snap.Downloaded + snap.Uploaded > 0)
            details.Add($"Since start: \u2193{snap.Downloaded} ({Ui.Size(snap.DownBytes)}) \u2191{snap.Uploaded} ({Ui.Size(snap.UpBytes)})");
        foreach (var bad in _host.Pairs.Where(p => p.LastError != null).Take(2))
            details.Add($"{Shorten(bad.Pair.Name, 24)}: {bad.LastError}");
        SetTray(details, color, "U");
        _trayPause.Text = _host.Paused ? "Resume" : "Pause";
    }

    /// <summary>Tooltips hold 127 characters: whole lines as long as they fit, the last one cut.</summary>
    private void SetTray(List<string> lines, Color color, string glyph)
    {
        var text = lines[0];
        foreach (var l in lines.Skip(1))
        {
            if (text.Length + 1 + l.Length <= 127) text += "\n" + l;
            else { if (text.Length < 120) text += "\n" + l[..(126 - text.Length - 1)] + "\u2026"; break; }
        }
        _tray.Text = text.Length > 127 ? text[..127] : text;
        _tray.Icon = IconFor(color, glyph);
        Icon = IconFor(color, "U");
    }

    private static string Shorten(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "\u2026";

    private Icon IconFor(Color c, string glyph = "U")
    {
        if (!_icons.TryGetValue((c, glyph), out var icon)) _icons[(c, glyph)] = icon = Ui.StatusIcon(c, glyph);
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
        _webIdleTimer.Stop();
        Show();
        _ = OpenWebAsync();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        _ = _bridge.RefreshGoogleInfoAsync(); // retries accounts whose last check failed
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        RememberBounds();
        if (!_exiting && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            _webIdleTimer.Stop(); _webIdleTimer.Start();
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
            _groupTimer.Dispose();
            foreach (var i in _icons.Values) i.Dispose();
        }
        base.Dispose(disposing);
    }
}
