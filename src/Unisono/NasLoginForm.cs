namespace Unisono.App;

/// <summary>Test-logs in to DSM (asking for a two-factor code when needed) and stores the login DPAPI-encrypted.</summary>
sealed class NasLoginForm : Form
{
    private readonly TextBox _server = new() { Width = 320, PlaceholderText = "QuickConnect ID, or https://nas.example.com:5001" };
    private readonly TextBox _user = new() { Width = 180 };
    private readonly TextBox _password = new() { Width = 180, UseSystemPasswordChar = true };
    private readonly Label _hint = new() { AutoSize = true, MaximumSize = new Size(420, 0), ForeColor = SystemColors.GrayText };
    private readonly Button _ok;
    private readonly Log _log;
    private List<string> _pins = new();
    private bool _allowAny;

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
            _pins = existing.PinnedCertificates.ToList();
            _allowAny = existing.AllowInvalidCertificate;
        }
        _hint.Text = "Your QuickConnect ID (as in DSM, Control Panel > External Access), or the DSM address with its HTTPS port (usually 5001). " +
                     "The password is stored encrypted for your Windows user only. With two-factor on you are asked for a code once.";

        _ok = new Button { Text = "Log in", AutoSize = true };
        _ok.Click += async (_, _) => await LoginAsync();
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };

        var grid = new TableLayoutPanel { AutoSize = true, ColumnCount = 2 };
        grid.Controls.Add(Ui.Label("Server"), 0, 0); grid.Controls.Add(_server, 1, 0);
        grid.Controls.Add(Ui.Label("User"), 0, 1); grid.Controls.Add(_user, 1, 1);
        grid.Controls.Add(Ui.Label("Password"), 0, 2); grid.Controls.Add(_password, 1, 2);
        grid.Controls.Add(_hint, 0, 4); grid.SetColumnSpan(_hint, 2);
        grid.Controls.Add(Ui.Row(_ok, cancel), 1, 5);
        Controls.Add(grid);
        AcceptButton = _ok;
        CancelButton = cancel;
    }

    private async Task LoginAsync()
    {
        if (_server.Text.Trim().Length == 0 || _user.Text.Trim().Length == 0 || _password.Text.Length == 0)
        {
            Ui.Error(this, "Fill in server, user and password.");
            return;
        }
        string server;
        try { server = SynologyClient.NormalizeServer(_server.Text); }
        catch (UriFormatException) { Ui.Error(this, "That is not a server address or QuickConnect ID."); return; }
        if (!server.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            Ui.Error(this, "Use an https:// address; the password must not travel unencrypted.");
            return;
        }

        var creds = new SynologyCredentials
        {
            Server = server,
            User = _user.Text.Trim(),
            Password = _password.Text,
            AllowInvalidCertificate = _allowAny,
            PinnedCertificates = _pins.ToList(),
        };
        _ok.Enabled = false;
        UseWaitCursor = true;
        _hint.Text = QuickConnect.IdFrom(server) != null ? "Looking up the NAS via QuickConnect..." : "Logging in...";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            SynologyClient nas;
            while (true)
            {
                try
                {
                    nas = await SynologyClient.ConnectAsync(creds, creds.AllowInvalidCertificate,
                        () => Ui.Prompt(this, "Two-factor code", "Enter the code from your authenticator app:"), cts.Token, _log.Info);
                    break;
                }
                catch (UntrustedCertificateException ex)
                {
                    var certs = ex.Distinct.Where(c => !creds.PinnedCertificates.Contains(c.Sha256, StringComparer.OrdinalIgnoreCase)).ToList();
                    if (certs.Count == 0) throw;
                    var text =
                        "The NAS answered, but with a certificate Windows does not trust:\n\n" +
                        string.Join("\n\n", certs.Select(c => c.Describe())) +
                        "\n\nThis is expected for DSM's own self-signed certificate, and happens when DSM failed to renew its " +
                        "QuickConnect certificate (renew it in DSM: Control Panel > Security > Certificate).\n\n" +
                        "Trust exactly these certificates for this NAS? Unisono will accept them and no other untrusted ones.";
                    UseWaitCursor = false;
                    if (MessageBox.Show(this, text, "Unisono: untrusted certificate", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                            MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                    {
                        _hint.Text = "";
                        return;
                    }
                    UseWaitCursor = true;
                    creds.PinnedCertificates.AddRange(certs.Select(c => c.Sha256));
                    _pins = creds.PinnedCertificates.ToList();
                    _log.Info($"trusting {certs.Count} certificate(s) for {creds.Server}: {string.Join(", ", certs.Select(c => c.Sha256[..16] + "..."))}");
                    _hint.Text = "Trying again with the trusted certificate...";
                }
            }
            using (nas)
            {
                var shares = await nas.ListAsync("/", cts.Token);
                await nas.LogoutAsync(cts.Token);
                creds.Save();
                _log.Info($"NAS login stored for {creds.User} @ {creds.Server} (answered at {nas.BaseUrl}); shared folders: {string.Join(", ", shares.Select(s => s.Path))}");
            }
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
