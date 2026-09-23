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

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Unisono.Synology.v1");

    private static string PathFor(string server) =>
        Path.Combine(AppPaths.AuthDir, "synology-" + string.Concat(new Uri(server).Host.Where(char.IsLetterOrDigit)) + ".bin");

    public static SynologyCredentials? Load(string server)
    {
        var path = PathFor(server);
        if (!File.Exists(path)) return null;
        var plain = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
        return JsonSerializer.Deserialize<SynologyCredentials>(plain);
    }

    public void Save()
    {
        AppPaths.EnsureDataDir();
        var plain = JsonSerializer.SerializeToUtf8Bytes(this);
        File.WriteAllBytes(PathFor(Server), ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser));
    }

    public static void Delete(string server)
    {
        var path = PathFor(server);
        if (File.Exists(path)) File.Delete(path);
    }
}
