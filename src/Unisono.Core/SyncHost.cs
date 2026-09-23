namespace Unisono;

public enum HostState { Stopped, Starting, Idle, Syncing, Paused, Trouble }

public sealed class PairStatus
{
    public SyncPair Pair { get; init; } = null!;
    public string Status { get; set; } = "waiting";
    public DateTime? LastSync { get; set; }
    public CycleStats? LastStats { get; set; }
    public string? LastError { get; set; }
}

/// <summary>
/// Runs all pairs continuously on a background task: a FileSystemWatcher per local folder nudges the loop,
/// otherwise the remote side is polled. Pausable, can be told to sync now, and reports per-pair status.
/// Events fire on a thread-pool thread.
/// </summary>
public sealed class SyncHost : IAsyncDisposable
{
    private readonly Log _log;
    private readonly bool _interactive;
    private readonly SemaphoreSlim _nudge = new(0, 1);
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private IDisposable? _lock;
    private volatile bool _paused;
    private volatile bool _forceOnce;

    public event Action? StateChanged;
    public event Action<PairStatus>? PairChanged;
    public event Action<PairStatus, CycleStats>? CycleCompleted;

    public HostState State { get; private set; } = HostState.Stopped;
    public IReadOnlyList<PairStatus> Pairs { get; private set; } = Array.Empty<PairStatus>();

    public SyncHost(Log log, bool interactive = false)
    {
        _log = log;
        _interactive = interactive;
    }

    public bool Paused
    {
        get => _paused;
        set
        {
            _paused = value;
            _log.Info(value ? "paused" : "resumed");
            if (State != HostState.Syncing) SetState(value ? HostState.Paused : HostState.Idle);
            Nudge();
        }
    }

    /// <summary>Run a cycle as soon as possible, even while paused.</summary>
    public void SyncNow()
    {
        _forceOnce = true;
        Nudge();
    }

    /// <summary>(Re)starts with the given pairs. Throws when another process is already syncing this data dir.</summary>
    public async Task StartAsync(Config config)
    {
        await StopAsync();
        _lock ??= SyncLock.TryAcquire()
                  ?? throw new InvalidOperationException("another Unisono is already syncing (the tray app or 'UnisonoCli run'); close it first");
        Pairs = config.Pairs.Select(p => new PairStatus { Pair = p }).ToList();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _loop = Task.Run(() => LoopAsync(ct));
    }

    public async Task StopAsync()
    {
        if (_cts == null) return;
        _cts.Cancel();
        try { if (_loop != null) await _loop; } catch (OperationCanceledException) { }
        _cts.Dispose();
        _cts = null;
        _loop = null;
        SetState(HostState.Stopped);
    }

    /// <summary>Runs until cancelled (CLI 'run').</summary>
    public Task Completion => _loop ?? Task.CompletedTask;

    private void Nudge()
    {
        if (_nudge.CurrentCount == 0) { try { _nudge.Release(); } catch (SemaphoreFullException) { } }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        SetState(HostState.Starting);
        var pairs = Pairs;
        var engines = new SyncEngine?[pairs.Count];
        var watchers = new List<FileSystemWatcher>();
        await using var stores = new StoreFactory(_log, _interactive);
        try
        {
            foreach (var p in pairs)
            {
                var w = TryWatch(p.Pair.LocalPath);
                if (w != null) watchers.Add(w);
            }
            var poll = TimeSpan.FromSeconds(pairs.Count == 0 ? 60 : Math.Max(5, pairs.Min(p => p.Pair.PollSeconds)));
            if (pairs.Count > 0)
                _log.Info($"syncing {pairs.Count} pair(s), watching {watchers.Count} folder(s), polling every {poll.TotalSeconds:0}s");

            var backoff = TimeSpan.Zero;
            while (!ct.IsCancellationRequested)
            {
                TimeSpan wait;
                if (pairs.Count > 0 && (!_paused || _forceOnce))
                {
                    _forceOnce = false;
                    SetState(HostState.Syncing);
                    var ok = true;
                    for (var i = 0; i < pairs.Count; i++)
                    {
                        var ps = pairs[i];
                        try
                        {
                            Update(ps, "syncing");
                            engines[i] ??= new SyncEngine(await stores.CreateAsync(ps.Pair, ct), ps.Pair, _log);
                            var stats = await engines[i]!.RunCycleAsync(ct);
                            ps.LastStats = stats;
                            ps.LastSync = DateTime.Now;
                            ps.LastError = stats.Errors > 0 ? $"{stats.Errors} item(s) failed, see log" : null;
                            Update(ps, stats.Errors > 0 ? "errors" : "in sync");
                            if (stats.Errors > 0) ok = false;
                            CycleCompleted?.Invoke(ps, stats);
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                        catch (Exception ex)
                        {
                            ok = false;
                            if (ps.Pair.Kind == RemoteKind.Synology) { engines[i] = null; stores.Forget(ps.Pair); }
                            ps.LastError = ex.Message;
                            Update(ps, "failed");
                            _log.Error($"[{ps.Pair.Name}] {ex.Message}");
                        }
                    }
                    // Network trouble: back off up to 5 minutes rather than hammering the servers.
                    backoff = ok ? TimeSpan.Zero : TimeSpan.FromSeconds(Math.Min(300, Math.Max(15, backoff.TotalSeconds * 2)));
                    wait = ok ? poll : backoff;
                    SetState(_paused ? HostState.Paused : ok ? HostState.Idle : HostState.Trouble);
                }
                else
                {
                    SetState(_paused ? HostState.Paused : HostState.Idle);
                    wait = Timeout.InfiniteTimeSpan;
                }

                if (await _nudge.WaitAsync(wait, ct))
                {
                    // Let a burst of writes (save, rename, temp file dance) settle before scanning.
                    await Task.Delay(2000, ct);
                    while (_nudge.CurrentCount > 0) await _nudge.WaitAsync(0, ct);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally
        {
            foreach (var w in watchers) w.Dispose();
        }
    }

    private FileSystemWatcher? TryWatch(string path)
    {
        if (!Directory.Exists(path)) return null;
        try
        {
            var w = new FileSystemWatcher(path)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 64 * 1024,
            };
            FileSystemEventHandler onChange = (_, ev) => { if (!LocalScanner.IsIgnored(Path.GetFileName(ev.FullPath))) Nudge(); };
            w.Created += onChange; w.Changed += onChange; w.Deleted += onChange;
            w.Renamed += (_, _) => Nudge();
            w.Error += (_, ev) => { _log.Warn($"watcher: {ev.GetException().Message}"); Nudge(); };
            w.EnableRaisingEvents = true;
            return w;
        }
        catch (Exception ex)
        {
            _log.Warn($"cannot watch {path}: {ex.Message}");
            return null;
        }
    }

    private void Update(PairStatus ps, string status)
    {
        ps.Status = status;
        try { PairChanged?.Invoke(ps); } catch { }
    }

    private void SetState(HostState s)
    {
        if (State == s) return;
        State = s;
        try { StateChanged?.Invoke(); } catch { }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _lock?.Dispose();
        _lock = null;
    }
}
