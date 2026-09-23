namespace Unisono.App;

/// <summary>
/// Adds or edits a sync pair. OK checks everything before anything is written: the remote folder exists (or may be
/// created), the local folder does not overlap another pair, and a first sync between two non-empty folders or into
/// OneDrive is confirmed.
/// </summary>
sealed class PairForm : Form
{
    private readonly SyncPair? _existing;
    private readonly Config _config;
    private readonly Func<CancellationToken, Task<DriveClient>> _drive;

    private readonly ComboBox _kind = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
    private readonly ComboBox _nas = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 320 };
    private readonly TextBox _remote = new() { Width = 320 };
    private readonly TextBox _local = new() { Width = 320 };
    private readonly CheckBox _create = new() { Text = "Create the remote folder if it does not exist", AutoSize = true };
    private readonly ComboBox _docs = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 320 };
    private readonly NumericUpDown _cloudMb = new() { Minimum = 0, Maximum = 100000, Width = 80 };
    private readonly TextBox _cloudFolders = new() { Width = 320, PlaceholderText = "e.g. Archive, Video/Raw" };
    private readonly NumericUpDown _poll = new() { Minimum = 10, Maximum = 3600, Width = 80 };
    private readonly Label _hint = new() { AutoSize = true, MaximumSize = new Size(520, 0), ForeColor = SystemColors.GrayText };
    private readonly Button _ok = new() { Text = "OK", AutoSize = true };
    private readonly List<Control> _driveOnly = new();
    private readonly List<Control> _nasOnly = new();
    private List<SynologyCredentials> _logins = new();

    public SyncPair? Result { get; private set; }

    public PairForm(Config config, SyncPair? existing, Func<CancellationToken, Task<DriveClient>> drive)
    {
        _config = config;
        _existing = existing;
        _drive = drive;
        Text = existing == null ? "Add sync pair" : "Edit sync pair";
        Font = SystemFonts.MessageBoxFont;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(10);

        _kind.Items.AddRange(new object[] { "Google Drive", "Synology NAS" });
        _docs.Items.AddRange(new object[] { "Export to docx / xlsx / pptx (read-only copies)", "Link that opens the document in the browser", "Skip" });
        ReloadLogins();

        var grid = new TableLayoutPanel { AutoSize = true, ColumnCount = 3 };
        var row = 0;
        void Add(string label, Control field, Control? extra = null, List<Control>? group = null)
        {
            var l = Ui.Label(label);
            grid.Controls.Add(l, 0, row);
            grid.Controls.Add(field, 1, row);
            if (extra != null) grid.Controls.Add(extra, 2, row);
            group?.AddRange(extra == null ? new[] { l, field } : new[] { l, field, extra });
            row++;
        }
        var addLogin = Ui.Button("New login...", (_, _) => NewLogin());
        Add("Remote", _kind);
        Add("NAS login", _nas, addLogin, _nasOnly);
        Add("Remote folder", _remote, Ui.Button("Browse...", async (_, _) => await BrowseRemoteAsync()));
        grid.Controls.Add(_create, 1, row++);
        Add("Local folder", _local, Ui.Button("Browse...", (_, _) => BrowseLocal()));
        Add("Google Docs", _docs, group: _driveOnly);
        var mbRow = Ui.Row(_cloudMb, Ui.Label("MB and larger (0 = off)"));
        Add("Cloud-only files", mbRow, group: _driveOnly);
        Add("Cloud-only folders", _cloudFolders, group: _driveOnly);
        Add("Check remote every", Ui.Row(_poll, Ui.Label("seconds (local changes are picked up at once)")));
        grid.Controls.Add(_hint, 0, row); grid.SetColumnSpan(_hint, 3); row++;
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        grid.Controls.Add(Ui.Row(_ok, cancel), 1, row);
        Controls.Add(grid);
        AcceptButton = _ok;
        CancelButton = cancel;

        var p = existing ?? new SyncPair();
        _kind.SelectedIndex = p.Kind == RemoteKind.Synology ? 1 : 0;
        if (p.Server != null)
            _nas.SelectedIndex = _logins.FindIndex(c => c.Server.Equals(p.Server, StringComparison.OrdinalIgnoreCase));
        _remote.Text = p.RemotePath;
        _local.Text = p.LocalPath;
        _create.Checked = existing == null;
        _docs.SelectedIndex = (int)p.GoogleDocs;
        _cloudMb.Value = p.CloudOnlyMinMB;
        _cloudFolders.Text = string.Join(", ", p.CloudOnlyFolders);
        _poll.Value = Math.Clamp(p.PollSeconds, 10, 3600);
        _kind.SelectedIndexChanged += (_, _) => OnKindChanged(userChange: true);
        OnKindChanged(userChange: false);
        _ok.Click += async (_, _) => await AcceptAsync();
    }

    private bool IsNas => _kind.SelectedIndex == 1;

    private void OnKindChanged(bool userChange)
    {
        foreach (var c in _driveOnly) c.Enabled = !IsNas;
        foreach (var c in _nasOnly) c.Enabled = IsNas;
        if (userChange && _existing == null) _poll.Value = IsNas ? 60 : 30;
        _hint.Text = IsNas
            ? "NAS paths start with the shared folder, e.g. /home/Sync. Turn on the Recycle Bin for that shared folder in DSM so deletions can be undone."
            : "Drive paths are folders under My Drive, e.g. Projects/2026. Deletions go to the Drive trash.";
    }

    private void ReloadLogins()
    {
        _logins = SynologyCredentials.ListAll();
        _nas.Items.Clear();
        foreach (var c in _logins) _nas.Items.Add($"{c.User} @ {c.Server}");
        if (_nas.Items.Count > 0 && _nas.SelectedIndex < 0) _nas.SelectedIndex = 0;
    }

    private void NewLogin()
    {
        using var f = new NasLoginForm(new Log { ToConsole = false });
        if (f.ShowDialog(this) != DialogResult.OK || f.Result == null) return;
        ReloadLogins();
        _nas.SelectedIndex = _logins.FindIndex(c => c.Server == f.Result.Server);
    }

    private SynologyCredentials? SelectedLogin => _nas.SelectedIndex >= 0 ? _logins[_nas.SelectedIndex] : null;

    private async Task<SynologyClient> ConnectNasAsync(CancellationToken ct)
    {
        var creds = SelectedLogin ?? throw new InvalidOperationException("Choose or add a NAS login first.");
        var client = await SynologyClient.ConnectAsync(creds, creds.AllowInvalidCertificate,
            () => Ui.Prompt(this, "Two-factor code", "Enter the code from your authenticator app:"), ct);
        creds.Save();
        return client;
    }

    private async Task BrowseRemoteAsync()
    {
        UseWaitCursor = true;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            if (IsNas)
            {
                using var nas = await ConnectNasAsync(cts.Token);
                using var f = new RemoteFolderForm(new NasFolderSource(nas), "Choose a NAS folder");
                UseWaitCursor = false;
                if (f.ShowDialog(this) == DialogResult.OK) _remote.Text = f.SelectedPath;
                await nas.LogoutAsync(CancellationToken.None);
            }
            else
            {
                var drive = await _drive(cts.Token);
                using var f = new RemoteFolderForm(new DriveFolderSource(drive), "Choose a Google Drive folder");
                UseWaitCursor = false;
                if (f.ShowDialog(this) == DialogResult.OK) _remote.Text = f.SelectedPath;
            }
        }
        catch (Exception ex) { Ui.Error(this, Ui.Explain(ex)); }
        finally { UseWaitCursor = false; }
    }

    private void BrowseLocal()
    {
        using var d = new FolderBrowserDialog { Description = "Local folder to keep in sync", UseDescriptionForTitle = true, ShowNewFolderButton = true };
        if (Directory.Exists(_local.Text)) d.SelectedPath = _local.Text;
        if (d.ShowDialog(this) == DialogResult.OK) _local.Text = d.SelectedPath;
    }

    private SyncPair BuildPair()
    {
        // Start from a copy of the existing pair so settings the form does not show survive an edit.
        var p = _existing == null ? new SyncPair() : System.Text.Json.JsonSerializer.Deserialize<SyncPair>(System.Text.Json.JsonSerializer.Serialize(_existing))!;
        p.Kind = IsNas ? RemoteKind.Synology : RemoteKind.Drive;
        var remote = _remote.Text.Trim().Replace('\\', '/');
        p.RemotePath = IsNas ? "/" + remote.Trim('/') : remote.Trim('/');
        p.LocalPath = _local.Text.Trim().Length == 0 ? "" : Path.GetFullPath(_local.Text.Trim());
        if (IsNas)
        {
            var login = SelectedLogin;
            p.Server = login?.Server;
            p.User = login?.User;
            p.AllowInvalidCertificate = login?.AllowInvalidCertificate ?? false;
        }
        else
        {
            p.Server = null;
            p.User = null;
            p.AllowInvalidCertificate = false;
            p.GoogleDocs = (GoogleDocsMode)_docs.SelectedIndex;
            p.CloudOnlyMinMB = (int)_cloudMb.Value;
            p.CloudOnlyFolders = _cloudFolders.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => s.Replace('\\', '/').Trim('/')).Where(s => s.Length > 0).ToList();
        }
        p.PollSeconds = (int)_poll.Value;
        return p;
    }

    private string? CheckLocal(SyncPair p)
    {
        if (p.LocalPath.Length == 0) return "Choose a local folder.";
        if (Path.GetPathRoot(p.LocalPath)?.TrimEnd('\\').Length == p.LocalPath.TrimEnd('\\').Length) return "A whole drive cannot be a sync folder.";
        var mine = WithSep(p.LocalPath);
        if (mine.StartsWith(WithSep(AppPaths.DataDir), StringComparison.OrdinalIgnoreCase) || mine.StartsWith(WithSep(AppPaths.AuthDir), StringComparison.OrdinalIgnoreCase))
            return "The local folder cannot be inside Unisono's own data folder.";
        foreach (var other in _config.Pairs.Where(o => !ReferenceEquals(o, _existing)))
        {
            var theirs = WithSep(other.LocalPath);
            if (mine.StartsWith(theirs, StringComparison.OrdinalIgnoreCase) || theirs.StartsWith(mine, StringComparison.OrdinalIgnoreCase))
                return $"The local folder overlaps with the pair '{other.Name}' ({other.LocalPath}). Each local folder can belong to one pair only.";
        }
        if (IsNas && p.Server == null) return "Choose or add a NAS login.";
        if (IsNas && p.RemotePath.Trim('/').Split('/').Length < 2) return "Pick a folder inside a shared folder (e.g. /home/Sync), not a shared folder root.";
        return null;
    }

    private static string WithSep(string path) => path.TrimEnd('\\', '/') + "\\";

    private static string? OneDriveRoot(string path)
    {
        foreach (var v in new[] { "OneDrive", "OneDriveCommercial", "OneDriveConsumer" })
            if (Environment.GetEnvironmentVariable(v) is { Length: > 0 } root
                && WithSep(path).StartsWith(WithSep(root), StringComparison.OrdinalIgnoreCase))
                return root;
        return null;
    }

    private async Task AcceptAsync()
    {
        var p = BuildPair();
        if (CheckLocal(p) is { } problem) { Ui.Error(this, problem); return; }

        _ok.Enabled = false;
        UseWaitCursor = true;
        var hint = _hint.Text;
        _hint.Text = "Checking the remote folder...";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var ct = cts.Token;
            bool exists, remoteHasItems;
            Func<Task> createRemote;
            SynologyClient? nas = null;
            try
            {
                if (IsNas)
                {
                    nas = await ConnectNasAsync(ct);
                    var info = await nas.GetInfoAsync(p.RemotePath, ct);
                    if (info is { IsDir: false }) throw new InvalidOperationException($"{p.RemotePath} is a file on the NAS, not a folder.");
                    exists = info != null;
                    remoteHasItems = exists && (await nas.ListAsync(p.RemotePath, ct)).Any(e => !e.Name.StartsWith('#') && e.Name != "@eaDir");
                    var n = nas;
                    createRemote = () => new SynologyStore(n, p).EnsureRootAsync(ct);
                }
                else
                {
                    var drive = await _drive(ct);
                    try
                    {
                        var folder = await drive.ResolveFolderAsync(p.RemotePath, ct);
                        exists = true;
                        remoteHasItems = (await drive.ListChildrenAsync(folder.Id, ct)).Count > 0;
                    }
                    catch (DirectoryNotFoundException) { exists = false; remoteHasItems = false; }
                    createRemote = () => drive.EnsureFolderAsync(p.RemotePath, ct);
                }

                if (!exists && !_create.Checked)
                    throw new InvalidOperationException($"'{p.RemotePath}' does not exist remotely. Tick 'Create the remote folder' or pick an existing one.");

                var fresh = _existing == null || _existing.Id != p.Id; // a new state file: the first sync starts without history
                var localHasItems = Directory.Exists(p.LocalPath) && Directory.EnumerateFileSystemEntries(p.LocalPath).Any();
                _hint.Text = hint;
                UseWaitCursor = false;
                if (fresh && localHasItems && remoteHasItems && !Ui.Confirm(this,
                        "Both folders already contain files.\n\n" +
                        "On the first sync, files that exist on both sides with identical content are simply linked up. " +
                        "Files with the same name but different content are all kept: the local one is renamed " +
                        "'name (conflict <computer> <time>)' and uploaded next to the remote one.\n\n" +
                        "Nothing is deleted. Continue?"))
                    return;
                if (OneDriveRoot(p.LocalPath) is { } od && !Ui.Confirm(this,
                        $"The local folder is inside OneDrive ({od}).\n\nEverything Unisono downloads there is also uploaded to OneDrive " +
                        "and ends up in two clouds. Usually you want a folder outside OneDrive. Continue anyway?"))
                    return;

                if (!exists) await createRemote();
                Directory.CreateDirectory(p.LocalPath);
            }
            finally
            {
                if (nas != null) { await nas.LogoutAsync(CancellationToken.None); nas.Dispose(); }
            }
            Result = p;
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            _hint.Text = hint;
            Ui.Error(this, Ui.Explain(ex));
        }
        finally
        {
            UseWaitCursor = false;
            if (!IsDisposed) _ok.Enabled = true;
        }
    }
}
