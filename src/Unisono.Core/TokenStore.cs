using Google.Apis.Json;
using Google.Apis.Util.Store;

namespace Unisono;

/// <summary>
/// Drop-in replacement for the client library's FileDataStore (same folder and file names, so existing sign-ins keep
/// working) that is safe when several Unisono processes use the same account at once: the tray, an Explorer
/// "Open in..." or "Share..." window and the CLI may all refresh the access token around the same moment.
/// FileDataStore writes in place and fails with "being used by another process"; this one serialises writers with a
/// named mutex per file, writes to a temporary file and swaps it in, and retries reads that hit a swap in progress.
/// </summary>
public sealed class TokenStore : IDataStore
{
    private readonly string _dir;

    public TokenStore(string dir)
    {
        _dir = dir;
        Directory.CreateDirectory(dir);
    }

    private string PathFor<T>(string key) => Path.Combine(_dir, FileDataStore.GenerateStoredKey(key, typeof(T)));

    public Task StoreAsync<T>(string key, T value)
    {
        var path = PathFor<T>(key);
        var json = NewtonsoftJsonSerializer.Instance.Serialize(value);
        Locked(path, () =>
        {
            var tmp = path + "." + Environment.ProcessId + ".tmp";
            File.WriteAllText(tmp, json);
            Retry(() => File.Move(tmp, path, overwrite: true));
        });
        return Task.CompletedTask;
    }

    public Task<T> GetAsync<T>(string key)
    {
        var path = PathFor<T>(key);
        string? json = null;
        Retry(() => json = File.Exists(path) ? File.ReadAllText(path) : null);
        return Task.FromResult(json == null ? default! : NewtonsoftJsonSerializer.Instance.Deserialize<T>(json));
    }

    public Task DeleteAsync<T>(string key)
    {
        var path = PathFor<T>(key);
        Locked(path, () => Retry(() => File.Delete(path)));
        return Task.CompletedTask;
    }

    public Task ClearAsync()
    {
        foreach (var f in Directory.EnumerateFiles(_dir)) Locked(f, () => Retry(() => File.Delete(f)));
        return Task.CompletedTask;
    }

    private static void Locked(string path, Action action)
    {
        // Named mutexes live in the session namespace; the name only needs to be the same in every process.
        var name = @"Local\Unisono.Token." + Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(
            System.Text.Encoding.UTF8.GetBytes(path.ToUpperInvariant())));
        using var mutex = new Mutex(false, name);
        var owned = false;
        try
        {
            try { owned = mutex.WaitOne(TimeSpan.FromSeconds(10)); }
            catch (AbandonedMutexException) { owned = true; } // the other process died mid-write; the swap keeps the file whole
            action();
        }
        finally
        {
            if (owned) mutex.ReleaseMutex();
        }
    }

    /// <summary>Virus scanners and a concurrent swap can hold the file for a moment.</summary>
    private static void Retry(Action action)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { action(); return; }
            catch (IOException) when (attempt < 10) { Thread.Sleep(50 * attempt); }
            catch (UnauthorizedAccessException) when (attempt < 10) { Thread.Sleep(50 * attempt); }
        }
    }
}
