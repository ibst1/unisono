using System.Security.Cryptography;
using System.Text;

namespace Unisono;

/// <summary>
/// Two processes syncing the same state files would fight over them, so only one may sync per data dir
/// (the tray app or 'UnisonoCli sync/run'). A named mutex is used for existence only, never owned, so it has
/// no thread affinity and vanishes with the process.
/// </summary>
public static class SyncLock
{
    /// <summary>Per data dir, so a test instance with UNISONO_DATA does not block the real one.</summary>
    public static string Suffix { get; } =
        Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(AppPaths.DataDir.ToLowerInvariant())))[..12];

    public static IDisposable? TryAcquire()
    {
        var m = new Mutex(false, @"Local\Unisono.Sync." + Suffix, out var created);
        if (created) return m;
        m.Dispose();
        return null;
    }
}
