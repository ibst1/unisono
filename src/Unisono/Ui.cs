using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Unisono.App;

/// <summary>Native bits the web UI cannot do: status icons drawn at runtime, Explorer, and readable error texts.</summary>
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

    /// <summary>Turns the usual network and API failures into something a person can act on.</summary>
    public static string Explain(Exception ex) => ex switch
    {
        HttpRequestException { InnerException: System.Security.Authentication.AuthenticationException } =>
            "The server's certificate is not trusted, and nothing else answered.",
        HttpRequestException h => $"Could not reach the server: {h.Message}",
        TaskCanceledException or OperationCanceledException => "Cancelled or timed out.",
        Google.Apis.Auth.OAuth2.Responses.TokenResponseException t when t.Error?.Error == "invalid_grant" =>
            "Google no longer accepts this sign-in (revoked or expired). Remove the account and add it again.",
        _ => ex.Message,
    };

    public static void OpenFolder(string path)
    {
        try { System.Diagnostics.Process.Start("explorer.exe", $"\"{path}\""); } catch { }
    }
}
