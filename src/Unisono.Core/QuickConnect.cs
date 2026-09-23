using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Unisono;

/// <summary>
/// Resolves a Synology QuickConnect ID to an address where the DSM API answers, the way DSM's own portal page does
/// in the browser: ask Synology's directory (global.quickconnect.to/Serv.php) where the NAS is, probe the LAN,
/// DDNS/FQDN, external and *.direct.quickconnect.to addresses, and fall back to Synology's relay.
/// https://id.quickconnect.to itself only serves that HTML portal page, never the API.
/// </summary>
public static class QuickConnect
{
    /// <summary>"myid", "myid.quickconnect.to", "https://myid.quickconnect.to" or "quickconnect.to/myid" give "myid"; anything else null.</summary>
    public static string? IdFrom(string input)
    {
        var s = Regex.Replace(input.Trim().TrimEnd('/'), "^[a-z]+://", "", RegexOptions.IgnoreCase);
        var m = Regex.Match(s, @"^quickconnect\.to/([A-Za-z0-9-]+)$", RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value;
        m = Regex.Match(s, @"^([A-Za-z0-9-]+)\.quickconnect\.to$", RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value;
        // A bare word is a QuickConnect ID; a LAN name would come with a port or a dot.
        if (Regex.IsMatch(s, "^[A-Za-z][A-Za-z0-9-]*$") && !s.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return s;
        return null;
    }

    public static async Task<string> ResolveAsync(HttpClient http, string id, Func<string, CancellationToken, Task<bool>> probe,
        Action<string>? trace, CancellationToken ct)
    {
        var info = await ServAsync(http, "https://global.quickconnect.to/Serv.php", "get_server_info", id, ct);
        var errno = info.TryGetProperty("errno", out var e) ? e.GetInt32() : -1;
        if (errno != 0)
        {
            var why = info.TryGetProperty("errinfo", out var ei) ? ei.GetString() : null;
            throw new InvalidOperationException(errno == 4
                ? $"QuickConnect ID '{id}' was not found. Check it in DSM: Control Panel > External Access > QuickConnect."
                : $"QuickConnect lookup for '{id}' failed (errno {errno}{(why != null ? ", " + why : "")}).");
        }

        var candidates = Candidates(info);
        trace?.Invoke($"QuickConnect '{id}': trying {string.Join(", ", candidates)}");
        var found = await FirstWorkingAsync(candidates, probe, ct);
        if (found != null) return found;

        // Nothing direct answered (typical away from home without port forwarding): go through Synology's relay.
        // The relay passes TLS through to the NAS, so the NAS's own certificate is what the client sees.
        if (Relay(info) is { } relay)
        {
            trace?.Invoke($"QuickConnect '{id}': trying relay {relay}");
            if (await probe(relay, ct)) return relay;
            candidates.Add(relay);
        }
        var controlHost = info.TryGetProperty("env", out var env) && env.TryGetProperty("control_host", out var ch) ? ch.GetString() : null;
        if (!string.IsNullOrEmpty(controlHost))
        {
            // Ask for a (fresh) tunnel; the answer names the relay host and port to use.
            var tunnel = await ServAsync(http, $"https://{controlHost}/Serv.php", "request_tunnel", id, ct);
            var terr = tunnel.TryGetProperty("errno", out var te) ? te.GetInt32() : -1;
            if (terr == 0 && Relay(tunnel) is { } fresh)
            {
                trace?.Invoke($"QuickConnect '{id}': trying relay {fresh}");
                if (await probe(fresh, ct)) return fresh;
                if (!candidates.Contains(fresh)) candidates.Add(fresh);
            }
            else if (terr != 0) trace?.Invoke($"QuickConnect '{id}': relay tunnel refused (errno {terr})");
        }
        throw new InvalidOperationException(
            $"QuickConnect found the NAS '{id}' but none of its addresses answered, not even through the relay ({string.Join(", ", candidates)}). " +
            "Check that QuickConnect is enabled with 'DSM' allowed under Advanced settings, or use a DDNS address with port 5001 forwarded.");
    }

    /// <summary>The relay endpoint from a get_server_info / request_tunnel answer: https://synr-xx.ID.direct.quickconnect.to:port.</summary>
    private static string? Relay(JsonElement info)
    {
        var service = info.TryGetProperty("service", out var sv) ? sv : default;
        var host = Str(service, "relay_dn") ?? Str(service, "relay_dualstack");
        var port = Int(service, "relay_port");
        return string.IsNullOrEmpty(host) || host == "NULL" || port <= 0 ? null : $"https://{host}:{port}";
    }

    private static async Task<JsonElement> ServAsync(HttpClient http, string url, string command, string id, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new[]
        {
            new Dictionary<string, object>
            {
                ["version"] = 1, ["command"] = command, ["stop_when_error"] = false, ["stop_when_success"] = true,
                ["id"] = "dsm_portal_https", ["serverID"] = id, ["is_gofile"] = false, ["path"] = "",
            },
        });
        using var res = await http.PostAsync(url, new StringContent(body, Encoding.UTF8, "application/json"), ct);
        res.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement[0] : doc.RootElement;
        return root.Clone();
    }

    /// <summary>Most likely first: hostnames with Synology-issued certificates, then LAN, then the public side.</summary>
    private static List<string> Candidates(JsonElement info)
    {
        var list = new List<string>();
        void Add(string? host, int port)
        {
            if (string.IsNullOrWhiteSpace(host) || host == "NULL" || port <= 0) return;
            var h = host.Contains(':') && !host.StartsWith('[') ? $"[{host}]" : host; // bare IPv6
            var url = $"https://{h}:{port}";
            if (!list.Contains(url)) list.Add(url);
        }

        var service = info.TryGetProperty("service", out var sv) ? sv : default;
        var port = Int(service, "port") is > 0 and var p ? p : 5001;
        var ext = Int(service, "ext_port") is > 0 and var x ? x : port;
        var server = info.TryGetProperty("server", out var s) ? s : default;
        var smart = info.TryGetProperty("smartdns", out var sd) ? sd : default;

        if (smart.ValueKind == JsonValueKind.Object && smart.TryGetProperty("lan", out var lan) && lan.ValueKind == JsonValueKind.Array)
            foreach (var h in lan.EnumerateArray()) Add(h.GetString(), port);
        if (server.ValueKind == JsonValueKind.Object && server.TryGetProperty("interface", out var ifs) && ifs.ValueKind == JsonValueKind.Array)
            foreach (var i in ifs.EnumerateArray()) Add(Str(i, "ip"), port);
        Add(Str(server, "ddns"), ext);
        Add(Str(server, "fqdn"), ext);
        Add(Str(smart, "host"), ext);
        Add(Str(smart, "external"), ext);
        if (server.ValueKind == JsonValueKind.Object && server.TryGetProperty("external", out var exo)) Add(Str(exo, "ip"), ext);
        return list;
    }

    /// <summary>Probes all candidates at once (LAN addresses time out elsewhere) and returns the best-ranked one that answered.</summary>
    private static async Task<string?> FirstWorkingAsync(List<string> candidates, Func<string, CancellationToken, Task<bool>> probe, CancellationToken ct)
    {
        var tasks = candidates.Select(async c => { try { return await probe(c, ct); } catch { return false; } }).ToList();
        await Task.WhenAll(tasks);
        for (var i = 0; i < candidates.Count; i++)
            if (tasks[i].Result) return candidates[i];
        return null;
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int Int(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
}
