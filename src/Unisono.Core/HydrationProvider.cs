using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Unisono;

/// <summary>
/// The connected side of a files-on-demand pair: when something opens an online-only file, Windows asks for its
/// content here. The callback only records the request and returns; the download runs on the thread pool and the
/// bytes are handed to Windows in chunks, after which the file is an ordinary local (hydrated) placeholder.
/// </summary>
public sealed class HydrationProvider : IDisposable
{
    /// <summary>(relative path, identity, size) -> a local temp file with the content; the provider deletes it afterwards.</summary>
    public delegate Task<string> FetchFunc(string rel, string identity, long size, CancellationToken ct);

    private static readonly ConcurrentDictionary<long, HydrationProvider> Connections = new();
    private const int Chunk = 4 * 1024 * 1024; // a multiple of 4 KB, as Windows requires for all but the last piece

    private readonly string _root;
    private readonly string _rootNoDrive;
    private readonly FetchFunc _fetch;
    private readonly Log _log;
    private readonly string _name;
    private readonly CancellationTokenSource _cts = new();
    private long _key;

    private HydrationProvider(string root, FetchFunc fetch, Log log, string name)
    {
        _root = root.TrimEnd('\\');
        _rootNoDrive = _root.Length > 2 && _root[1] == ':' ? _root[2..] : _root;
        _fetch = fetch;
        _log = log;
        _name = name;
    }

    public static unsafe HydrationProvider Connect(string root, FetchFunc fetch, Log log, string name)
    {
        var p = new HydrationProvider(root, fetch, log, name);
        var table = stackalloc CallbackRegistration[2];
        table[0] = new CallbackRegistration { Type = 0 /* FETCH_DATA */, Callback = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, void>)&OnFetchData };
        table[1] = new CallbackRegistration { Type = unchecked((int)0xffffffff), Callback = IntPtr.Zero };
        var hr = CfConnectSyncRoot(root, table, IntPtr.Zero, 0x4 /* REQUIRE_FULL_FILE_PATH */, out p._key);
        if (hr < 0) throw new IOException($"cannot serve online-only files in {root}: {Marshal.GetExceptionForHR(hr)?.Message}", hr);
        Connections[p._key] = p;
        return p;
    }

    public void Dispose()
    {
        _cts.Cancel();
        if (_key != 0)
        {
            Connections.TryRemove(_key, out _);
            CfDisconnectSyncRoot(_key);
            _key = 0;
        }
    }

    private sealed record Request(long ConnectionKey, long TransferKey, long RequestKey, string Path, string Identity, long FileSize, long Offset, long Length);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static unsafe void OnFetchData(IntPtr info, IntPtr parameters)
    {
        try
        {
            var ci = (byte*)info;
            var pp = (byte*)parameters;
            var key = *(long*)(ci + 8);
            if (!Connections.TryGetValue(key, out var provider)) return;
            var idPtr = *(IntPtr*)(ci + 88);
            var idLen = *(uint*)(ci + 96);
            var req = new Request(
                key, *(long*)(ci + 112), *(long*)(ci + 144),
                Marshal.PtrToStringUni(*(IntPtr*)(ci + 104)) ?? "",
                idPtr == IntPtr.Zero || idLen == 0 ? "" : new string((char*)idPtr, 0, (int)idLen / 2),
                *(long*)(ci + 80), *(long*)(pp + 16), *(long*)(pp + 24));
            _ = Task.Run(() => provider.ServeAsync(req));
        }
        catch { /* never let an exception cross into Windows */ }
    }

    private async Task ServeAsync(Request r)
    {
        var rel = RelOf(r.Path);
        string? tmp = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            timeout.CancelAfter(TimeSpan.FromMinutes(30));
            tmp = await _fetch(rel, r.Identity, r.FileSize, timeout.Token);
            await using var fs = new FileStream(tmp, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
            if (fs.Length != r.FileSize)
                throw new IOException($"the remote file is {fs.Length:N0} bytes, the placeholder says {r.FileSize:N0}; it changed remotely and will be updated on the next sync");
            var buf = new byte[Chunk];
            long offset = 0;
            while (offset < r.FileSize)
            {
                var n = await fs.ReadAtLeastAsync(buf, (int)Math.Min(Chunk, r.FileSize - offset), throwOnEndOfStream: true, timeout.Token);
                Transfer(r, buf, offset, n, 0);
                offset += n;
            }
            if (r.FileSize == 0) Transfer(r, buf, 0, 0, 0);
            _log.Info($"[{_name}] downloaded on open: {rel} ({r.FileSize:N0} bytes)");
        }
        catch (Exception ex)
        {
            _log.Warn($"[{_name}] could not download {rel} on open: {ex.Message}");
            try { Transfer(r, null, r.Offset, r.Length, unchecked((int)0xC0000001) /* STATUS_UNSUCCESSFUL */); } catch { }
        }
        finally
        {
            if (tmp != null) try { File.Delete(tmp); } catch { }
        }
    }

    /// <summary>NormalizedPath is volume-relative ("\Users\me\Unisono\Drive\a\b.txt").</summary>
    private string RelOf(string normalized)
    {
        var p = normalized.StartsWith(_rootNoDrive + "\\", StringComparison.OrdinalIgnoreCase) ? normalized[(_rootNoDrive.Length + 1)..] : Path.GetFileName(normalized);
        return p.Replace('\\', '/');
    }

    private static unsafe void Transfer(Request r, byte[]? buf, long offset, long length, int status)
    {
        var op = stackalloc byte[48];
        new Span<byte>(op, 48).Clear();
        *(uint*)op = 48;                   // StructSize
        *(int*)(op + 4) = 0;               // CF_OPERATION_TYPE_TRANSFER_DATA
        *(long*)(op + 8) = r.ConnectionKey;
        *(long*)(op + 16) = r.TransferKey;
        *(long*)(op + 40) = r.RequestKey;
        var prm = stackalloc byte[40];
        new Span<byte>(prm, 40).Clear();
        *(uint*)prm = 40;                  // ParamSize
        *(int*)(prm + 12) = status;        // CompletionStatus
        *(long*)(prm + 24) = offset;
        *(long*)(prm + 32) = length;
        fixed (byte* b = buf)
        {
            *(IntPtr*)(prm + 16) = (IntPtr)b;
            var hr = CfExecute((IntPtr)op, (IntPtr)prm);
            if (hr < 0) throw new IOException($"Windows refused the data: {Marshal.GetExceptionForHR(hr)?.Message}", hr);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CallbackRegistration { public int Type; public IntPtr Callback; }

    [DllImport("cldapi.dll", CharSet = CharSet.Unicode)]
    private static extern unsafe int CfConnectSyncRoot(string path, CallbackRegistration* table, IntPtr context, int flags, out long key);
    [DllImport("cldapi.dll")] private static extern int CfDisconnectSyncRoot(long key);
    [DllImport("cldapi.dll")] private static extern int CfExecute(IntPtr opInfo, IntPtr opParams);
}
