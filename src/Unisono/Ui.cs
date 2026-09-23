using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Unisono.App;

/// <summary>Small WinForms helpers: status icons drawn at runtime, a text prompt, and layout shorthands.</summary>
static class Ui
{
    public static string Version { get; } =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";

    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr handle);

    /// <summary>A filled circle with a white "U"; the colour carries the state.</summary>
    public static Icon StatusIcon(Color color)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);
            using var brush = new SolidBrush(color);
            g.FillEllipse(brush, 1, 1, 30, 30);
            using var font = new Font("Segoe UI", 19, FontStyle.Bold, GraphicsUnit.Pixel);
            using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString("U", font, Brushes.White, new RectangleF(0, 1, 32, 32), fmt);
        }
        var h = bmp.GetHicon();
        var icon = (Icon)Icon.FromHandle(h).Clone();
        DestroyIcon(h);
        return icon;
    }

    public static readonly Color Green = Color.FromArgb(46, 139, 87);
    public static readonly Color Blue = Color.FromArgb(30, 110, 200);
    public static readonly Color Amber = Color.FromArgb(215, 140, 0);
    public static readonly Color Red = Color.FromArgb(200, 50, 50);
    public static readonly Color Grey = Color.FromArgb(120, 120, 120);

    public static Button Button(string text, EventHandler click)
    {
        var b = new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(6, 1, 6, 1) };
        b.Click += click;
        return b;
    }

    public static FlowLayoutPanel Row(params Control[] controls)
    {
        var p = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0), Dock = DockStyle.Fill };
        p.Controls.AddRange(controls);
        return p;
    }

    public static Label Label(string text) =>
        new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 7, 3, 3) };

    /// <summary>Modal single-line prompt; null when cancelled.</summary>
    public static string? Prompt(IWin32Window? owner, string title, string text, bool password = false)
    {
        using var f = new Form
        {
            Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(10), Font = SystemFonts.MessageBoxFont,
        };
        var box = new TextBox { Width = 260, UseSystemPasswordChar = password };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        var layout = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        layout.Controls.Add(new Label { Text = text, AutoSize = true, MaximumSize = new Size(300, 0) });
        layout.Controls.Add(box);
        layout.Controls.Add(Row(ok, cancel));
        f.Controls.Add(layout);
        f.AcceptButton = ok;
        f.CancelButton = cancel;
        return f.ShowDialog(owner) == DialogResult.OK ? box.Text.Trim() : null;
    }

    public static void Error(IWin32Window? owner, string message) =>
        MessageBox.Show(owner, message, "Unisono", MessageBoxButtons.OK, MessageBoxIcon.Warning);

    public static bool Confirm(IWin32Window? owner, string message) =>
        MessageBox.Show(owner, message, "Unisono", MessageBoxButtons.OKCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.OK;

    /// <summary>Turns the usual network and API failures into something a person can act on.</summary>
    public static string Explain(Exception ex) => ex switch
    {
        HttpRequestException { InnerException: System.Security.Authentication.AuthenticationException } =>
            "The server's certificate is not trusted. If the NAS uses a self-signed certificate, tick 'Accept a self-signed certificate'.",
        HttpRequestException h => $"Could not reach the server: {h.Message}",
        TaskCanceledException => "The server did not answer in time.",
        _ => ex.Message,
    };

    public static void OpenFolder(string path)
    {
        try { System.Diagnostics.Process.Start("explorer.exe", $"\"{path}\""); } catch { }
    }
}
