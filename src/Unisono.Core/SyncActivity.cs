namespace Unisono;

/// <summary>
/// What is being transferred right now, and how much has been since start: for the tray icon (arrows) and its
/// tooltip. Written by the sync engine and the on-open downloads, read by the app; thread-safe.
/// </summary>
public static class SyncActivity
{
    public enum Direction { Up, Down }

    public sealed record Transfer(Direction Dir, string Pair, string Rel, long Size);

    public sealed record Snapshot(IReadOnlyList<Transfer> Active, int Uploaded, int Downloaded, long UpBytes, long DownBytes);

    private static readonly List<Transfer> Active = new();
    private static int _uploaded, _downloaded;
    private static long _upBytes, _downBytes;

    /// <summary>Raised on every start and end of a transfer (on the transferring thread).</summary>
    public static event Action? Changed;

    /// <summary>Marks a transfer as running until the returned object is disposed; counted when it ends without error.</summary>
    public static Tracker Begin(Direction dir, string pair, string rel, long size)
    {
        var t = new Transfer(dir, pair, rel, size);
        lock (Active) Active.Add(t);
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
