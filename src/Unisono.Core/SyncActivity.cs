namespace Unisono;

/// <summary>
/// What is being transferred right now, how far each transfer has come, and how much has been transferred since start:
/// for the tray icon (arrows), its tooltip and the window's transfer list. Written by the sync engine (and, through
/// <see cref="ProgressStream"/>, by the stores while bytes flow), read by the app; thread-safe.
/// </summary>
public static class SyncActivity
{
    public enum Direction { Up, Down }

    /// <summary>One running transfer. <see cref="Done"/> grows while the store moves bytes (when it reports them).</summary>
    public sealed class Transfer
    {
        public Direction Dir { get; }
        public string Pair { get; }
        public string Rel { get; }
        public long Size { get; }
        public DateTime Started { get; } = DateTime.UtcNow;
        private long _done;
        private long _lastRaise;

        internal Transfer(Direction dir, string pair, string rel, long size) { Dir = dir; Pair = pair; Rel = rel; Size = size; }

        public long Done => Interlocked.Read(ref _done);

        /// <summary>Bytes per second so far (0 in the first moment).</summary>
        public double Speed => (DateTime.UtcNow - Started).TotalSeconds is var s && s > 0.5 ? Done / s : 0;

        public void Report(long done)
        {
            Interlocked.Exchange(ref _done, done);
            // Listeners redraw on Changed: a few times a second per transfer is plenty.
            var now = Environment.TickCount64;
            var last = Interlocked.Read(ref _lastRaise);
            if (now - last >= 300 && Interlocked.CompareExchange(ref _lastRaise, now, last) == last) Raise();
        }
    }

    public sealed record Snapshot(IReadOnlyList<Transfer> Active, int Uploaded, int Downloaded, long UpBytes, long DownBytes);

    private static readonly List<Transfer> Active = new();
    private static int _uploaded, _downloaded;
    private static long _upBytes, _downBytes;
    private static readonly AsyncLocal<Transfer?> Current = new();

    /// <summary>The transfer the calling code runs for (set by <see cref="Begin"/> for everything it awaits), or null.</summary>
    public static Transfer? CurrentTransfer => Current.Value;

    /// <summary>Raised on every start and end of a transfer and, throttled, on its progress (on the transferring thread).</summary>
    public static event Action? Changed;

    /// <summary>
    /// Marks a transfer as running until the returned object is disposed; counted when it ends without error. Call it in
    /// the async method that awaits the transfer: the transfer is then <see cref="CurrentTransfer"/> for all it awaits.
    /// </summary>
    public static Tracker Begin(Direction dir, string pair, string rel, long size)
    {
        var t = new Transfer(dir, pair, rel, size);
        lock (Active) Active.Add(t);
        Current.Value = t;
        Raise();
        return new Tracker(t);
    }

    public static Snapshot Now()
    {
        lock (Active) return new Snapshot(Active.ToList(), _uploaded, _downloaded, _upBytes, _downBytes);
    }

    private static void Raise()
    {
        try { Changed?.Invoke(); } catch { /* a listener's problem, not the transfer's */ }
    }

    public sealed class Tracker : IDisposable
    {
        private readonly Transfer _t;
        private bool _done, _ok;

        internal Tracker(Transfer t) => _t = t;

        /// <summary>Call when the transfer succeeded; a tracker disposed without it counts as failed.</summary>
        public void Complete() => _ok = true;

        public void Dispose()
        {
            if (_done) return;
            _done = true;
            if (Current.Value == _t) Current.Value = null;
            lock (Active)
            {
                Active.Remove(_t);
                if (_ok && _t.Dir == Direction.Up) { _uploaded++; _upBytes += _t.Size; }
                if (_ok && _t.Dir == Direction.Down) { _downloaded++; _downBytes += _t.Size; }
            }
            Raise();
        }
    }
}

/// <summary>
/// Passes a stream through and reports the bytes read or written to the current transfer (<see cref="SyncActivity.CurrentTransfer"/>),
/// for the progress of large files. Seeking (a resumed upload) moves the count along.
/// </summary>
public sealed class ProgressStream : Stream
{
    private readonly Stream _inner;
    private readonly SyncActivity.Transfer? _transfer = SyncActivity.CurrentTransfer;
    private long _count;

    public ProgressStream(Stream inner) => _inner = inner;

    private void Add(int n)
    {
        if (n <= 0) return;
        _count += n;
        _transfer?.Report(_count);
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => _inner.CanWrite;
    public override long Length => _inner.Length;

    public override long Position
    {
        get => _inner.Position;
        set { _inner.Position = value; _count = value; _transfer?.Report(_count); }
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        var p = _inner.Seek(offset, origin);
        _count = p;
        _transfer?.Report(_count);
        return p;
    }

    public override int Read(byte[] buffer, int offset, int count) { var n = _inner.Read(buffer, offset, count); Add(n); return n; }
    public override int Read(Span<byte> buffer) { var n = _inner.Read(buffer); Add(n); return n; }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) { var n = await _inner.ReadAsync(buffer, ct); Add(n); return n; }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override void Write(byte[] buffer, int offset, int count) { _inner.Write(buffer, offset, count); Add(count); }
    public override void Write(ReadOnlySpan<byte> buffer) { _inner.Write(buffer); Add(buffer.Length); }
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) { await _inner.WriteAsync(buffer, ct); Add(buffer.Length); }
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override void Flush() => _inner.Flush();
    public override Task FlushAsync(CancellationToken ct) => _inner.FlushAsync(ct);
    public override void SetLength(long value) => _inner.SetLength(value);

    protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
    public override async ValueTask DisposeAsync() { await _inner.DisposeAsync(); await base.DisposeAsync(); }
}
