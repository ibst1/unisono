namespace Unisono;

public enum HostState { Stopped, Starting, Idle, Syncing, Paused, Trouble }

public sealed class PairStatus
{
    public SyncPair Pair { get; init; } = null!;
    public string Status { get; set; } = "waiting";
    public DateTime? LastSync { get; set; }
    /// <summary>Files found to change very often, waiting for the user's choice (ignore or keep syncing). Lock it.</summary>
    public List<string> Frequent { get; } = new();
    /// <summary>While this pair waits for its turn: the pair that is syncing now.</summary>
    public string? WaitingFor { get; set; }
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

    /// <summary>
    /// Asked before every automatic round; a non-null answer ("metered connection", "presentation") skips the round.
    /// "Sync now" overrides it. Set by the app, which knows about networks and presentations.
    /// </summary>
    public Func<string?>? AutoPauseReason { get; set; }

    /// <summary>Why the last automatic round was skipped, or null.</summary>
    public string? PausedBecause { get; private set; }

    private AppOptions _options = new();
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
        var lastRuns = LoadLastRuns();
        Pairs = config.Pairs.Select(p => new PairStatus { Pair = p, LastSync = lastRuns.TryGetValue(p.Id, out var t) ? t : null }).ToList();
        _options = config.Options;
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
        var providers = new List<HydrationProvider>();
        // Missed events (sleep, no network, another computer) need no replay: every cycle compares everything. What
        // matters is running one soon: at start (below), when the network is back and when Windows wakes up.
        System.Net.NetworkInformation.NetworkAvailabilityChangedEventHandler onNet = (_, e) => { if (e.IsAvailable) { _log.Debug("network available: syncing"); Nudge(); } };
        System.Net.NetworkInformation.NetworkAddressChangedEventHandler onAddr = (_, _) => Nudge(); // VPN, another network
        Microsoft.Win32.PowerModeChangedEventHandler onPower = (_, e) => { if (e.Mode == Microsoft.Win32.PowerModes.Resume) { _log.Info("woke up: syncing"); Nudge(); } };
        System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged += onNet;
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += onAddr;
        try { Microsoft.Win32.SystemEvents.PowerModeChanged += onPower; } catch { /* no session events (service) */ }
        await using var stores = new StoreFactory(_log, _interactive);
        // Transfers during a round switch the display to "syncing" (the host and the pair that is transferring).
        var roundRunning = false;
        PairStatus? current = null;
        Action onActivity = () =>
        {
            if (!roundRunning) return; // an online-only file opened between rounds
            var active = SyncActivity.Now().Active;
            if (active.Count == 0) return;
            SetState(HostState.Syncing);
            if (current is { } c && c.Status != "syncing" && active.Any(t => t.Pair == c.Pair.Name)) Update(c, "syncing");
        };
        SyncActivity.Changed += onActivity;
        // Per pair: the watchers saw something since its last cycle (1), so the next one scans the local folder.
        var changed = new int[pairs.Count];
        Array.Fill(changed, 1);
        try
        {
            for (var i = 0; i < pairs.Count; i++)
            {
                var p = pairs[i];
                var index = i;
                Action mark = () => Interlocked.Exchange(ref changed[index], 1);
                if (p.Pair.OnDemand) ServeOnDemand(p, stores, providers);
                var w = TryWatch(p.Pair.LocalFullPath, attributes: p.Pair.OnDemand, mark);
                if (w != null) watchers.Add(w);
                // A folder pair's other side is a folder too: watch it as well, so its changes come across at once.
                // Network shares usually deliver these events; the full pass every few minutes covers the ones that do not.
                if (p.Pair.Kind == RemoteKind.Folder && TryWatch(p.Pair.RemoteFullPath, attributes: false, mark) is { } rw) watchers.Add(rw);
            }
            var poll = TimeSpan.FromSeconds(pairs.Count == 0 ? 60 : Math.Max(5, pairs.Min(p => p.Pair.PollSeconds)));
            if (pairs.Count > 0)
                _log.Info($"syncing {pairs.Count} pair(s), watching {watchers.Count} folder(s), polling every {poll.TotalSeconds:0}s");

            var backoff = TimeSpan.Zero;
            var followUp = false; // files still being written were skipped: look again shortly
            while (!ct.IsCancellationRequested)
            {
                TimeSpan wait;
                var autoPause = _paused || _forceOnce ? null : SafeAutoPause();
                if (autoPause != PausedBecause)
                {
                    PausedBecause = autoPause;
                    _log.Info(autoPause != null ? $"pausing: {autoPause}" : "resuming (no longer paused automatically)");
                    try { StateChanged?.Invoke(); } catch { }
                }
                if (autoPause != null && pairs.Count > 0)
                {
                    SetState(HostState.Paused);
                    wait = TimeSpan.FromSeconds(Math.Min(60, poll.TotalSeconds)); // check again soon
                }
                else if (pairs.Count > 0 && (!_paused || _forceOnce))
                {
                    _forceOnce = false;
                    // A routine check keeps showing "up to date"; "syncing" comes with the first transfer (below) and
                    // stays while work is left over for the next round.
                    if (State != HostState.Idle) SetState(HostState.Syncing);
                    roundRunning = true;
                    var ok = true;
                    followUp = false;
                    for (var i = 0; i < pairs.Count; i++)
                    {
                        var ps = pairs[i];
                        for (var j = i + 1; j < pairs.Count; j++) { pairs[j].WaitingFor = ps.Pair.Name; Update(pairs[j], pairs[j].Status); }
                        ps.WaitingFor = null;
                        current = ps;
                        try
                        {
                            if (ps.Status != "in sync") Update(ps, "syncing");
                            engines[i] ??= new SyncEngine(await stores.CreateAsync(ps.Pair, ct), ps.Pair, _log, pairs.Select(x => x.Pair).ToList(), _options);
                            // With other pairs to serve, a long job (thousands of uploads) is done in turns of a few minutes.
                            engines[i]!.TimeBudget = pairs.Count > 1 ? TurnLength : null;
                            engines[i]!.LocalChanged = Interlocked.Exchange(ref changed[i], 0) == 1;
                            var stats = await engines[i]!.RunCycleAsync(ct);
                            followUp |= engines[i]!.NeedsFollowUp;
                            ps.LastStats = stats;
                            ps.LastSync = DateTime.Now;
                            SaveLastRuns();
                            ps.LastError = stats.Errors > 0 ? $"{stats.Errors} item(s) failed, see log" : null;
                            Update(ps, stats.Errors > 0 ? "errors" : "in sync");
                            if (stats.Errors > 0) ok = false;
                            if (stats.Frequent.Count > 0)
                                lock (ps.Frequent) ps.Frequent.AddRange(stats.Frequent.Where(f => !ps.Frequent.Contains(f, StringComparer.OrdinalIgnoreCase)));
                            CycleCompleted?.Invoke(ps, stats);
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                        catch (Exception ex)
                        {
                            ok = false;
                            Interlocked.Exchange(ref changed[i], 1); // scan again next time
                            if (ps.Pair.Kind == RemoteKind.Synology) { engines[i] = null; stores.Forget(ps.Pair); }
                            ps.LastError = ex.Message;
                            Update(ps, "failed");
                            _log.Error($"[{ps.Pair.Name}] {ex.Message}");
                        }
                    }
                    // Network trouble: back off up to 5 minutes rather than hammering the servers.
                    backoff = ok ? TimeSpan.Zero : TimeSpan.FromSeconds(Math.Min(300, Math.Max(15, backoff.TotalSeconds * 2)));
                    wait = ok ? poll : backoff;
                    roundRunning = false;
                    current = null;
                    SetState(_paused ? HostState.Paused : !ok ? HostState.Trouble : followUp ? HostState.Syncing : HostState.Idle);
                }
                else
                {
                    SetState(_paused ? HostState.Paused : HostState.Idle);
                    wait = Timeout.InfiniteTimeSpan;
                }

                if (followUp && wait > TimeSpan.FromSeconds(3)) wait = TimeSpan.FromSeconds(3);
                if (await _nudge.WaitAsync(wait, ct))
                {
                    // Let a burst of writes settle before scanning (R, Office: temp file, rename, delete): wait until
                    // nothing has happened for 2 s, but no longer than 30 s in all under a steady stream of changes.
                    var burst = DateTime.UtcNow;
                    while (DateTime.UtcNow - burst < TimeSpan.FromSeconds(30) && await _nudge.WaitAsync(2000, ct)) { }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally
        {
            SyncActivity.Changed -= onActivity;
            foreach (var w in watchers) w.Dispose();
            foreach (var p in providers) p.Dispose();
            System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged -= onNet;
            System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged -= onAddr;
            try { Microsoft.Win32.SystemEvents.PowerModeChanged -= onPower; } catch { }
        }
    }

    /// <summary>
    /// Registers a files-on-demand pair's folder and answers Windows when an online-only file in it is opened. A problem
    /// here (folder inside OneDrive, ...) is reported on the pair; its cycles then fail with the same explanation.
    /// </summary>
    private void ServeOnDemand(PairStatus ps, StoreFactory stores, List<HydrationProvider> providers)
    {
        var pair = ps.Pair;
        try
        {
            if (CloudFiles.WhyNot(pair.LocalFullPath, pair) is { } why) throw new InvalidOperationException(why);
            if (!CloudFiles.IsRegistered(pair)) CloudFiles.Register(pair);
            IRemoteStore? store = null;
            var gate = new SemaphoreSlim(1, 1);
            providers.Add(HydrationProvider.Connect(pair.LocalFullPath, async (rel, identity, size, ct) =>
            {
                await gate.WaitAsync(ct);
                try { store ??= await stores.CreateAsync(pair, ct); }
                finally { gate.Release(); }
                var dir = Path.Combine(AppPaths.DataDir, "hydrate");
                Directory.CreateDirectory(dir);
                var tmp = Path.Combine(dir, Guid.NewGuid().ToString("N") + Path.GetExtension(rel));
                try
                {
                    using var t = SyncActivity.Begin(SyncActivity.Direction.Down, pair.Name, rel, size);
                    await store.DownloadAsync(store.HydrationNode(rel, identity, size), tmp, ct);
                    t.Complete();
                }
                catch { store = null; throw; } // e.g. a NAS session the sync loop has replaced: reconnect next time
                return tmp;
            }, _log, pair.Name));
        }
        catch (Exception ex)
        {
            ps.LastError = ex.Message;
            _log.Error($"[{pair.Name}] files on demand: {ex.Message}");
        }
    }

    private string? SafeAutoPause()
    {
        try { return AutoPauseReason?.Invoke(); } catch { return null; }
    }

    /// <param name="attributes">Also react to attribute changes: in a files-on-demand folder that is how Explorer's
    /// "Always keep on this device" and "Free up space" arrive.</param>
    private FileSystemWatcher? TryWatch(string path, bool attributes, Action changed)
    {
        if (!Directory.Exists(path)) return null;
        try
        {
            var w = new FileSystemWatcher(path)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size
                               | (attributes ? NotifyFilters.Attributes : 0),
                InternalBufferSize = 64 * 1024,
            };
            // Office's owner file (~$...) is ignored for syncing, but its removal means Word or Excel let go of a file whose update
            // was held back while it was open: sync right away.
            FileSystemEventHandler onChange = (_, ev) =>
            {
                var name = Path.GetFileName(ev.FullPath);
                if (!LocalScanner.IsIgnored(name) || (ev.ChangeType == WatcherChangeTypes.Deleted && name.StartsWith("~$", StringComparison.Ordinal))) { changed(); Nudge(); }
            };
            w.Created += onChange; w.Changed += onChange; w.Deleted += onChange;
            w.Renamed += (_, _) => { changed(); Nudge(); };
            // Events were lost (buffer overflow): only a scan can tell what happened.
            w.Error += (_, ev) => { _log.Warn($"watcher: {ev.GetException().Message}"); changed(); Nudge(); };
            w.EnableRaisingEvents = true;
            return w;
        }
        catch (Exception ex)
        {
            _log.Warn($"cannot watch {path}: {ex.Message}");
            return null;
        }
    }

    // UNISONO_TURN_SECONDS shortens the turns for tests.
    private static TimeSpan TurnLength => TimeSpan.FromSeconds(int.TryParse(Environment.GetEnvironmentVariable("UNISONO_TURN_SECONDS"), out var s) && s > 0 ? s : 120);

    // When each pair last finished a cycle, so the window can say so after a restart too.
    private static string LastRunsFile => Path.Combine(AppPaths.DataDir, "lastrun.json");

    private static Dictionary<string, DateTime> LoadLastRuns()
    {
        try { return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, DateTime>>(File.ReadAllText(LastRunsFile)) ?? new(); }
        catch { return new(); }
    }

    private void SaveLastRuns()
    {
        try
        {
            AppPaths.EnsureDataDir();
            var runs = Pairs.Where(p => p.LastSync != null).ToDictionary(p => p.Pair.Id, p => p.LastSync!.Value);
            File.WriteAllText(LastRunsFile, System.Text.Json.JsonSerializer.Serialize(runs));
        }
        catch (Exception ex) { _log.Debug("could not save the last run times: " + ex.Message); }
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
