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
    /// <param name="glyph">"U" (the letter), or "up", "down", "both" for transfers in progress.</param>
    public static Icon StatusIcon(Color color, string glyph = "U")
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);
            using var brush = new SolidBrush(color);
            g.FillEllipse(brush, 1, 1, 30, 30);
            if (glyph == "U")
            {
                using var font = new Font("Segoe UI", 19, FontStyle.Bold, GraphicsUnit.Pixel);
                using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString("U", font, Brushes.White, new RectangleF(0, 1, 32, 32), fmt);
            }
            else if (glyph == "both")
            {
                g.FillPolygon(Brushes.White, Arrow(10.5f, 5, 27, 11, up: true));
                g.FillPolygon(Brushes.White, Arrow(21.5f, 5, 27, 11, up: false));
            }
            else g.FillPolygon(Brushes.White, Arrow(16, 5, 27, 18, up: glyph == "up"));
        }
        var h = bmp.GetHicon();
        var icon = (Icon)Icon.FromHandle(h).Clone();
        DestroyIcon(h);
        return icon;
    }

    /// <summary>An arrow centred on x between y0 and y1, <paramref name="w"/> wide at the head.</summary>
    private static PointF[] Arrow(float x, float y0, float y1, float w, bool up)
    {
        var head = w * 0.55f; var stem = w * 0.18f;
        PointF P(float dx, float y) => new(x + dx, up ? y : y0 + y1 - y);
        return new[]
        {
            P(0, y0), P(w / 2, y0 + head), P(stem, y0 + head), P(stem, y1),
            P(-stem, y1), P(-stem, y0 + head), P(-w / 2, y0 + head),
        };
    }

    /// <summary>"820 B", "14 kB", "3.2 MB", "1.4 GB".</summary>
    public static string Size(long bytes) => bytes switch
    {
        < 1000 => $"{bytes} B",
        < 1_000_000 => $"{bytes / 1000.0:0} kB",
        < 1_000_000_000 => $"{bytes / 1_000_000.0:0.#} MB",
        _ => $"{bytes / 1_000_000_000.0:0.#} GB",
    };

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
