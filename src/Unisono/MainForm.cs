using Microsoft.Win32;

namespace Unisono.App;

/// <summary>
/// The Unisono window: accounts (Google, NAS logins), sync pairs with live status, the log, pause, sync now and
/// autostart. Closing the window hides it to the tray; syncing continues until Exit.
/// </summary>
sealed class MainForm : Form
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "Unisono";

    private readonly bool _startHidden;
    private bool _allowShow;
    private bool _exiting;
    private bool _toldAboutTray;
    private readonly Log _log = new() { ToConsole = false };
    private readonly SyncHost _host;
    private Config _config = Config.Load();
    private DriveClient? _drive;
    private readonly Dictionary<Color, Icon> _icons = new();

    private readonly NotifyIcon _tray = new();
    private readonly ToolStripMenuItem _trayPause = new("Pause");
    private readonly Label _googleStatus = new() { AutoSize = true, Margin = new Padding(3, 7, 12, 3) };
    private readonly Button _googleButton = new() { AutoSize = true };
    private readonly Button _googleClient = new() { AutoSize = true, Text = "OAuth client..." };
    private readonly ComboBox _nasLogins = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
    private readonly ListView _pairs = new()
    {
        View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false, Dock = DockStyle.Fill, GridLines = false,
    };
    private readonly Button _pause = new() { AutoSize = true, Text = "Pause" };
    private readonly RichTextBox _logBox = new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, BackColor = SystemColors.Window, Font = new Font("Consolas", 9f), WordWrap = false,
        DetectUrls = false, BorderStyle = BorderStyle.None,
    };
    private readonly CheckBox _verbose = new() { Text = "Details", AutoSize = true };
    private readonly CheckBox _autostart = new() { Text = "Start with Windows", AutoSize = true };
    private readonly Label _state = new() { AutoSize = true, Anchor = AnchorStyles.Right, Margin = new Padding(3, 7, 3, 3) };

    public MainForm(bool startHidden, EventWaitHandle showSignal)
    {
        _startHidden = startHidden;
        _host = new SyncHost(_log);
        Text = "Unisono";
        Font = SystemFonts.MessageBoxFont;
        Size = new Size(980, 720);
        MinimumSize = new Size(720, 520);
        StartPosition = FormStartPosition.CenterScreen;
        Icon = IconFor(Ui.Grey);

        BuildLayout();
        BuildTray();

        _log.Written += (level, line) => OnUi(() => AppendLog(level, line));
        _host.StateChanged += () => OnUi(UpdateState);
        _host.PairChanged += ps => OnUi(() => UpdatePairRow(ps));
        _host.CycleCompleted += (ps, stats) =>
        {
            if (stats.Conflicts > 0)
                OnUi(() => _tray.ShowBalloonTip(8000, "Unisono: conflict",
                    $"{stats.Conflicts} file(s) in {ps.Pair.Name} changed on both sides. Both versions were kept (look for 'conflict' in the name).",
                    ToolTipIcon.Warning));
        };

        // A second start of Unisono only signals this one to show itself.
        ThreadPool.RegisterWaitForSingleObject(showSignal, (_, _) => OnUi(ShowFromTray), null, Timeout.Infinite, executeOnlyOnce: false);

        _ = Handle; // BeginInvoke needs a handle even while the window stays hidden
        BeginInvoke(new Action(async () => await InitAsync()));
    }

    // ------------------------------------------------------------------ layout

    private void BuildLayout()
    {
        var accounts = new GroupBox { Text = "Accounts", Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(8, 4, 8, 6) };
        var acc = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2 };
        acc.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        acc.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _googleButton.Click += async (_, _) => await GoogleButtonAsync();
        _googleClient.Click += (_, _) => ChooseClientSecret();
        acc.Controls.Add(Ui.Label("Google Drive"), 0, 0);
        acc.Controls.Add(Ui.Row(_googleStatus, _googleButton, _googleClient), 1, 0);
        acc.Controls.Add(Ui.Label("Synology NAS"), 0, 1);
        acc.Controls.Add(Ui.Row(_nasLogins,
            Ui.Button("Add login...", (_, _) => AddNasLogin()),
            Ui.Button("Log in again...", (_, _) => AddNasLogin(relogin: true)),
            Ui.Button("Forget", (_, _) => ForgetNasLogin())), 1, 1);
        accounts.Controls.Add(acc);

        var pairsBox = new GroupBox { Text = "Sync pairs", Dock = DockStyle.Fill, Padding = new Padding(8, 4, 8, 6) };
        _pairs.Columns.Add("Remote", 300);
        _pairs.Columns.Add("Local folder", 260);
        _pairs.Columns.Add("Status", 80);
        _pairs.Columns.Add("Last sync", 70);
        _pairs.Columns.Add("Last result", 230);
        _pairs.DoubleClick += async (_, _) => await EditPairAsync();
        var pairMenu = new ContextMenuStrip();
        pairMenu.Items.Add("Open local folder", null, (_, _) => { if (SelectedPair is { } p) Ui.OpenFolder(p.LocalPath); });
        pairMenu.Items.Add("Edit...", null, async (_, _) => await EditPairAsync());
        pairMenu.Items.Add("Remove", null, async (_, _) => await RemovePairAsync());
        _pairs.ContextMenuStrip = pairMenu;
        _pause.Click += (_, _) => TogglePause();
        var pairButtons = Ui.Row(
            Ui.Button("Add...", async (_, _) => await AddPairAsync()),
            Ui.Button("Edit...", async (_, _) => await EditPairAsync()),
            Ui.Button("Remove", async (_, _) => await RemovePairAsync()),
            Ui.Button("Open folder", (_, _) => { if (SelectedPair is { } p) Ui.OpenFolder(p.LocalPath); }),
            new Label { Width = 24 },
            Ui.Button("Sync now", (_, _) => _host.SyncNow()),
            _pause);
        pairButtons.Dock = DockStyle.Bottom;
        pairsBox.Controls.Add(_pairs);
        pairsBox.Controls.Add(pairButtons);

        var logBox = new GroupBox { Text = "Log", Dock = DockStyle.Fill, Padding = new Padding(8, 4, 8, 6) };
        _verbose.CheckedChanged += (_, _) => _log.Verbose = _verbose.Checked;
        var logButtons = Ui.Row(_verbose,
            Ui.Button("Clear", (_, _) => _logBox.Clear()),
            Ui.Button("Open log folder", (_, _) => Ui.OpenFolder(_log.Directory)),
            Ui.Button("Open settings folder", (_, _) => Ui.OpenFolder(AppPaths.DataDir)));
        logButtons.Dock = DockStyle.Bottom;
        logBox.Controls.Add(_logBox);
        logBox.Controls.Add(logButtons);

        var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2 };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _autostart.Checked = IsAutostart();
        _autostart.CheckedChanged += (_, _) => SetAutostart(_autostart.Checked);
        bottom.Controls.Add(_autostart, 0, 0);
        bottom.Controls.Add(_state, 1, 0);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(8) };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 40));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 60));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(accounts, 0, 0);
        root.Controls.Add(pairsBox, 0, 1);
        root.Controls.Add(logBox, 0, 2);
        root.Controls.Add(bottom, 0, 3);
        Controls.Add(root);
    }

    private void BuildTray()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open Unisono", null, (_, _) => ShowFromTray());
        menu.Items.Add("Sync now", null, (_, _) => _host.SyncNow());
        _trayPause.Click += (_, _) => TogglePause();
        menu.Items.Add(_trayPause);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApp());
        _tray.ContextMenuStrip = menu;
        _tray.Icon = IconFor(Ui.Grey);
        _tray.Text = "Unisono";
        _tray.Visible = true;
        _tray.DoubleClick += (_, _) => ShowFromTray();
        _tray.BalloonTipClicked += (_, _) => ShowFromTray();
    }

    // ------------------------------------------------------------ lifecycle

    private async Task InitAsync()
    {
        _log.Info($"Unisono {Ui.Version} started; settings in {AppPaths.DataDir}");
        RefreshNasLogins();
        RefreshPairs();
        UpdateState();
        await RefreshGoogleAsync();
        await StartHostAsync();
        if (_config.Pairs.Count == 0 && !_startHidden)
            _log.Info("No sync pairs yet. Sign in to Google or add a NAS login, then click Add... under Sync pairs.");
    }

    private async Task StartHostAsync()
    {
        try
        {
            await _host.StartAsync(_config);
        }
        catch (Exception ex)
        {
            _log.Error(ex.Message);
            _state.Text = "Not syncing: " + ex.Message;
        }
        RefreshPairs();
    }

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
        try { Task.Run(async () => await _host.DisposeAsync()).Wait(TimeSpan.FromSeconds(10)); } catch { }
        base.OnFormClosing(e);
    }

    private void ExitApp()
    {
        _exiting = true;
        Close();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tray.Dispose();
            foreach (var i in _icons.Values) i.Dispose();
        }
        base.Dispose(disposing);
    }

    private void OnUi(Action action)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(action); } catch (InvalidOperationException) { /* includes ObjectDisposedException */ }
    }

    // ---------------------------------------------------------------- google

    private async Task RefreshGoogleAsync()
    {
        _googleClient.Visible = true;
        if (!DriveClient.HasClientSecret())
        {
            _googleStatus.Text = "No OAuth client yet (see README: one-time setup)";
            _googleButton.Text = "Sign in...";
            _googleButton.Enabled = false;
            return;
        }
        _googleButton.Enabled = true;
        if (!DriveClient.HasStoredToken())
        {
            _googleStatus.Text = "Not signed in";
            _googleButton.Text = "Sign in...";
            return;
        }
        _googleButton.Text = "Sign out";
        _googleStatus.Text = "Signed in, checking...";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var drive = await GetDriveAsync(cts.Token);
            var (email, usage, limit, _) = await drive.WhoAmIAsync(cts.Token);
            _googleStatus.Text = $"{email}   {Gb(usage)} of {(limit > 0 ? Gb(limit) : "unlimited")} used";
        }
        catch (Exception ex)
        {
            _googleStatus.Text = "Signed in (could not reach Google: " + Ui.Explain(ex) + ")";
        }
    }

    private async Task<DriveClient> GetDriveAsync(CancellationToken ct)
    {
        if (_drive != null) return _drive;
        if (!DriveClient.HasStoredToken())
            throw new InvalidOperationException("Sign in to Google first (Accounts, Google Drive).");
        return _drive = await DriveClient.ConnectAsync(ct, quiet: true);
    }

    private async Task GoogleButtonAsync()
    {
        if (DriveClient.HasStoredToken())
        {
            if (_config.Pairs.Any(p => p.Kind == RemoteKind.Drive) &&
                !Ui.Confirm(this, "Sign out of Google? Drive pairs stop syncing until you sign in again. No files are touched."))
                return;
            DriveClient.SignOut();
            _drive = null;
            _log.Info("signed out of Google");
            await RefreshGoogleAsync();
            await StartHostAsync();
            return;
        }

        _googleButton.Enabled = false;
        _googleStatus.Text = "Finish the sign-in in your browser...";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            _drive = await DriveClient.ConnectAsync(cts.Token, quiet: true);
            _log.Info("signed in to Google");
        }
        catch (Exception ex)
        {
            _drive = null;
            Ui.Error(this, "Google sign-in did not complete: " + Ui.Explain(ex));
        }
        finally
        {
            _googleButton.Enabled = true;
        }
        await RefreshGoogleAsync();
        await StartHostAsync();
    }

    private void ChooseClientSecret()
    {
        using var d = new OpenFileDialog
        {
            Title = "OAuth client JSON downloaded from Google Cloud Console (Desktop app)",
            Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
        };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var info = OAuthClientInfo.Read(d.FileName);
            if (info.Kind != "installed" && !Ui.Confirm(this, "This is not a 'Desktop app' client; sign-in will probably fail. Use it anyway?"))
                return;
            AppPaths.EnsureDataDir();
            File.Copy(d.FileName, AppPaths.ClientSecretPath, overwrite: true);
            _drive = null;
            _log.Info($"OAuth client set: project {info.ProjectId ?? "?"}, client {info.ClientId}");
            _ = RefreshGoogleAsync();
        }
        catch (Exception ex) { Ui.Error(this, ex.Message); }
    }

    private static string Gb(long bytes) => $"{bytes / 1024.0 / 1024 / 1024:F1} GB";

    // ------------------------------------------------------------------- NAS

    private void RefreshNasLogins()
    {
        var logins = SynologyCredentials.ListAll();
        _nasLogins.Items.Clear();
        foreach (var c in logins) _nasLogins.Items.Add(new NasItem(c));
        if (logins.Count == 0) _nasLogins.Items.Add("(no NAS logins)");
        _nasLogins.SelectedIndex = 0;
    }

    private sealed record NasItem(SynologyCredentials Creds)
    {
        public override string ToString() => $"{Creds.User} @ {Creds.Server}";
    }

    private void AddNasLogin(bool relogin = false)
    {
        var existing = relogin ? (_nasLogins.SelectedItem as NasItem)?.Creds : null;
        if (relogin && existing == null) { Ui.Error(this, "Pick a stored NAS login first."); return; }
        using var f = new NasLoginForm(_log, existing);
        if (f.ShowDialog(this) != DialogResult.OK) return;
        RefreshNasLogins();
        _ = StartHostAsync(); // a pair may have been waiting for this login
    }

    private void ForgetNasLogin()
    {
        if (_nasLogins.SelectedItem is not NasItem item) return;
        var users = _config.Pairs.Count(p => p.Kind == RemoteKind.Synology && string.Equals(p.Server, item.Creds.Server, StringComparison.OrdinalIgnoreCase));
        if (!Ui.Confirm(this, $"Forget the login {item}?" + (users > 0 ? $"\n\n{users} pair(s) use it and will stop syncing." : ""))) return;
        SynologyCredentials.Delete(item.Creds.Server);
        _log.Info($"forgot NAS login {item}");
        RefreshNasLogins();
    }

    // ----------------------------------------------------------------- pairs

    private SyncPair? SelectedPair =>
        _pairs.SelectedItems.Count == 1 ? _pairs.SelectedItems[0].Tag as SyncPair : null;

    private async Task AddPairAsync()
    {
        using var f = new PairForm(_config, null, GetDriveAsync);
        if (f.ShowDialog(this) != DialogResult.OK || f.Result == null) return;
        _config.Pairs.Add(f.Result);
        _config.Save();
        _log.Info($"added pair {f.Result.Name} <-> {f.Result.LocalPath}");
        await StartHostAsync();
    }

    private async Task EditPairAsync()
    {
        if (SelectedPair is not { } pair) return;
        using var f = new PairForm(_config, pair, GetDriveAsync);
        if (f.ShowDialog(this) != DialogResult.OK || f.Result == null) return;
        var i = _config.Pairs.IndexOf(pair);
        if (f.Result.Id != pair.Id) DeleteState(pair); // other folders: the old history no longer applies
        _config.Pairs[i] = f.Result;
        _config.Save();
        _log.Info($"changed pair {f.Result.Name} <-> {f.Result.LocalPath}");
        await StartHostAsync();
    }

    private async Task RemovePairAsync()
    {
        if (SelectedPair is not { } pair) return;
        if (!Ui.Confirm(this, $"Stop syncing {pair.Name} <-> {pair.LocalPath}?\n\nNo files are deleted on either side.")) return;
        _config.Pairs.Remove(pair);
        _config.Save();
        // Without its history a re-added pair starts by matching content, which is the safe way round.
        DeleteState(pair);
        _log.Info($"removed pair {pair.Name}");
        await StartHostAsync();
    }

    private static void DeleteState(SyncPair pair)
    {
        try { File.Delete(SyncState.FilePathFor(pair)); } catch { }
    }

    private void RefreshPairs()
    {
        _pairs.BeginUpdate();
        _pairs.Items.Clear();
        foreach (var p in _config.Pairs)
        {
            var item = new ListViewItem(new[] { RemoteLabel(p), p.LocalPath, "", "", "" }) { Tag = p };
            _pairs.Items.Add(item);
        }
        _pairs.EndUpdate();
        foreach (var ps in _host.Pairs) UpdatePairRow(ps);
    }

    private static string RemoteLabel(SyncPair p) => p.Kind == RemoteKind.Synology
        ? $"NAS {(Uri.TryCreate(p.Server, UriKind.Absolute, out var u) ? u.Host : p.Server)}: {p.RemotePath}"
        : $"Google Drive: {(p.RemotePath.Length == 0 ? "My Drive" : p.RemotePath)}";

    private void UpdatePairRow(PairStatus ps)
    {
        foreach (ListViewItem item in _pairs.Items)
        {
            if (item.Tag is not SyncPair p || p.Id != ps.Pair.Id) continue;
            item.SubItems[2].Text = ps.Status;
            item.SubItems[3].Text = ps.LastSync?.ToString("HH:mm:ss") ?? "";
            item.SubItems[4].Text = ps.LastError ?? Summary(ps.LastStats);
            item.ForeColor = ps.Status switch
            {
                "failed" or "errors" => Ui.Red,
                "syncing" => Ui.Blue,
                _ => SystemColors.WindowText,
            };
        }
    }

    private static string Summary(CycleStats? s)
    {
        if (s == null) return "";
        var parts = new List<string>();
        if (s.Downloaded > 0) parts.Add($"{s.Downloaded} down");
        if (s.Uploaded > 0) parts.Add($"{s.Uploaded} up");
        if (s.Exported > 0) parts.Add($"{s.Exported} exported");
        if (s.Linked > 0) parts.Add($"{s.Linked} linked");
        if (s.Trashed > 0) parts.Add($"{s.Trashed} deleted remotely");
        if (s.Recycled > 0) parts.Add($"{s.Recycled} to Recycle Bin");
        if (s.Conflicts > 0) parts.Add($"{s.Conflicts} conflict(s)");
        return parts.Count == 0 ? "no changes" : string.Join(", ", parts);
    }

    // ---------------------------------------------------------- state, log

    private void TogglePause()
    {
        _host.Paused = !_host.Paused;
        UpdateState();
    }

    private void UpdateState()
    {
        var (text, color) = _host.State switch
        {
            HostState.Syncing => ("Syncing...", Ui.Blue),
            HostState.Paused => ("Paused", Ui.Amber),
            HostState.Trouble => ("Problems, retrying (see log)", Ui.Red),
            HostState.Idle when _config.Pairs.Count == 0 => ("No sync pairs", Ui.Grey),
            HostState.Idle => ("Up to date", Ui.Green),
            HostState.Starting => ("Starting...", Ui.Blue),
            _ => ("Not syncing", Ui.Grey),
        };
        if (_host.Paused && _host.State != HostState.Syncing) (text, color) = ("Paused", Ui.Amber);
        _state.Text = $"{text}    Unisono {Ui.Version}";
        _tray.Text = Truncate("Unisono: " + text, 63);
        _tray.Icon = IconFor(color);
        Icon = IconFor(color);
        _pause.Text = _host.Paused ? "Resume" : "Pause";
        _trayPause.Text = _host.Paused ? "Resume" : "Pause";
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n];

    private Icon IconFor(Color c)
    {
        if (!_icons.TryGetValue(c, out var icon)) _icons[c] = icon = Ui.StatusIcon(c);
        return icon;
    }

    private void AppendLog(LogLevel level, string line)
    {
        if (_logBox.Lines.Length > 3000)
        {
            _logBox.Select(0, _logBox.GetFirstCharIndexFromLine(500));
            _logBox.SelectedText = "";
        }
        _logBox.SelectionStart = _logBox.TextLength;
        _logBox.SelectionLength = 0;
        _logBox.SelectionColor = level switch
        {
            LogLevel.Error => Ui.Red,
            LogLevel.Warn => Color.FromArgb(170, 110, 0),
            LogLevel.Debug => SystemColors.GrayText,
            _ => SystemColors.WindowText,
        };
        _logBox.AppendText(line + Environment.NewLine);
        _logBox.SelectionColor = SystemColors.WindowText;
        _logBox.ScrollToCaret();
    }

    // -------------------------------------------------------------- autostart

    private static bool IsAutostart()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(RunValue) is string s && s.Contains(Environment.ProcessPath ?? "\u0000", StringComparison.OrdinalIgnoreCase);
    }

    private void SetAutostart(bool on)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (on) key.SetValue(RunValue, $"\"{Environment.ProcessPath}\" --tray");
            else key.DeleteValue(RunValue, throwOnMissingValue: false);
            _log.Info(on ? "will start with Windows (in the tray)" : "will not start with Windows");
        }
        catch (Exception ex) { Ui.Error(this, ex.Message); }
    }
}
