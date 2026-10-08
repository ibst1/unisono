using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Unisono;

/// <summary>
/// Whether a newer Unisono has been released on GitHub: one anonymous request for the repository's latest release
/// (drafts and pre-releases left out). Nothing is downloaded; the window links to the release page. A repository
/// without releases, a private one or no network answers null.
/// </summary>
public static class UpdateCheck
{
    public const string Repo = "ibst1/unisono";

    public sealed record Release(string Version, string Url, string Name);

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Unisono-update-check", "1"));
        c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return c;
    }

    public static async Task<Release?> LatestAsync(CancellationToken ct, string repo = Repo)
    {
        using var resp = await Http.GetAsync($"https://api.github.com/repos/{repo}/releases/latest", ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var r = doc.RootElement;
        var tag = r.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
        var url = r.TryGetProperty("html_url", out var u) ? u.GetString() ?? "" : "";
        var name = r.TryGetProperty("name", out var n) ? n.GetString() ?? tag : tag;
        return tag.Length == 0 || !url.StartsWith("https://github.com/", StringComparison.Ordinal) ? null : new Release(tag.TrimStart('v', 'V'), url, name);
    }

    /// <summary>Whether <paramref name="candidate"/> (e.g. "0.2.90") is a later version than <paramref name="running"/> ("0.2.82+37da45a").</summary>
    public static bool IsNewer(string candidate, string running)
    {
        static int[] Parts(string v) => v.Split('+')[0].Split('-')[0].Split('.').Select(p => int.TryParse(p, out var i) ? i : 0).ToArray();
        var a = Parts(candidate); var b = Parts(running);
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var x = i < a.Length ? a[i] : 0; var y = i < b.Length ? b[i] : 0;
            if (x != y) return x > y;
        }
        return false;
    }
}
