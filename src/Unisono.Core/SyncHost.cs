namespace Unisono;

/// <summary>Checking: the full pass over all pairs after a (re)start, nothing transferred (yet).</summary>
public enum HostState { Stopped, Starting, Idle, Syncing, Paused, Trouble, Checking }

public sealed class PairStatus
{
    public SyncPair Pair { get; init; } = null!;
    public string Status { get; set; } = "waiting";
    public DateTime? LastSync { get; set; }
    /// <summary>Files found to change very often, waiting for the user's choice (ignore or keep syncing). Lock it.</summary>
    public List<string> Frequent { get; } = new();
    /// <summary>What Unisono suggests leaving out of syncing (see <see cref="IgnoreAdvisor"/>); replaced, never changed.</summary>
    public List<IgnoreSuggestion> Suggestions { get; set; } = new();
    /// <summary>Since when cycles of this pair fail or leave items unsynced (null = the last one went through).</summary>
    public DateTime? TroubleSince { get; set; }
    public int TroubleCycles { get; set; }
    /// <summary>Set once the trouble has lasted long enough to tell the user; cleared by the next clean cycle.</summary>
    public string? Health { get; set; }
    /// <summary>The last content check (null until one ran in this session), whether one runs now, and when the last finished.</summary>
    public ContentCheck.Report? LastCheck { get; set; }
    public bool Checking { get; set; }
    public DateTime? LastChecked { get; set; }
    /// <summary>Backup pairs: the versions kept on the other side, as of the last cycle.</summary>
    public List<SyncEngine.BackupVersion>? Versions { get; set; }
    /// <summary>The remote folder is a Cryptomator vault but this pair is not encrypted (yet): nothing is synced.</summary>
    public bool NeedsPassphrase { get; set; }
    /// <summary>The pair is encrypted but its remote folder is no vault any more: nothing is synced.</summary>
    public bool VaultGone { get; set; }
    /// <summary>A cycle of this pair runs right now.</summary>
    public bool Running { get; set; }
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
    private volatile Config? _pendingConfig;

    /// <summary>A pair's running parts: its engine, conversion, watchers and on-demand provider, kept across pair-list changes.</summary>
    private sealed class Runtime
    {
        public PairStatus Status = null!;
        public string Definition = "";
        public SyncEngine? Engine;
        public VaultConverter? Converter;
        public readonly List<FileSystemWatcher> Watchers = new();
        public HydrationProvider? Provider;
        public int Changed = 1; // the watchers saw something since the pair's last cycle: scan the local folder
        public int SuggestionsVersion; // the engine's suggestions last copied to the status

        public void Dispose()
        {
            foreach (var w in Watchers) w.Dispose();
            Watchers.Clear();
            Provider?.Dispose();
            Provider = null;
        }
    }

    private static string DefinitionOf(SyncPair pair) => System.Text.Json.JsonSerializer.Serialize(pair);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _checkRequests = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, List<string>> _forget = new();
    private Task? _checkTask;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, System.Collections.Concurrent.ConcurrentQueue<(string Rel, TaskCompletionSource<string> Done)>> _restores = new();

    /// <summary>Restores a backup pair's kept version after the pair's next cycle; returns the restored file's path in the pair.</summary>
    public Task<string> RestoreAsync(string pairId, string versionRel)
    {
        var done = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _restores.GetOrAdd(pairId, _ => new()).Enqueue((versionRel, done));
        _forceOnce = true; // even while paused
        Nudge();
        return done.Task;
    }

    /// <summary>Check this pair's content soon (after its next cycle).</summary>
    public void RequestCheck(string pairId) { _checkRequests[pairId] = true; Nudge(); }

    /// <summary>Work out this pair's ignore suggestions afresh in its next cycle (after the user chose some).</summary>
    public void SuggestAgain(string pairId) { _suggestAgain[pairId] = true; Nudge(); }
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _suggestAgain = new();

    /// <summary>Forget the sync history of these files before the pair's next cycle (see <see cref="SyncEngine.Forget"/>).</summary>
    public void ForgetHistory(string pairId, IEnumerable<string> rels)
    {
        _forget.AddOrUpdate(pairId, _ => rels.ToList(), (_, l) => l.Concat(rels).ToList());
        Nudge();
    }

    /// <summary>A content check found files that changed on their own. Raised on a thread-pool thread.</summary>
    public event Action<PairStatus>? ContentProblem;
    /// <summary>A pair's conversion into a Cryptomator vault finished. Raised on the syncing thread.</summary>
    public event Action<PairStatus>? Converted;

    public event Action? StateChanged;
    public event Action<PairStatus>? PairChanged;
    public event Action<PairStatus, CycleStats>? CycleCompleted;
    /// <summary>A pair has not synced cleanly for a while (<see cref="PairStatus.Health"/> says why). Once per episode.</summary>
    public event Action<PairStatus>? HealthProblem;

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
    /// <summary>
    /// (Re)starts with the given pairs. While running and with the same settings, only what changed is restarted: new
    /// and changed pairs start afresh (with a full pass), removed ones stop, the others keep their engines, watchers and
    /// quiet cycles. Settings that apply to all pairs restart everything.
    /// </summary>
    public async Task StartAsync(Config config)
    {
        if (_loop is { IsCompleted: false } && System.Text.Json.JsonSerializer.Serialize(config.Options) == System.Text.Json.JsonSerializer.Serialize(_options))
        {
            _pendingConfig = config;
            Nudge();
            return;
        }
        await StopAsync();
        _lock ??= SyncLock.TryAcquire()
                  ?? throw new InvalidOperationException("another Unisono is already syncing (the tray app or 'UnisonoCli run'); close it first");
        ActivityJournal.Prune();
        var lastRuns = LoadLastRuns();
        var lastChecks = LoadTimes(CheckedFile);
        Pairs = config.Pairs.Select(p => new PairStatus { Pair = p, LastSync = lastRuns.TryGetValue(p.Id, out var t) ? t : null,
            LastChecked = lastChecks.TryGetValue(p.Id, out var c) ? c : null }).ToList();
        // A pair never checked gets its first automatic check one interval from now, not all at once today.
        if (Pairs.Any(p => p.LastChecked == null))
        {
            foreach (var p in Pairs.Where(p => p.LastChecked == null)) lastChecks[p.Pair.Id] = DateTime.Now;
            SaveTimes(CheckedFile, lastChecks);
        }
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
        var runtimes = new List<Runtime>();
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
        var roundStartedIdle = false;
        var checkingRound = false;
        // Other changes (deleting, recycling, moving) count for ChangeHold after the last one, so a run of them shows as
        // one stretch of syncing instead of flickering between files.
        var changeHold = TimeSpan.FromSeconds(3);
        var recheckQueued = 0;
        Action? onActivity = null;
        onActivity = () =>
        {
            if (!roundRunning) return; // an online-only file opened between rounds
            var active = SyncActivity.Now().Active;
            var (changedAt, changedPair) = SyncActivity.LastChange;
            var changing = DateTime.UtcNow - changedAt < changeHold;
            // Transfers and changes over while the round goes on with quiet checks: back to how the round began.
            if (active.Count == 0 && !changing) { if (roundStartedIdle && State == HostState.Syncing) SetState(checkingRound ? HostState.Checking : HostState.Idle); return; }
            SetState(HostState.Syncing);
            if (current is { } c && c.Status != "syncing" && (active.Any(t => t.Pair == c.Pair.Name) || (changing && changedPair == c.Pair.Name))) Update(c, "syncing");
            // Nothing announces the end of a run of changes: look again once the hold is over.
            if (active.Count == 0 && Interlocked.Exchange(ref recheckQueued, 1) == 0)
                _ = Task.Delay(changeHold + TimeSpan.FromMilliseconds(200)).ContinueWith(_ => { Interlocked.Exchange(ref recheckQueued, 0); onActivity!(); });
        };
        SyncActivity.Changed += onActivity;
        Runtime NewRuntime(PairStatus ps)
        {
            var rt = new Runtime { Status = ps, Definition = DefinitionOf(ps.Pair) };
            Action mark = () => Interlocked.Exchange(ref rt.Changed, 1);
            if (ps.Pair.OnDemand) rt.Provider = ServeOnDemand(ps, stores);
            if (TryWatch(ps.Pair.LocalFullPath, attributes: ps.Pair.OnDemand, mark) is { } w) rt.Watchers.Add(w);
            // A folder pair's other side is a folder too: watch it as well, so its changes come across at once.
            // Network shares usually deliver these events; the full pass every few minutes covers the ones that do not.
            if (ps.Pair.Kind == RemoteKind.Folder && TryWatch(ps.Pair.RemoteFullPath, attributes: false, mark) is { } rw) rt.Watchers.Add(rw);
            return rt;
        }
        TimeSpan PollOf(List<Runtime> list) => TimeSpan.FromSeconds(list.Count == 0 ? 60 : Math.Max(5, list.Min(r => r.Status.Pair.PollSeconds)));
        try
        {
            foreach (var ps in Pairs) runtimes.Add(NewRuntime(ps));
            var poll = PollOf(runtimes);
            if (runtimes.Count > 0)
                _log.Info($"syncing {runtimes.Count} pair(s), watching {runtimes.Sum(r => r.Watchers.Count)} folder(s), polling every {poll.TotalSeconds:0}s");

            var backoff = TimeSpan.Zero;
            var followUp = false; // files still being written were skipped: look again shortly
            while (!ct.IsCancellationRequested)
            {
                // The pair list changed (a pair was added, edited or removed): keep what is unchanged.
                if (Interlocked.Exchange(ref _pendingConfig, null) is { } config)
                {
                    runtimes = Reconcile(runtimes, config, NewRuntime);
                    poll = PollOf(runtimes);
                }
                var pairs = runtimes.Select(r => r.Status).ToList();
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
                    // After a (re)start every pair gets a full pass: shown as checking, not as syncing, until something moves.
                    checkingRound = State == HostState.Starting;
                    if (checkingRound) SetState(HostState.Checking);
                    roundStartedIdle = State is HostState.Idle or HostState.Checking;
                    if (!roundStartedIdle) SetState(HostState.Syncing);
                    roundRunning = true;
                    var ok = true;
                    followUp = false;
                    for (var i = 0; i < pairs.Count; i++)
                    {
                        if (_pendingConfig != null) { followUp = true; break; } // the pair list changed: take it in first
                        var ps = pairs[i];
                        var rt = runtimes[i];
                        if (i > 0 && pairs[i - 1].Running) { pairs[i - 1].Running = false; Update(pairs[i - 1], pairs[i - 1].Status); }
                        for (var j = i + 1; j < pairs.Count; j++) { pairs[j].WaitingFor = ps.Pair.Name; Update(pairs[j], pairs[j].Status); }
                        ps.WaitingFor = null;
                        current = ps;
                        ps.Running = true;
                        Update(ps, ps.Status);
                        try
                        {
                            if (VaultConverter.Pending(ps.Pair))
                            {
                                // Turning an existing pair into an encrypted one: turns of copying instead of cycles.
                                var decrypt = VaultConverter.PendingDecrypt(ps.Pair);
                                rt.Converter ??= new VaultConverter(ps.Pair, await stores.CreatePlainAsync(ps.Pair, ct), await stores.CreateVaultAsync(ps.Pair, ct), _log, decrypt);
                                var finished = await rt.Converter.RunAsync(pairs.Count > 1 ? TurnLength : null, s => Update(ps, s), ct);
                                if (!finished) { followUp = true; continue; }
                                rt.Converter = null;
                                rt.Engine = null; // starts afresh from the new state
                                Update(ps, "in sync");
                                try { Converted?.Invoke(ps); } catch { }
                            }
                            if (ps.Status != "in sync") Update(ps, "syncing");
                            rt.Engine ??= new SyncEngine(await stores.CreateAsync(ps.Pair, ct), ps.Pair, _log, pairs.Select(x => x.Pair).ToList(), _options);
                            var engine = rt.Engine;
                            // With other pairs to serve, a long job (thousands of uploads) is done in turns of a few minutes.
                            engine.TimeBudget = pairs.Count > 1 ? TurnLength : null;
                            engine.LocalChanged = Interlocked.Exchange(ref rt.Changed, 0) == 1;
                            if (_suggestAgain.TryRemove(ps.Pair.Id, out _)) { engine.SuggestAgain(); engine.LocalChanged = true; }
                            if (_forget.TryRemove(ps.Pair.Id, out var forget))
                            {
                                engine.Forget(forget);
                                _log.Info($"[{ps.Pair.Name}] {forget.Count} file(s) will be compared afresh; differing versions are both kept");
                            }
                            var stats = await engine.RunCycleAsync(ct);
                            followUp |= engine.NeedsFollowUp;
                            ps.LastStats = stats;
                            ps.NeedsPassphrase = false;
                            ps.VaultGone = false;
                            ps.LastSync = DateTime.Now;
                            SaveLastRuns();
                            ps.LastError = stats.Errors > 0 ? $"{stats.Errors} item(s) failed, see log" : null;
                            Update(ps, stats.Errors > 0 ? "errors" : "in sync");
                            if (stats.Errors > 0) ok = false;
                            TrackHealth(ps, stats.Errors > 0 ? $"{stats.Errors} item(s) could not be synced, e.g. {stats.LastErrorText}" : null);
                            if (stats.Frequent.Count > 0)
                                lock (ps.Frequent) ps.Frequent.AddRange(stats.Frequent.Where(f => !ps.Frequent.Contains(f, StringComparer.OrdinalIgnoreCase)));
                            if (engine.SuggestionsVersion != rt.SuggestionsVersion)
                            {
                                rt.SuggestionsVersion = engine.SuggestionsVersion;
                                ps.Suggestions = engine.Suggestions;
                            }
                            CycleCompleted?.Invoke(ps, stats);
                            MaybeStartCheck(ps, engine, ct);
                            ps.Versions = engine.BackupVersions();
                            if (_restores.TryGetValue(ps.Pair.Id, out var queue))
                                while (queue.TryDequeue(out var r))
                                {
                                    try { r.Done.SetResult(await engine.RestoreAsync(r.Rel, ct)); followUp = true; }
                                    catch (Exception rex) { r.Done.SetException(rex); }
                                }
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                        catch (Exception ex)
                        {
                            ok = false;
                            Interlocked.Exchange(ref rt.Changed, 1); // scan again next time
                            if (ps.Pair.Kind == RemoteKind.Synology) { rt.Engine = null; stores.Forget(ps.Pair); }
                            ps.LastError = Log.Text(ex);
                            ps.NeedsPassphrase = ex is VaultFoundException;
                            ps.VaultGone = ex is VaultGoneException;
                            TrackHealth(ps, Log.Text(ex));
                            Update(ps, "failed");
                            _log.Error($"[{ps.Pair.Name}] {Log.Text(ex)}");
                        }
                    }
                    // Network trouble: back off up to 5 minutes rather than hammering the servers.
                    backoff = ok ? TimeSpan.Zero : TimeSpan.FromSeconds(Math.Min(300, Math.Max(15, backoff.TotalSeconds * 2)));
                    wait = ok ? poll : backoff;
                    roundRunning = false;
                    current = null;
                    foreach (var q in pairs) q.Running = false;
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
            foreach (var rt in runtimes) rt.Dispose();
            System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged -= onNet;
            System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged -= onAddr;
            try { Microsoft.Win32.SystemEvents.PowerModeChanged -= onPower; } catch { }
        }
    }

    /// <summary>
    /// Registers a files-on-demand pair's folder and answers Windows when an online-only file in it is opened. A problem
    /// here (folder inside OneDrive, ...) is reported on the pair; its cycles then fail with the same explanation.
    /// </summary>
    private HydrationProvider? ServeOnDemand(PairStatus ps, StoreFactory stores)
    {
        var pair = ps.Pair;
        try
        {
            if (CloudFiles.WhyNot(pair.LocalFullPath, pair) is { } why) throw new InvalidOperationException(why);
            if (!CloudFiles.IsRegistered(pair)) CloudFiles.Register(pair);
            IRemoteStore? store = null;
            var gate = new SemaphoreSlim(1, 1);
            return HydrationProvider.Connect(pair.LocalFullPath, async (rel, identity, size, ct) =>
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
            }, _log, pair.Name);
        }
        catch (Exception ex)
        {
            ps.LastError = Log.Text(ex);
            _log.Error($"[{pair.Name}] files on demand: {Log.Text(ex)}");
            return null;
        }
    }

    /// <summary>
    /// Takes in a changed pair list between cycles: a pair whose definition is unchanged keeps its runtime (engine,
    /// watchers, quiet cycles); new and changed pairs get a new one (a full pass first); removed ones are stopped.
    /// </summary>
    private List<Runtime> Reconcile(List<Runtime> old, Config config, Func<PairStatus, Runtime> create)
    {
        var lastRuns = LoadLastRuns();
        var result = new List<Runtime>();
        var kept = new HashSet<Runtime>();
        foreach (var pair in config.Pairs)
        {
            var definition = DefinitionOf(pair);
            var same = old.FirstOrDefault(r => !kept.Contains(r) && (ReferenceEquals(r.Status.Pair, pair) || r.Definition == definition));
            if (same != null)
            {
                kept.Add(same);
                result.Add(same);
                continue;
            }
            // Changed or new. A changed pair keeps its status (last run, health) but starts afresh.
            var previous = old.FirstOrDefault(r => !kept.Contains(r) && r.Status.Pair.Id == pair.Id);
            var ps = new PairStatus { Pair = pair, LastSync = previous?.Status.LastSync ?? (lastRuns.TryGetValue(pair.Id, out var t) ? t : null),
                LastChecked = previous?.Status.LastChecked ?? DateTime.Now, Status = previous?.Status.Status ?? "waiting" };
            if (previous != null) { previous.Dispose(); kept.Add(previous); }
            result.Add(create(ps));
            _log.Info($"[{pair.Name}] {(previous != null ? "settings changed: starting this pair afresh" : "added")}");
        }
        foreach (var gone in old.Where(r => !kept.Contains(r)))
        {
            gone.Dispose();
            _log.Info($"[{gone.Status.Pair.Name}] removed: no longer synced");
        }
        var all = result.Select(r => r.Status.Pair).ToList();
        foreach (var r in result) r.Engine?.SetOtherPairs(all); // nested folders are worked out from the whole list
        Pairs = result.Select(r => r.Status).ToList();
        try { StateChanged?.Invoke(); } catch { }
        return result;
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

    // A few failed cycles are normal (network, sleep); a pair that has not synced cleanly for HealthDelay and at least
    // 3 cycles is worth a warning. UNISONO_HEALTH_MINUTES shortens the delay for tests.
    private static TimeSpan HealthDelay => TimeSpan.FromMinutes(int.TryParse(Environment.GetEnvironmentVariable("UNISONO_HEALTH_MINUTES"), out var m) && m >= 0 ? m : 30);

    private void TrackHealth(PairStatus ps, string? problem)
    {
        if (problem == null)
        {
            if (ps.Health != null) _log.Info($"[{ps.Pair.Name}] syncing normally again");
            ps.TroubleSince = null; ps.TroubleCycles = 0; ps.Health = null;
            return;
        }
        ps.TroubleSince ??= DateTime.Now;
        ps.TroubleCycles++;
        if (ps.Health != null || ps.TroubleCycles < 3 || DateTime.Now - ps.TroubleSince < HealthDelay) return;
        ps.Health = $"Not fully synced since {ps.TroubleSince:HH:mm}{(ps.TroubleSince.Value.Date != DateTime.Today ? ps.TroubleSince.Value.ToString(" (d MMM)") : "")}: {problem}";
        _log.Warn($"[{ps.Pair.Name}] {ps.Health}");
        try { HealthProblem?.Invoke(ps); } catch { }
    }

    // ------------------------------------------------------------ content check

    private static string CheckedFile => Path.Combine(AppPaths.DataDir, "checked.json");

    /// <summary>After a pair's cycle: starts its content check when asked for or due. One check at a time, beside the cycles.</summary>
    private void MaybeStartCheck(PairStatus ps, SyncEngine engine, CancellationToken ct)
    {
        if (_checkTask is { IsCompleted: false }) return;
        var asked = _checkRequests.TryRemove(ps.Pair.Id, out _);
        var due = _options.CheckContentDays > 0 && ps.LastChecked is { } last && DateTime.Now - last > TimeSpan.FromDays(_options.CheckContentDays)
                  && !ContentCheck.OnBattery();
        if (!asked && !due) return;
        var (entries, remote) = engine.Snapshot();
        ps.Checking = true;
        Update(ps, ps.Status);
        _log.Info($"[{ps.Pair.Name}] checking the content of {entries.Count} synced file(s){(asked ? "" : " (the regular check)")}");
        _checkTask = Task.Run(async () =>
        {
            ContentCheck.Report report;
            try { report = await ContentCheck.RunAsync(ps.Pair, entries, remote, _log, ct); }
            catch (Exception ex) { _log.Warn($"[{ps.Pair.Name}] content check failed: {ex.Message}"); ps.Checking = false; Update(ps, ps.Status); return; }
            ps.Checking = false;
            if (!report.Cancelled)
            {
                ps.LastCheck = report;
                ps.LastChecked = report.Finished;
                var times = LoadTimes(CheckedFile);
                times[ps.Pair.Id] = report.Finished;
                SaveTimes(CheckedFile, times);
                var summary = $"content check: {report.Checked} file(s), {report.Bytes / 1048576.0:N0} MB read, {report.Problems.Count} problem(s)";
                if (report.Problems.Count == 0) _log.Info($"[{ps.Pair.Name}] {summary}");
                else
                {
                    _log.Warn($"[{ps.Pair.Name}] {summary}");
                    foreach (var p in report.Problems) _log.Warn($"[{ps.Pair.Name}]   {p.Rel}: {p.Detail}");
                    try { ContentProblem?.Invoke(ps); } catch { }
                }
            }
            Update(ps, ps.Status);
        });
    }

    private static Dictionary<string, DateTime> LoadTimes(string file)
    {
        try { return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, DateTime>>(File.ReadAllText(file)) ?? new(); }
        catch { return new(); }
    }

    private void SaveTimes(string file, Dictionary<string, DateTime> times)
    {
        try { AppPaths.EnsureDataDir(); File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(times)); }
        catch (Exception ex) { _log.Debug($"could not save {file}: {ex.Message}"); }
    }

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
