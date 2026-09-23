namespace Unisono.App;

/// <summary>Test-logs in to DSM (asking for a two-factor code when needed) and stores the login DPAPI-encrypted.</summary>
sealed class NasLoginForm : Form
{
    private readonly TextBox _server = new() { Width = 320, PlaceholderText = "https://nas.example.com:5001" };
    private readonly TextBox _user = new() { Width = 180 };
    private readonly TextBox _password = new() { Width = 180, UseSystemPasswordChar = true };
    private readonly CheckBox _insecure = new() { Text = "Accept a self-signed certificate", AutoSize = true };
    private readonly Label _hint = new() { AutoSize = true, MaximumSize = new Size(420, 0), ForeColor = SystemColors.GrayText };
    private readonly Button _ok;
    private readonly Log _log;

    public SynologyCredentials? Result { get; private set; }

    public NasLoginForm(Log log, SynologyCredentials? existing = null)
    {
        _log = log;
        Text = "Synology NAS login";
        Font = SystemFonts.MessageBoxFont;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(10);

        if (existing != null)
        {
            _server.Text = existing.Server;
            _user.Text = existing.User;
            _insecure.Checked = existing.AllowInvalidCertificate;
        }
        _hint.Text = "DSM address with its HTTPS port (usually 5001), or https://<id>.quickconnect.to. " +
                     "The password is stored encrypted for your Windows user only. With two-factor on you are asked for a code once.";

        _ok = new Button { Text = "Log in", AutoSize = true };
        _ok.Click += async (_, _) => await LoginAsync();
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };

        var grid = new TableLayoutPanel { AutoSize = true, ColumnCount = 2 };
        grid.Controls.Add(Ui.Label("Server"), 0, 0); grid.Controls.Add(_server, 1, 0);
        grid.Controls.Add(Ui.Label("User"), 0, 1); grid.Controls.Add(_user, 1, 1);
        grid.Controls.Add(Ui.Label("Password"), 0, 2); grid.Controls.Add(_password, 1, 2);
        grid.Controls.Add(_insecure, 1, 3);
        grid.Controls.Add(_hint, 0, 4); grid.SetColumnSpan(_hint, 2);
        grid.Controls.Add(Ui.Row(_ok, cancel), 1, 5);
        Controls.Add(grid);
        AcceptButton = _ok;
        CancelButton = cancel;
    }

    private async Task LoginAsync()
    {
        var server = _server.Text.Trim().TrimEnd('/');
        if (server.Length == 0 || _user.Text.Trim().Length == 0 || _password.Text.Length == 0)
        {
            Ui.Error(this, "Fill in server, user and password.");
            return;
        }
        if (!server.Contains("://")) server = "https://" + server;
        if (!Uri.TryCreate(server, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            Ui.Error(this, "The server must be an https:// address.");
            return;
        }

        var creds = new SynologyCredentials
        {
            Server = uri.GetLeftPart(UriPartial.Authority),
            User = _user.Text.Trim(),
            Password = _password.Text,
            AllowInvalidCertificate = _insecure.Checked,
        };
        _ok.Enabled = false;
        UseWaitCursor = true;
        _hint.Text = "Logging in...";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            using var nas = await SynologyClient.ConnectAsync(creds, creds.AllowInvalidCertificate,
                () => Ui.Prompt(this, "Two-factor code", "Enter the code from your authenticator app:"), cts.Token);
            var shares = await nas.ListAsync("/", cts.Token);
            await nas.LogoutAsync(cts.Token);
            creds.Save();
            _log.Info($"NAS login stored for {creds.User} @ {creds.Server} (answered at {nas.BaseUrl}); shared folders: {string.Join(", ", shares.Select(s => s.Path))}");
            Result = creds;
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            _hint.Text = "";
            Ui.Error(this, Ui.Explain(ex));
        }
        finally
        {
            UseWaitCursor = false;
            if (!IsDisposed) _ok.Enabled = true;
        }
    }
}
