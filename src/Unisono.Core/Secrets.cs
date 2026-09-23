using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Unisono;

/// <summary>
/// Credentials for a NAS, encrypted with DPAPI for the current Windows user and stored next to the OAuth tokens.
/// Another user on the machine, or a copy of the file, cannot decrypt it.
/// </summary>
public sealed class SynologyCredentials
{
    public string Server { get; set; } = "";
    public string User { get; set; } = "";
    public string Password { get; set; } = "";
    /// <summary>Device token from a completed two-factor login; lets later logins skip the OTP.</summary>
    public string? DeviceId { get; set; }
    /// <summary>Where the server actually answered (QuickConnect resolves to a relay or the LAN address).</summary>
    public string? ResolvedBase { get; set; }
    /// <summary>SHA-256 fingerprints of certificates the user reviewed and chose to trust for this NAS
    /// (DSM's self-signed one, or an expired QuickConnect one). Only these are accepted besides properly valid ones.</summary>
    public List<string> PinnedCertificates { get; set; } = new();
    /// <summary>Accept any certificate for this server (CLI --insecure). Pinning is the better choice.</summary>
    public bool AllowInvalidCertificate { get; set; }

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Unisono.Synology.v1");

    private static string Slug(string s) => string.Concat(s.Where(char.IsLetterOrDigit)).ToLowerInvariant();

    /// <summary>One file per server and user, so several DSM accounts (on one NAS or several) can be stored.</summary>
    private static string PathFor(string server, string user) =>
        Path.Combine(AppPaths.AuthDir, $"synology-{Slug(new Uri(server).Authority)}-{Slug(user)}.bin");

    /// <summary>Before multi-account support there was one file per server.</summary>
    private static string LegacyPathFor(string server) =>
        Path.Combine(AppPaths.AuthDir, "synology-" + string.Concat(new Uri(server).Authority.Where(char.IsLetterOrDigit)) + ".bin");

    public static SynologyCredentials? Load(string server, string? user)
    {
        if (user != null && File.Exists(PathFor(server, user))) return LoadFile(PathFor(server, user));
        if (File.Exists(LegacyPathFor(server)) && LoadFile(LegacyPathFor(server)) is { } legacy
            && (user == null || legacy.User.Equals(user, StringComparison.OrdinalIgnoreCase)))
            return legacy;
        return user == null ? ListAll().FirstOrDefault(c => c.Server.Equals(server, StringComparison.OrdinalIgnoreCase)) : null;
    }

    private static SynologyCredentials? LoadFile(string path)
    {
        var plain = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
        return JsonSerializer.Deserialize<SynologyCredentials>(plain);
    }
    /// <summary>All stored NAS logins (password included; do not display it).</summary>
    public static List<SynologyCredentials> ListAll()
    {
        var list = new List<SynologyCredentials>();
        if (!Directory.Exists(AppPaths.AuthDir)) return list;
        foreach (var f in Directory.EnumerateFiles(AppPaths.AuthDir, "synology-*.bin"))
        {
            try
            {
                if (LoadFile(f) is { } c && !list.Any(x => x.Server.Equals(c.Server, StringComparison.OrdinalIgnoreCase) && x.User.Equals(c.User, StringComparison.OrdinalIgnoreCase)))
                    list.Add(c);
            }
            catch { /* written by another Windows user or corrupt: not ours to show */ }
        }
        return list.OrderBy(c => c.Server, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public void Save()
    {
        AppPaths.EnsureDataDir();
        var plain = JsonSerializer.SerializeToUtf8Bytes(this);
        File.WriteAllBytes(PathFor(Server, User), ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser));
        // Migrate: the per-server file of the same login is replaced by the per-user one.
        try
        {
            var legacy = LegacyPathFor(Server);
            if (File.Exists(legacy) && LoadFile(legacy)?.User.Equals(User, StringComparison.OrdinalIgnoreCase) == true) File.Delete(legacy);
        }
        catch { }
    }

    public static void Delete(string server, string user)
    {
        if (File.Exists(PathFor(server, user))) File.Delete(PathFor(server, user));
        var legacy = LegacyPathFor(server);
        if (File.Exists(legacy) && LoadFile(legacy)?.User.Equals(user, StringComparison.OrdinalIgnoreCase) == true) File.Delete(legacy);
    }}
