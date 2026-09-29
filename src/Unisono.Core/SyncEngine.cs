using System.Text;

namespace Unisono;

/// <summary>An unencrypted pair's remote folder turned out to be a Cryptomator vault: its passphrase is needed.</summary>
public sealed class VaultFoundException : Exception
{
    public VaultFoundException() : base("The remote folder is an encrypted Cryptomator vault. Enter its passphrase (Edit, Encrypt) to sync it; nothing is synced until then.") { }
}

/// <summary>An encrypted pair's remote folder holds no vault any more (decrypted or removed elsewhere).</summary>
public sealed class VaultGoneException : Exception
{
    public VaultGoneException() : base("The remote folder is no Cryptomator vault any more (it was decrypted or removed on another computer). Untick 'Encrypt' (Edit) to sync it as plain files; nothing is synced until then.") { }
}

public sealed class CycleStats
{
    public int Downloaded, Uploaded, Exported, Linked, Trashed, Recycled, Conflicts, Errors, Moved, Online;
    /// <summary>Things the user should hear about right away (the tray shows them), beyond the log.</summary>
    public List<string> Notices { get; } = new();
    /// <summary>Files found to change very often (see <see cref="SyncEngine.FrequentTransfers"/>), each reported once.</summary>
    public List<string> Frequent { get; } = new();
    /// <summary>The last item error of the cycle ("path: message"), for the health warning.</summary>
    public string? LastErrorText { get; set; }
    public bool Any => Downloaded + Uploaded + Exported + Linked + Trashed + Recycled + Conflicts + Errors + Moved + Online > 0;
    public override string ToString() =>
        $"{Downloaded} down, {Uploaded} up, {Moved} moved,{(Online > 0 ? $" {Online} online-only," : "")} {Exported} exported, {Linked} linked, {Trashed} deleted remotely, {Recycled} recycled locally, {Conflicts} conflicts, {Errors} errors";
}

/// <summary>One thing a dry run found it would do.</summary>
public sealed record PlannedAction(string Action, string Path, string? Detail);

/// <summary>
/// One sync pair. Each cycle compares three things per path: what the remote side has (from the store), what the
/// disk has (a fresh stat walk) and what both looked like last time they agreed (the state). That third leg is what
/// tells a deletion from a never-seen file, and a local edit from a remote one.
///
/// Safety rules: deletes go to the remote trash / Recycle Bin; a folder is only deleted on the other side when
/// nothing inside it changed since the last sync; when both sides changed, both versions are kept; a local file is
/// copied to the version store before it is overwritten.
/// Derived items (Google Docs exports, cloud-only links) are one-way: the remote is the source, local copies are
/// regenerated, and deleting or editing them locally changes nothing remotely.
///
/// Direction: in a one-way pair only the source side's changes are applied; nothing done on the other side is
/// propagated or undone. A dry run takes every decision but performs none, collecting them in <see cref="Plan"/>.
/// </summary>
public sealed partial class SyncEngine
{
    private readonly IRemoteStore _store;
    private readonly SyncPair _pair;
    private readonly Log _log;
    private readonly string _root;
    private readonly SyncState _state;
    private readonly AppOptions _options;
    private IReadOnlyList<SyncPair> _others;

    /// <summary>All pairs (for nested folders): replaced when the pair list changes while this engine keeps running.</summary>
    public void SetOtherPairs(IReadOnlyList<SyncPair> pairs) => _others = pairs;
    private readonly HashSet<string> _warnedOnce = new(StringComparer.OrdinalIgnoreCase);
    private SyncScope _scope = null!;
    private HashSet<string> _ignoredBelow = new(StringComparer.OrdinalIgnoreCase);

    public SyncPair Pair => _pair;

    /// <summary>Decide everything, change nothing (the state file is not written either).</summary>
    public bool DryRun { get; init; }
    public List<PlannedAction> Plan { get; } = new();

    /// <summary>Something was left for a moment (a file still being written); the host runs another cycle soon.</summary>
    public bool NeedsFollowUp { get; private set; }

    /// <summary>A file written to in the last seconds is left for the next cycle rather than uploaded half-saved.</summary>
    /// <summary>
    /// How long one cycle may keep starting file transfers; the rest waits for the next cycle. Set by the host when
    /// there are other pairs, so one pair with hours of uploads does not keep the others from their turn. Null = no limit.
    /// </summary>
    public TimeSpan? TimeBudget { get; set; }

    /// <summary>
    /// Set by the host from its file watchers: something changed in the local folder (for folder pairs also in the
    /// other folder) since the last cycle. Without that, with the remote tree as it was and no work left over, a cycle
    /// stops after the remote check: no local scan, no file passes, no state written. A full pass still runs every
    /// <see cref="FullCycleInterval"/>, for anything a watcher missed.
    /// </summary>
    public bool LocalChanged { get; set; } = true;
    public TimeSpan FullCycleInterval { get; set; } = TimeSpan.FromMinutes(10);
    private DateTime _lastFullCycleUtc = DateTime.MinValue;
    private long _lastRemoteFingerprint;
    private bool _docsChecked;
    private bool _lastHadErrors; // failed items are retried every cycle, not only in the next full pass
    private Dictionary<string, RemoteNode>? _lastRemote;

    /// <summary>The synced files' state and the last remote tree, copied for the content check. Call between cycles.</summary>
    public (List<StateEntry> Entries, Dictionary<string, RemoteNode> Remote) Snapshot() =>
        (_state.Entries.Values.Where(e => e.Kind == EntryKind.File).ToList(),
         new Dictionary<string, RemoteNode>(_lastRemote ?? new(), StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Forgets the sync history of these files. The next cycle then meets both sides without history: identical content
    /// is simply linked up again, different content is kept twice (a conflict copy). Call between cycles.
    /// </summary>
    public void Forget(IEnumerable<string> rels)
    {
        foreach (var rel in rels) _state.Entries.Remove(rel);
        LocalChanged = true; // a full cycle, which also writes the state
    }
    private DateTime _deadline = DateTime.MaxValue;

    private bool OutOfTime()
    {
        if (DryRun || DateTime.UtcNow < _deadline) return false;
        NeedsFollowUp = true;
        return true;
    }

    private bool Settling(LocalItem l)
    {
        if (DryRun || !LocalScanner.Unsettled(l)) return false; // a preview shows what would happen, settled or not
        NeedsFollowUp = true;
        return true;
    }

    private bool Down => _pair.Direction is not (SyncDirection.UploadOnly or SyncDirection.Backup);   // may change the local side
    private bool Up => _pair.Direction != SyncDirection.DownloadOnly;   // may change the remote side

    public SyncEngine(IRemoteStore store, SyncPair pair, Log log, IReadOnlyList<SyncPair>? allPairs = null, AppOptions? options = null)
    {
        _others = allPairs ?? Array.Empty<SyncPair>();
        _options = options ?? new AppOptions();
        _store = store;
        _pair = pair;
        _log = log;
        _root = pair.LocalFullPath;
        _state = SyncState.Load(pair);
        _store.LoadState(_state);
    }

    public async Task<CycleStats> RunCycleAsync(CancellationToken ct)
    {
        var stats = new CycleStats();
        Plan.Clear();
        if (!Directory.Exists(_root))
        {
            _log.Warn($"[{_pair.Name}] local folder {_root} does not exist; nothing done");
            return stats;
        }

        // Selective sync and ignore rules: out-of-scope and ignored items are simply invisible to the rest of the
        // cycle, so they are never touched on either side.
        _scope = new SyncScope(_pair, _others);
        EnsureSyncRoot();
        _doneThisCycle.Clear();
        var workLeft = NeedsFollowUp;
        NeedsFollowUp = false;
        _deadline = TimeBudget is { } budget ? DateTime.UtcNow + budget : DateTime.MaxValue;
        var remoteAll = await _store.GetTreeAsync(_log, ct);
        _lastRemote = remoteAll;
        _fullRemote = remoteAll;
        // A remote folder that is (or is becoming) a Cryptomator vault is never synced as plain files: that would bring the
        // ciphertext here and, once the plain originals are gone, delete them here too.
        if (_store is not CryptomatorStore && remoteAll.ContainsKey(CryptomatorVault.VaultFile) && remoteAll.ContainsKey(CryptomatorVault.MasterkeyFile))
            throw new VaultFoundException();
        var fingerprint = Fingerprint(remoteAll);
        _docsChecked = false;
        if (!DryRun && !LocalChanged && !workLeft && !_lastHadErrors && fingerprint == _lastRemoteFingerprint && DateTime.UtcNow - _lastFullCycleUtc < FullCycleInterval)
        {
            // Nothing seen on either side. Only Google Docs edits, which Drive reports late, need a look of their own.
            if (!await FreshenExportedDocsAsync(remoteAll, ct)) return stats;
            _docsChecked = true;
        }
        _lastRemoteFingerprint = fingerprint;
        _lastFullCycleUtc = DateTime.UtcNow;
        var localAll = LocalScanner.Scan(_root, _log);
        var ignore = IgnoreRules.Build(_options, _pair, _root,
            localAll.Values.Where(i => !i.IsDir && PathUtil.NameOf(i.RelPath).Equals(IgnoreRules.FileName, StringComparison.OrdinalIgnoreCase)).Select(i => i.RelPath),
            _log);
        _ignoredBelow = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // The backup's versions folder is not part of the synced content, on either side.
        var remote = Visible(Without(remoteAll, IsInVersions), n => n.IsFolder, ignore);
        var local = Visible(Without(localAll, IsInVersions), i => i.IsDir, ignore);
        Advise(local, stats);
        try
        {
            // Renames and moves first, so they are carried over as such instead of as a delete plus an upload.
            await DetectFolderMovesAsync(remote, local, stats, ct);
            await SyncFoldersAsync(remote, local, stats, ct);
            await DetectFileMovesAsync(remote, local, stats, ct);
            await SyncFilesAsync(remote, local, stats, ct);
            await ApplyOnDemandAsync(remote, local, stats, ct);
            if (!DryRun) TidyBridgeFolders(local);
            await PruneVersionsAsync(ct);
        }
        finally
        {
            if (!DryRun)
            {
                _store.SaveState(_state);
                _state.Save(_pair);
            }
        }
        if (stats.Any) _log.Info($"[{_pair.Name}] {stats}");
        _lastHadErrors = stats.Errors > 0;
        return stats;
    }

    // Files whose changes are transferred again and again (logs, status files) are usually better ignored: after
    // FrequentTransfers changes within FrequentWindow the user is asked, once per file and run of the app.
    public const int FrequentTransfers = 6;
    public static readonly TimeSpan FrequentWindow = TimeSpan.FromHours(1);
    private readonly Dictionary<string, List<DateTime>> _changeTimes = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _frequentReported = new(StringComparer.OrdinalIgnoreCase);

    private void ChangeTransferred(string rel, CycleStats stats)
    {
        if (DryRun || _pair.FrequentOk.Contains(rel, StringComparer.OrdinalIgnoreCase)) return;
        var now = DateTime.UtcNow;
        lock (_changeTimes)
        {
            if (!_changeTimes.TryGetValue(rel, out var times)) _changeTimes[rel] = times = new();
            times.Add(now);
            times.RemoveAll(t => now - t > FrequentWindow);
            if (times.Count < FrequentTransfers || !_frequentReported.Add(rel)) return;
        }
        stats.Frequent.Add(rel);
        stats.Notices.Add($"{PathUtil.NameOf(rel)} in {_pair.Name} changes very often ({FrequentTransfers} times within an hour). Open Unisono to ignore it or keep syncing it.");
        _log.Warn($"[{_pair.Name}] '{rel}' changes very often; it is probably better ignored");
    }

    private static Dictionary<string, T> Without<T>(Dictionary<string, T> all, Func<string, bool> drop)
    {
        if (!all.Keys.Any(drop)) return all;
        var kept = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in all) if (!drop(k)) kept[k] = v;
        return kept;
    }

    /// <summary>Changes whenever an item of the remote tree is added, removed or changed (within this process).</summary>
    private static long Fingerprint(Dictionary<string, RemoteNode> tree)
    {
        long h = tree.Count;
        foreach (var (rel, n) in tree) // a sum: the order of the dictionary does not matter
            h += HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(rel), n.Id, n.Size, n.ModifiedUtc, n.Md5);
        return h;
    }

    /// <summary>The in-scope, not-ignored part of one side; records which folders hold ignored content.</summary>
    private Dictionary<string, T> Visible<T>(Dictionary<string, T> all, Func<T, bool> isDir, IgnoreRules ignore)
    {
        var scoped = _scope.Filter(all, isDir);
        if (ignore.IsEmpty) return scoped;
        var result = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in scoped)
        {
            if (!ignore.IsIgnored(k, isDir(v))) { result[k] = v; continue; }
            for (var p = PathUtil.ParentOf(k); p.Length > 0; p = PathUtil.ParentOf(p)) _ignoredBelow.Add(p);
        }
        return result;
    }

    /// <summary>
    /// A folder that must never be deleted as a whole because it holds things the cycle cannot see: content outside
    /// the folder selection, a nested pair, or ignored files. Its visible content is synced item by item instead.
    /// </summary>
    private bool IsProtected(string rel) => _scope.IsProtected(rel) || _ignoredBelow.Contains(rel);

    /// <summary>True when the (filtered) side has in-scope content below a folder other than bridge folders.</summary>
    private bool HasScopedContent<T>(string rel, Dictionary<string, T> side)
    {
        var prefix = rel + "/";
        return side.Keys.Any(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !_scope.IsBridge(k));
    }

    /// <summary>
    /// A bridge folder (only there to reach an included folder) that ends up with nothing in scope below it is removed
    /// locally, but only when it is physically empty: files outside the scope in it are never touched.
    /// </summary>
    private void TidyBridgeFolders(Dictionary<string, LocalItem> local)
    {
        foreach (var rel in local.Values.Where(i => i.IsDir && _scope.IsBridge(i.RelPath)).Select(i => i.RelPath)
                     .OrderByDescending(PathUtil.Depth).ToList())
        {
            var path = Local(rel);
            if (!Directory.Exists(path) || Directory.EnumerateFileSystemEntries(path).Any()) continue;
            try
            {
                Directory.Delete(path);
                local.Remove(rel);
                _state.Entries.Remove(rel);
            }
            catch { /* in use: try again next cycle */ }
        }
    }

    /// <summary>In a dry run: note the action and say so. Returns true when the caller must not perform it.</summary>
    private bool Planned(string action, string rel, string? detail = null)
    {
        if (!DryRun) return false;
        Plan.Add(new PlannedAction(action, rel, detail));
        _log.Info($"[{_pair.Name}] would {action}: {rel}{(detail != null ? " (" + detail + ")" : "")}");
        return true;
    }

    private RemoteNode DryNode(string rel, RemoteNode? parent, bool folder) => new()
    {
        Id = "dry:" + rel, Name = PathUtil.NameOf(rel), ParentId = parent?.Id ?? "", RelPath = rel,
        MimeType = folder ? DriveClient.FolderMime : "application/octet-stream",
    };

    // ---------------------------------------------------------------- folders

    private async Task SyncFoldersAsync(Dictionary<string, RemoteNode> remote, Dictionary<string, LocalItem> local, CycleStats stats, CancellationToken ct)
    {
        // Remote folders, shallow first so parents exist before children.
        foreach (var node in remote.Values.Where(n => n.IsFolder).OrderBy(n => PathUtil.Depth(n.RelPath)).ToList())
        {
            ct.ThrowIfCancellationRequested();
            var rel = node.RelPath;
            if (!remote.ContainsKey(rel)) continue; // an ancestor was deleted in this pass
            local.TryGetValue(rel, out var l);
            _state.Entries.TryGetValue(rel, out var s);

            if (l is { IsDir: true })
            {
                UpsertFolderEntry(rel, node.Id);
                continue;
            }
            if (l != null)
            {
                WarnOnce(rel, $"'{rel}' is a folder remotely but a file locally; leaving both alone");
                RemoveTree(remote, rel);
                continue;
            }
            if (IsProtected(rel))
            {
                // Holds content this cycle cannot see: never deleted as a whole. Deleted locally with nothing visible
                // changed remotely since: stays deleted here, and the file pass removes its visible content remotely
                // item by item. Otherwise it exists locally while something visible lives in it.
                if (s is { Kind: EntryKind.Folder } && !RemoteTreeHasChanges(rel, remote)) continue;
                if (Down && HasScopedContent(rel, remote)) CreateLocalFolder(rel, node, local);
                continue;
            }
            if (s is { Kind: EntryKind.Folder } && !RemoteTreeHasChanges(rel, remote))
            {
                // Deleted locally, unchanged remotely since we last saw it: propagate (unless this pair only downloads,
                // in which case the local deletion is simply left as it is).
                if (!Up) continue;
                if (Directory.Exists(Local(rel))) { NeedsFollowUp = true; continue; } // back already (renamed away and back)
                await Try(rel, stats, async () =>
                {
                    if (!Planned(BackupMode ? "keep folder in the backup's versions (deleted locally)" : "delete folder remotely (deleted locally)", rel))
                    {
                        if (BackupMode) await KeepRemoteVersionAsync(node, rel, ct);
                        else await _store.DeleteAsync(node, ct);
                    }
                    _state.RemoveUnder(rel);
                    RemoveTree(remote, rel);
                    stats.Trashed++;
                    if (!DryRun && !BackupMode) _log.Info($"[{_pair.Name}] deleted folder remotely (deleted locally): {rel}");
                    Journal("deleted remotely", rel + "/", detail: BackupMode ? "kept in the backup's versions" : "deleted here first");
                });
                continue;
            }
            if (!Down) continue; // upload-only: remote-only folders are left alone
            if (s is { Kind: EntryKind.Folder })
            {
                // Deleted locally but the remote has new content in it: keep the remote version, forget the old state
                // so the file pass treats everything inside as new and downloads it rather than deleting it.
                _log.Warn($"[{_pair.Name}] folder '{rel}' was deleted locally but changed remotely; restoring it");
                _state.RemoveUnder(rel);
            }
            CreateLocalFolder(rel, node, local);
        }

        // Local folders unknown remotely, shallow first.
        foreach (var item in local.Values.Where(i => i.IsDir).OrderBy(i => PathUtil.Depth(i.RelPath)).ToList())
        {
            ct.ThrowIfCancellationRequested();
            var rel = item.RelPath;
            if (!local.ContainsKey(rel) || remote.ContainsKey(rel)) continue;
            _state.Entries.TryGetValue(rel, out var s);

            if (IsProtected(rel))
            {
                // The mirror image: never recycled locally as a whole (it may hold local-only files the cycle cannot
                // see); created remotely only while something visible lives in it.
                if (!HasScopedContent(rel, local)) { _state.Entries.Remove(rel); continue; }
                // Deleted remotely with nothing visible changed locally since: not recreated there; the file pass
                // recycles its visible content here item by item, and the rest of the folder stays.
                if (s is { Kind: EntryKind.Folder } && !LocalTreeHasChanges(rel, local)) continue;
                if (Up) await CreateRemoteFolderAsync(rel, remote, stats, ct);
                continue;
            }
            if (s is { Kind: EntryKind.Folder })
            {
                if (!LocalTreeHasChanges(rel, local))
                {
                    if (!Down) continue; // upload-only: a remote deletion is not applied here (nor undone)
                    await Try(rel, stats, () =>
                    {
                        if (!Planned("move folder to the Recycle Bin (deleted remotely)", rel))
                        {
                            var where = RemoveLocal(item.FullPath);
                            _log.Info($"[{_pair.Name}] moved folder to {where} (deleted remotely): {rel}");
                            Journal("deleted here", rel + "/", detail: $"deleted on the other side; moved to {where}");
                        }
                        RemoveTree(local, rel);
                        _state.RemoveUnder(rel);
                        stats.Recycled++;
                        return Task.CompletedTask;
                    });
                    continue;
                }
                if (!Up) continue;
                _log.Warn($"[{_pair.Name}] folder '{rel}' was deleted remotely but changed locally; re-creating it");
                _state.RemoveUnder(rel);
            }
            if (!Up) continue; // download-only: local-only folders stay local
            await CreateRemoteFolderAsync(rel, remote, stats, ct);
        }
    }

    private void CreateLocalFolder(string rel, RemoteNode node, Dictionary<string, LocalItem> local)
    {
        if (!Planned("create folder locally", rel))
        {
            if (OnDemand)
            {
                CloudFiles.CreatePlaceholder(Local(rel), isDir: true, 0, node.ModifiedUtc == default ? DateTime.UtcNow : node.ModifiedUtc, node.Id);
                if (ParentPinned(Local(rel))) CloudFiles.SetPinState(Local(rel), 1); // new folders in a kept folder are kept too
            }
            else Directory.CreateDirectory(Local(rel));
        }
        local[rel] = new LocalItem { RelPath = rel, FullPath = Local(rel), IsDir = true, Attributes = DryRun ? FileAttributes.Directory : File.GetAttributes(Local(rel)) };
        UpsertFolderEntry(rel, node.Id);
    }

    private async Task CreateRemoteFolderAsync(string rel, Dictionary<string, RemoteNode> remote, CycleStats stats, CancellationToken ct)
    {
        if (!TryParent(rel, remote, out var parent)) return; // parent could not be created; reported there
        await Try(rel, stats, async () =>
        {
            var node = Planned("create folder remotely", rel) ? DryNode(rel, parent, folder: true) : await _store.CreateFolderAsync(rel, parent, ct);
            remote[rel] = node;
            UpsertFolderEntry(rel, node.Id);
            if (!DryRun) _log.Info($"[{_pair.Name}] created folder remotely: {rel}");
        });
    }

    // ------------------------------------------------------------------ files

    private async Task SyncFilesAsync(Dictionary<string, RemoteNode> remote, Dictionary<string, LocalItem> local, CycleStats stats, CancellationToken ct)
    {
        // Local paths that are derived from the remote (exports, links) and must not be treated as new local files.
        var derived = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!_docsChecked) await FreshenExportedDocsAsync(remote, ct);

        foreach (var node in remote.Values.Where(n => !n.IsFolder).OrderBy(n => n.RelPath, StringComparer.OrdinalIgnoreCase).ToList())
        {
            ct.ThrowIfCancellationRequested();
            var rel = node.RelPath;
            var parentRel = PathUtil.ParentOf(rel);
            // Parent missing locally: nothing to do, unless the file was synced before and its (partly synced) folder
            // was deleted here; then the file pass still has to remove it remotely, item by item.
            if (parentRel.Length > 0 && !(local.GetValueOrDefault(parentRel)?.IsDir ?? false)
                && !(_state.Entries.TryGetValue(rel, out var prev) && prev.Kind == EntryKind.File && !local.ContainsKey(rel)))
                continue;

            var kind = Classify(node, local);
            if (kind == EntryKind.File) { if (!OutOfTime()) await SyncFileAsync(node, remote, local, derived, stats, ct); }
            else if (kind != EntryKind.Folder) await SyncDerivedAsync(node, kind, remote, local, derived, stats, ct);
            // Folder here means "skip": nothing local, but a stale derived entry is cleaned up below.
        }

        // Derived entries whose remote item is gone or is now represented differently: drop the local copy.
        foreach (var s in _state.Entries.Values.Where(e => e.Kind is EntryKind.Export or EntryKind.Link).ToList())
        {
            var stillDerived = remote.TryGetValue(s.Path, out var node) && Classify(node, local) == s.Kind
                               && string.Equals(DerivedName(node, s.Kind), s.LocalName, StringComparison.OrdinalIgnoreCase);
            if (stillDerived) { if (s.LocalName != null) derived.Add(s.LocalName); continue; }
            if (s.LocalName != null)
            {
                derived.Add(s.LocalName);
                if (Down && local.TryGetValue(s.LocalName, out var l) && !l.IsDir)
                {
                    if (!Planned($"remove {s.Kind.ToString().ToLowerInvariant()}", s.LocalName))
                    {
                        var where = RecycleBin.Delete(l.FullPath);
                        _log.Info($"[{_pair.Name}] removed {s.Kind.ToString().ToLowerInvariant()} ({where}): {s.LocalName}");
                    }
                    local.Remove(s.LocalName);
                }
            }
            _state.Entries.Remove(s.Path);
        }

        // Local files the remote does not have.
        var uploads = new List<(LocalItem Item, RemoteNode? Parent)>();
        foreach (var item in local.Values.Where(i => !i.IsDir).OrderBy(i => i.RelPath, StringComparer.OrdinalIgnoreCase).ToList())
        {
            ct.ThrowIfCancellationRequested();
            var rel = item.RelPath;
            if (!local.ContainsKey(rel) || derived.Contains(rel) || remote.ContainsKey(rel)) continue;
            _state.Entries.TryGetValue(rel, out var s);
            // Parent missing remotely: only a synced, unchanged file (its partly synced folder was deleted remotely)
            // is handled, by the deletion below; anything else waits until the parent exists.
            var parentKnown = TryParent(rel, remote, out var parent);
            if (!parentKnown && !(s is { Kind: EntryKind.File } && LocalUnchanged(item, s))) continue;

            if (s is { Kind: EntryKind.File })
            {
                if (LocalUnchanged(item, s))
                {
                    if (!Down) continue; // upload-only: the remote deletion is neither applied nor undone
                    await Try(rel, stats, () =>
                    {
                        if (!Planned("move to the Recycle Bin (deleted remotely)", rel))
                        {
                            var where = RemoveLocal(item.FullPath);
                            _log.Info($"[{_pair.Name}] moved to {where} (deleted remotely): {rel}");
                            Journal("deleted here", rel, item.Size, $"deleted on the other side; moved to {where}");
                        }
                        local.Remove(rel);
                        _state.Entries.Remove(rel);
                        stats.Recycled++;
                        return Task.CompletedTask;
                    });
                    continue;
                }
                if (Up) _log.Warn($"[{_pair.Name}] '{rel}' was deleted remotely but changed locally; uploading it again");
            }
            if (!Up || !parentKnown) continue; // download-only: local-only files stay local
            if (Settling(item) || OutOfTime()) continue;
            uploads.Add((item, parent));
        }
        await UploadNewFilesAsync(uploads, remote, stats, ct);

        // Entries with neither side left.
        foreach (var key in _state.Entries.Keys.Where(k => !remote.ContainsKey(k) && !local.ContainsKey(k)).ToList())
            _state.Entries.Remove(key);
    }

    /// <summary>
    /// New local files, uploaded several at a time where the store allows it (Drive takes a second or two per file
    /// whatever its size, so a folder of thousands of small files would otherwise take hours). Only the transfer runs
    /// in parallel; the bookkeeping is done one at a time.
    /// </summary>
    private async Task UploadNewFilesAsync(List<(LocalItem Item, RemoteNode? Parent)> uploads, Dictionary<string, RemoteNode> remote, CycleStats stats, CancellationToken ct)
    {
        var gate = new object();
        await Parallel.ForEachAsync(uploads, new ParallelOptions { MaxDegreeOfParallelism = DryRun ? 1 : Math.Max(1, _store.ParallelUploads), CancellationToken = ct },
            async (u, token) =>
            {
                var (item, parent) = u;
                var rel = item.RelPath;
                if (OutOfTime()) return;
                try
                {
                    RemoteNode node;
                    lock (gate) node = Planned("upload", rel, $"{item.Size:N0} bytes") ? DryNode(rel, parent, folder: false) : null!;
                    node ??= await Tracked(SyncActivity.Direction.Up, rel, item.Size, () => _store.UploadNewAsync(item.FullPath, rel, parent, token));
                    lock (gate)
                    {
                        remote[rel] = node;
                        if (!DryRun) Uploaded(item.FullPath, node.Id);
                        RecordFile(rel, node, item.FullPath);
                        stats.Uploaded++;
                        if (!DryRun) _log.Info($"[{_pair.Name}] uploaded: {rel} ({item.Size:N0} bytes)");
                        Journal("uploaded", rel, item.Size, "new here");
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    lock (gate) { stats.Errors++; stats.LastErrorText = $"{rel}: {Log.Text(ex)}"; _log.Error($"[{_pair.Name}] {rel}: {Log.Text(ex)}"); }
                }
            });
    }

    private async Task SyncFileAsync(RemoteNode node, Dictionary<string, RemoteNode> remote, Dictionary<string, LocalItem> local, HashSet<string> derived, CycleStats stats, CancellationToken ct)
    {
        var rel = node.RelPath;
        var path = Local(rel);
        local.TryGetValue(rel, out var l);
        _state.Entries.TryGetValue(rel, out var s);

        if (l is { IsDir: true })
        {
            WarnOnce(rel, $"'{rel}' is a file remotely but a folder locally; leaving both alone");
            return;
        }
        if (s != null && s.Kind != EntryKind.File)
        {
            // Was a link/export; a real local copy has appeared (user hydrated it). Drop the derived file and
            // continue with no history, i.e. adopt if identical, otherwise keep both.
            if (s.LocalName != null)
            {
                derived.Add(s.LocalName);
                if (local.TryGetValue(s.LocalName, out var d) && !d.IsDir)
                {
                    if (!Planned("remove link", s.LocalName)) RecycleBin.Delete(d.FullPath);
                    local.Remove(s.LocalName);
                }
            }
            _state.Entries.Remove(rel);
            s = null;
        }

        await Try(rel, stats, async () =>
        {
            if (l == null)
            {
                if (s == null || !RemoteUnchanged(node, s))
                {
                    if (Down) await DownloadAsync(node, path, stats, s == null ? "new remotely" : "changed remotely after local delete", ct);
                    return;
                }
                if (!Up) return; // download-only: a local deletion is left as it is until the remote file changes
                // Deleted locally and, as far as the cached tree knows, untouched remotely. The cache can lag,
                // so confirm with fresh metadata before deleting anything on the other side.
                var fresh = await _store.RefreshAsync(node, ct);
                if (fresh == null)
                {
                    remote.Remove(rel);
                    _state.Entries.Remove(rel);
                    return; // already gone on both sides
                }
                if (!RemoteUnchanged(fresh, s))
                {
                    remote[rel] = fresh;
                    if (Down) await DownloadAsync(fresh, path, stats, "changed remotely after local delete", ct);
                    return;
                }
                // Saving programs replace files by rename; the scan may have caught the moment it was gone.
                if (File.Exists(path)) { NeedsFollowUp = true; return; }
                if (!Planned(BackupMode ? "keep in the backup's versions (deleted locally)" : "delete remotely (deleted locally)", rel))
                {
                    if (BackupMode) await KeepRemoteVersionAsync(fresh, rel, ct);
                    else await _store.DeleteAsync(fresh, ct);
                }
                remote.Remove(rel);
                _state.Entries.Remove(rel);
                stats.Trashed++;
                if (!DryRun && !BackupMode) _log.Info($"[{_pair.Name}] deleted remotely (deleted locally): {rel}");
                Journal("deleted remotely", rel, s.Size, BackupMode ? "deleted here; kept in the backup's versions" : "deleted here first");
                return;
            }

            if (s == null)
            {
                // An online-only placeholder without history holds nothing local: describe the remote file.
                if (l.Dehydrated) { if (Down) await DownloadAsync(node, path, stats, "online-only placeholder without history", ct); return; }
                // Both sides have it and we have never synced it: content decides; in a one-way pair the source wins.
                if (await SameContentAsync(node, path, ct)) { RecordFile(rel, node, path); return; }
                if (_pair.Direction == SyncDirection.DownloadOnly) await DownloadAsync(node, path, stats, "differs; download-only pair", ct);
                else if (!Down) await UploadChangeAsync(node, rel, path, l, remote, stats, ct);
                else await ConflictAsync(node, rel, path, remote, stats, "exists on both sides with different content", ct);
                return;
            }

            var remoteChanged = !RemoteUnchanged(node, s);
            var localTouched = !l.Dehydrated && (l.Size != s.LocalSize || l.MtimeTicks != s.LocalMtimeTicks);
            if (localTouched && Up && Settling(l)) return; // still being saved: next cycle
            var localChanged = localTouched && !string.Equals(PathUtil.Md5Of(path), s.Md5, StringComparison.OrdinalIgnoreCase);
            if (localTouched && !localChanged)
            {
                s.LocalSize = l.Size; s.LocalMtimeTicks = l.MtimeTicks; // touched, same bytes
            }

            if (remoteChanged && localChanged)
            {
                if (await SameContentAsync(node, path, ct)) { RecordFile(rel, node, path); return; }
                if (_pair.Direction == SyncDirection.DownloadOnly) await DownloadAsync(node, path, stats, "changed on both sides; download-only pair", ct);
                else if (!Down) await UploadChangeAsync(node, rel, path, l, remote, stats, ct);
                else await ConflictAsync(node, rel, path, remote, stats, "changed on both sides", ct);
            }
            else if (remoteChanged)
            {
                if (Down) await DownloadAsync(node, path, stats, "changed remotely", ct);
            }
            else if (localChanged)
            {
                if (!Up) return; // download-only: local edits stay local until the remote file changes
                // About to overwrite remotely: make sure the cached tree is not hiding a newer remote version.
                var current = await _store.RefreshAsync(node, ct);
                if (current == null)
                {
                    remote.Remove(rel);
                    _state.Entries.Remove(rel);
                    _log.Warn($"[{_pair.Name}] '{rel}' vanished remotely while changed locally; it will be uploaded as new");
                    return;
                }
                if (!RemoteUnchanged(current, s) && _pair.Direction == SyncDirection.TwoWay)
                {
                    remote[rel] = current;
                    if (await SameContentAsync(current, path, ct)) { RecordFile(rel, current, path); return; }
                    await ConflictAsync(current, rel, path, remote, stats, "changed on both sides", ct);
                    return;
                }
                await UploadChangeAsync(current, rel, path, l, remote, stats, ct);
            }
        });
    }

    private async Task UploadChangeAsync(RemoteNode node, string rel, string path, LocalItem l, Dictionary<string, RemoteNode> remote, CycleStats stats, CancellationToken ct)
    {
        RemoteNode fresh;
        if (Planned(BackupMode ? "upload change (the previous version is kept)" : "upload change", rel, $"{l.Size:N0} bytes")) fresh = node;
        else if (BackupMode)
        {
            // The version on the other side moves into the versions folder; the new one is uploaded in its place.
            TryParent(rel, remote, out var parent);
            await KeepRemoteVersionAsync(node, rel, ct);
            fresh = await Tracked(SyncActivity.Direction.Up, rel, l.Size, () => _store.UploadNewAsync(path, rel, parent, ct));
        }
        else fresh = await Tracked(SyncActivity.Direction.Up, rel, l.Size, () => _store.UploadUpdateAsync(path, node, ct));
        remote[rel] = fresh;
        if (!DryRun) Uploaded(path, fresh.Id);
        RecordFile(rel, fresh, path);
        stats.Uploaded++;
        if (!DryRun) _log.Info($"[{_pair.Name}] uploaded change: {rel} ({l.Size:N0} bytes)");
        Journal("uploaded change", rel, l.Size, BackupMode ? "changed here; the previous version is kept in the backup" : "changed here");
        ChangeTransferred(rel, stats);
    }

    private async Task DownloadAsync(RemoteNode node, string path, CycleStats stats, string why, CancellationToken ct, bool keep = false)
    {
        if (!keep && !KeepLocal(path)) { Placeholder(node, path, stats, why); return; }
        if (OpenElsewhere(path))
        {
            // Replacing a file that Word or Excel has open would let its next save undo the new version.
            WarnOnce("open:" + node.RelPath, $"'{node.RelPath}' is open in another program; the new version comes once it is closed");
            return;
        }
        _doneThisCycle.Add(node.RelPath);
        var pin = OnDemand && (keep || ParentPinned(path) || (File.Exists(path) && CloudFiles.IsPinned(File.GetAttributes(path))));
        stats.Downloaded++;
        if (Planned("download", node.RelPath, $"{why}, {node.Size:N0} bytes{(File.Exists(path) ? ", replaces the local file (a copy is kept)" : "")}")) return;
        // The local file is about to be replaced: keep a copy first (an online-only placeholder has nothing to keep).
        if (File.Exists(path) && !CloudFiles.IsDehydrated(File.GetAttributes(path)))
        {
            try { VersionStore.Save(_pair, node.RelPath, path, _options); }
            catch (Exception ex) { _log.Warn($"[{_pair.Name}] could not keep a copy of {node.RelPath} before replacing it: {ex.Message}"); }
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var replaces = File.Exists(path);
        await Tracked(SyncActivity.Direction.Down, node.RelPath, node.Size, () => _store.DownloadAsync(node, path, ct));
        if (replaces) ChangeTransferred(node.RelPath, stats);
        Adopt(path, node.Id, pin);
        RecordFile(node.RelPath, node, path);
        _log.Info($"[{_pair.Name}] downloaded ({why}): {node.RelPath} ({node.Size:N0} bytes)");
        Journal("downloaded", node.RelPath, node.Size, why);
    }

    /// <summary>Keeps both: the local version is renamed and uploaded as its own file, the remote version takes the original name.</summary>
    private async Task ConflictAsync(RemoteNode node, string rel, string path, Dictionary<string, RemoteNode> remote, CycleStats stats, string why, CancellationToken ct)
    {
        var name = PathUtil.NameOf(rel);
        var ext = Path.GetExtension(name);
        var stem = name[..^ext.Length];
        var conflictName = $"{stem} (conflict {Environment.MachineName} {DateTime.Now:yyyy-MM-dd HHmm}){ext}";
        var conflictRel = PathUtil.Join(PathUtil.ParentOf(rel), conflictName);
        if (OpenElsewhere(path)) { WarnOnce("open:" + rel, $"'{rel}' is open in another program; the conflict is sorted out once it is closed"); return; }
        stats.Conflicts++;
        if (Planned("keep both (conflict)", rel, $"{why}; local copy becomes '{conflictName}'")) return;
        var conflictPath = Local(conflictRel);
        File.Move(path, conflictPath, overwrite: false);
        _log.Warn($"[{_pair.Name}] conflict ({why}): '{rel}' kept as '{conflictName}'");
        Journal("conflict", rel, detail: $"{why}; this computer's version kept as '{conflictName}'");

        await DownloadAsync(node, path, stats, "conflict, remote version", ct);

        TryParent(rel, remote, out var parent);
        var cnode = await Tracked(SyncActivity.Direction.Up, conflictRel, new FileInfo(conflictPath).Length, () => _store.UploadNewAsync(conflictPath, conflictRel, parent, ct));
        remote[conflictRel] = cnode;
        Uploaded(conflictPath, cnode.Id);
        RecordFile(conflictRel, cnode, conflictPath);
        stats.Uploaded++;
    }

    // ---------------------------------------------------- exports and links

    private async Task SyncDerivedAsync(RemoteNode node, EntryKind kind, Dictionary<string, RemoteNode> remote, Dictionary<string, LocalItem> local, HashSet<string> derived, CycleStats stats, CancellationToken ct)
    {
        var rel = node.RelPath;
        var derivedRel = DerivedName(node, kind)!;
        derived.Add(derivedRel);
        if (remote.ContainsKey(derivedRel))
        {
            WarnOnce(derivedRel, $"cannot write {kind.ToString().ToLowerInvariant()} '{derivedRel}': a real file by that name exists remotely");
            return;
        }
        var path = Local(derivedRel);
        _state.Entries.TryGetValue(rel, out var s);
        local.TryGetValue(derivedRel, out var l);

        var writeBack = kind == EntryKind.Export && _store.CanImport(node);
        if (s != null && s.Kind == kind && s.DriveId == node.Id && s.LocalName == derivedRel && l is { IsDir: false })
        {
            // Google Docs: the revision (asked this cycle) decides; Drive's modified time trails edits, write-backs included.
            var upToDate = _revisions.TryGetValue(rel, out var revNow) && s.Revision != null
                ? revNow == s.Revision
                : s.DriveModifiedUtc == node.ModifiedUtc && !_staleExports.Contains(rel);
            if (upToDate && s.DriveModifiedUtc != node.ModifiedUtc) s.DriveModifiedUtc = node.ModifiedUtc; // caught up
            var needsRepair = kind == EntryKind.Export && s.ExportVersion < DocxFix.Version; // made before a repair: once more
            if (upToDate && s.Revision == null && _revisions.TryGetValue(rel, out var firstRev)) s.Revision = firstRev; // from now on
            var editedLocally = l.Size != s.LocalSize || l.MtimeTicks != s.LocalMtimeTicks;
            if (editedLocally && writeBack && Up)
            {
                if (Settling(l)) return; // still being saved
                if (OpenElsewhere(path)) { WarnOnce("open:" + derivedRel, $"'{derivedRel}' is open in another program; it is synced once it is closed"); return; }
                string? why = null;
                if (upToDate)
                {
                    // Only a round trip that loses nothing (no tabs, no open comments) goes back into the document.
                    try { why = DryRun ? null : await _store.WhyNotImportAsync(node, ct); }
                    catch (Exception ex) { why = $"it could not be checked ({ex.Message})"; }
                    if (why == null)
                    {
                        await Try(rel, stats, () => WriteBackAsync(node, rel, derivedRel, path, remote, stats, ct));
                        return;
                    }
                    // Tabs or comments: carry the edit over paragraph by paragraph (tab by tab) instead of replacing all.
                    try
                    {
                        var size = new FileInfo(path).Length;
                        var (fresh, revision, notPatched) = await Tracked(SyncActivity.Direction.Up, derivedRel, size, () => _store.PatchAsync(node, path, ct));
                        if (fresh != null)
                        {
                            remote[rel] = fresh;
                            RecordWriteBack(rel, derivedRel, path, fresh, revision);
                            stats.Uploaded++;
                            _log.Info($"[{_pair.Name}] wrote back into the Google document, tab by tab: {derivedRel}");
                            Journal("written back", derivedRel, detail: "into the Google document, tab by tab");
                            return;
                        }
                        why = notPatched ?? why;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { why = $"{why}, and writing it back tab by tab failed ({ex.Message})"; }
                }
                // The edit is kept as its own file (uploaded as an ordinary file on the next cycle) and the export below
                // brings back the document's own version: edited here and changed in Google too, or not safe to write back.
                if (upToDate) stats.Uploaded++; else stats.Conflicts++;
                if (!Planned(upToDate ? "keep local edit of export as its own file" : "keep local edit of export as conflict copy", derivedRel, why))
                {
                    var ext = Path.GetExtension(derivedRel);
                    var asideRel = derivedRel[..^ext.Length] + $" ({(upToDate ? "edited" : "conflict")} {Environment.MachineName} {DateTime.Now:yyyy-MM-dd HHmm}){ext}";
                    // An ordinary file again (a files-on-demand placeholder would still carry the document's identity).
                    if (OnDemand && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) CloudFiles.Revert(path);
                    File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
                    File.Move(path, Local(asideRel));
                    local.Remove(derivedRel);
                    var name = PathUtil.NameOf(asideRel);
                    if (upToDate)
                    {
                        _log.Warn($"[{_pair.Name}] '{derivedRel}' not written back into the Google document, because {why}; your edit is kept as '{name}'");
                        stats.Notices.Add($"Your edit of {PathUtil.NameOf(derivedRel)} was saved as \"{name}\" and uploaded next to it: the Google document was not changed, because {why}.");
                    }
                    else _log.Warn($"[{_pair.Name}] conflict (edited here, changed in Google too): '{derivedRel}' kept as '{name}'");
                    Journal(upToDate ? "kept aside" : "conflict", derivedRel, detail: upToDate ? $"not written back ({why}); kept as '{name}'" : $"edited here and in Google; kept as '{name}'");
                }
            }
            else if (editedLocally && kind == EntryKind.Export)
            {
                _log.Warn($"[{_pair.Name}] '{derivedRel}' was edited locally; exports are one-way and the edit will not reach the original{(upToDate ? "" : " (and the original has changed since)")}");
                s.LocalSize = l.Size; s.LocalMtimeTicks = l.MtimeTicks; // warn once per edit
                if (upToDate) return;
            }
            else if (upToDate && !needsRepair)
            {
                // Exports that can be written back are ordinary, writable files (earlier versions made them read-only).
                if (writeBack && !DryRun && (l.Attributes & FileAttributes.ReadOnly) != 0)
                    try { File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly); } catch { }
                return;
            }
        }
        if (s?.LocalName != null && !string.Equals(s.LocalName, derivedRel, StringComparison.OrdinalIgnoreCase)
            && local.TryGetValue(s.LocalName, out var old) && !old.IsDir)
        {
            if (!Planned("remove old representation", s.LocalName)) RecycleBin.Delete(old.FullPath); // e.g. export -> link
            local.Remove(s.LocalName);
        }
        if (s is { Kind: EntryKind.File } && local.TryGetValue(rel, out var pointer) && !pointer.IsDir && LocalUnchanged(pointer, s))
        {
            // Until now the document was synced as its raw pointer file (a NAS .odoc before exports existed). The export or
            // link replaces that local copy; the document on the remote side is not touched.
            if (!Planned("replace local pointer file", rel))
            {
                var where = RecycleBin.Delete(pointer.FullPath);
                _log.Info($"[{_pair.Name}] local pointer file replaced by {kind.ToString().ToLowerInvariant()} (old copy to {where}): {rel}");
            }
            local.Remove(rel);
            derived.Add(rel);
        }

        if (File.Exists(path) && OpenElsewhere(path))
        {
            WarnOnce("open:" + derivedRel, $"'{derivedRel}' is open in another program; the new version comes once it is closed");
            return;
        }
        await Try(rel, stats, async () =>
        {
            if (kind == EntryKind.Link)
            {
                stats.Linked++;
                if (Planned("write link", derivedRel)) return;
                WriteLink(path, await _store.LinkUrlAsync(node, ct), node.ModifiedUtc);
                _log.Debug($"[{_pair.Name}] link: {derivedRel}");
            }
            else
            {
                stats.Exported++;
                if (Planned("export", derivedRel)) return;
                await Tracked(SyncActivity.Direction.Down, derivedRel, node.Size, () => _store.ExportAsync(node, ExportMime(node.MimeType)!, path, ct));
                // Read-only where an edit could not go back (Synology Office); otherwise edits are written back.
                if (!writeBack) File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
                _log.Info($"[{_pair.Name}] exported: {derivedRel}");
                Journal("exported", derivedRel, detail: "from the Google document");
            }
            Adopt(path, node.Id, pin: true); // files on demand: shown as in sync, always kept (they are made here)
            var info = new FileInfo(path);
            _state.Entries[rel] = new StateEntry
            {
                Path = rel, Kind = kind, DriveId = node.Id, Md5 = node.Md5, Size = node.Size, DriveModifiedUtc = node.ModifiedUtc,
                LocalName = derivedRel, LocalSize = info.Length, LocalMtimeTicks = info.LastWriteTimeUtc.Ticks,
                Revision = _revisions.GetValueOrDefault(rel), ExportVersion = DocxFix.Version,
            };
            local[derivedRel] = new LocalItem { RelPath = derivedRel, FullPath = path, Size = info.Length, MtimeTicks = info.LastWriteTimeUtc.Ticks };
        });
    }

    private int _docsRotation;
    /// <summary>This cycle's revisions of the exported Google Docs that were asked (rel -> revision).</summary>
    private readonly Dictionary<string, string> _revisions = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Exports whose document has a newer revision than the one they were made from.</summary>
    private readonly HashSet<string> _staleExports = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Drive's change feed reports edits of Google documents minutes late (5 minutes was measured), while asking for the
    /// document itself is always current. So the exported documents are asked directly: the 10 most recently changed
    /// every cycle, and 10 more of the rest in turn, which keeps it cheap with many documents.
    /// </summary>
    /// <summary>Returns true when a checked document changed (modified time or revision).</summary>
    private async Task<bool> FreshenExportedDocsAsync(Dictionary<string, RemoteNode> remote, CancellationToken ct)
    {
        _revisions.Clear();
        _staleExports.Clear();
        var changed = false;
        if (_store.Kind != "Drive" || !Down) return false;
        var docs = _state.Entries.Values.Where(e => e.Kind == EntryKind.Export && remote.ContainsKey(e.Path))
            .OrderByDescending(e => e.DriveModifiedUtc).Select(e => e.Path).ToList();
        if (docs.Count == 0) return false;
        var rest = docs.Skip(10).ToList();
        var pick = docs.Take(10).ToList();
        if (rest.Count > 0)
        {
            _docsRotation %= rest.Count;
            pick.AddRange(rest.Skip(_docsRotation).Take(10));
            _docsRotation += 10;
        }
        foreach (var rel in pick)
        {
            try
            {
                var fresh = await _store.RefreshAsync(remote[rel], ct);
                if (fresh == null) continue;
                if (fresh.ModifiedUtc != remote[rel].ModifiedUtc) { remote[rel] = fresh; changed = true; }
                // Google Docs: the revision changes with the edit itself, the modified time only a minute or two later.
                if (await _store.RevisionAsync(fresh, ct) is { } rev)
                {
                    _revisions[rel] = rev;
                    if (_state.Entries.TryGetValue(rel, out var e) && e.Revision != null && e.Revision != rev) { _staleExports.Add(rel); changed = true; }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { _log.Debug($"[{_pair.Name}] {rel}: {ex.Message}"); }
        }
        return changed;
    }

    /// <summary>
    /// After a write-back the local file stays as it is (no re-export of the document right away, which would rewrite a
    /// file that is probably still open): the state says it matches the document at this revision.
    /// </summary>
    private void RecordWriteBack(string rel, string derivedRel, string path, RemoteNode fresh, string? revision)
    {
        Adopt(path, fresh.Id, pin: true);
        var info = new FileInfo(path);
        _state.Entries[rel] = new StateEntry
        {
            Path = rel, Kind = EntryKind.Export, DriveId = fresh.Id, Md5 = fresh.Md5, Size = fresh.Size, DriveModifiedUtc = fresh.ModifiedUtc,
            LocalName = derivedRel, LocalSize = info.Length, LocalMtimeTicks = info.LastWriteTimeUtc.Ticks, Revision = revision, ExportVersion = DocxFix.Version,
        };
    }

    /// <summary>An edited export goes back into its Google document (Drive converts it; the old content stays in its history).</summary>
    private async Task WriteBackAsync(RemoteNode node, string rel, string derivedRel, string path, Dictionary<string, RemoteNode> remote, CycleStats stats, CancellationToken ct)
    {
        stats.Uploaded++;
        if (Planned("write back into the Google document", derivedRel)) return;
        var size = new FileInfo(path).Length;
        var fresh = await Tracked(SyncActivity.Direction.Up, derivedRel, size, () => _store.ImportAsync(node, path, ExportMime(node.MimeType)!, ct));
        remote[rel] = fresh;
        Adopt(path, fresh.Id, pin: true);
        string? revision = null;
        try { revision = await _store.RevisionAsync(fresh, ct); } catch { }
        RecordWriteBack(rel, derivedRel, path, fresh, revision);
        _log.Info($"[{_pair.Name}] wrote back into the Google document: {derivedRel} ({size:N0} bytes)");
        Journal("written back", derivedRel, size, "into the Google document");
    }

    private static void WriteLink(string path, string url, DateTime modifiedUtc)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[InternetShortcut]");
        sb.AppendLine($"URL={url}");
        if (File.Exists(path)) File.SetAttributes(path, FileAttributes.Normal);
        File.WriteAllText(path, sb.ToString(), Encoding.ASCII);
        File.SetLastWriteTimeUtc(path, modifiedUtc);
    }

    private EntryKind Classify(RemoteNode node, Dictionary<string, LocalItem> local)
    {
        var kind = ClassifyAny(node, local);
        return !Down && kind is EntryKind.Export or EntryKind.Link ? EntryKind.Folder : kind;
    }

    private EntryKind ClassifyAny(RemoteNode node, Dictionary<string, LocalItem> local)
    {
        if (node.IsFolder) return EntryKind.Folder;
        var canLink = _store.CanLink(node);
        if (node.IsNativeDocument)
            return _pair.GoogleDocs switch
            {
                GoogleDocsMode.Skip => EntryKind.Folder, // caller treats Folder from a file as "skip"
                GoogleDocsMode.Link => canLink ? EntryKind.Link : EntryKind.Folder,
                _ => ExportMime(node.MimeType) != null ? EntryKind.Export : canLink ? EntryKind.Link : EntryKind.Folder,
            };
        var cloudOnly = (_pair.CloudOnlyMinMB > 0 && node.Size >= (long)_pair.CloudOnlyMinMB * 1024 * 1024)
                        || _pair.CloudOnlyFolders.Any(f => IsUnder(node.RelPath, f));
        if (cloudOnly && !canLink)
        {
            WarnOnce("cloud-only", $"cloud-only rules are ignored: {_store.Kind} offers no browser link to point to");
            cloudOnly = false;
        }
        // A cloud-only file that the user has a real local copy of is simply a synced file.
        if (cloudOnly && !(local.TryGetValue(node.RelPath, out var l) && !l.IsDir)) return EntryKind.Link;
        return EntryKind.File;
    }

    internal static string? DerivedName(RemoteNode node, EntryKind kind) => kind switch
    {
        EntryKind.Link => node.RelPath + ".url",
        // Google Docs have no extension ("Plan" -> "Plan.docx"); Synology Office files do ("Plan.odoc" -> "Plan.docx").
        EntryKind.Export => (node.IsSynologyOffice ? node.RelPath[..^Path.GetExtension(node.RelPath).Length] : node.RelPath) + ExportExt(node.MimeType),
        _ => null,
    };

    internal static string? ExportMime(string mime) => mime switch
    {
        "application/vnd.google-apps.document" or RemoteNode.SynologyDocument => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "application/vnd.google-apps.spreadsheet" or RemoteNode.SynologySpreadsheet => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        "application/vnd.google-apps.presentation" or RemoteNode.SynologyPresentation => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        "application/vnd.google-apps.drawing" => "image/png",
        _ => null,
    };

    private static string? ExportExt(string mime) => mime switch
    {
        "application/vnd.google-apps.document" or RemoteNode.SynologyDocument => ".docx",
        "application/vnd.google-apps.spreadsheet" or RemoteNode.SynologySpreadsheet => ".xlsx",
        "application/vnd.google-apps.presentation" or RemoteNode.SynologyPresentation => ".pptx",
        "application/vnd.google-apps.drawing" => ".png",
        _ => null,
    };

    // ---------------------------------------------------------------- helpers

    private void RecordFile(string rel, RemoteNode node, string path)
    {
        var info = new FileInfo(path);
        _state.Entries[rel] = new StateEntry
        {
            Path = rel, Kind = EntryKind.File, DriveId = node.Id,
            // Without a remote checksum (NAS) the local one stands in; local edits are still detected against it.
            Md5 = node.Md5 ?? (CloudFiles.IsDehydrated(info.Attributes) ? null : PathUtil.Md5Of(path)),
            Size = node.Size, DriveModifiedUtc = node.ModifiedUtc,
            LocalSize = info.Length, LocalMtimeTicks = info.LastWriteTimeUtc.Ticks,
        };
    }

    private void UpsertFolderEntry(string rel, string id)
    {
        if (_state.Entries.TryGetValue(rel, out var s) && s.Kind == EntryKind.Folder && s.DriveId == id) return;
        _state.Entries[rel] = new StateEntry { Path = rel, Kind = EntryKind.Folder, DriveId = id };
    }

    /// <summary>The remote node of a path's parent, or null for the pair root. False when the parent is not on the remote (creation failed).</summary>
    private static bool TryParent(string rel, Dictionary<string, RemoteNode> remote, out RemoteNode? parent)
    {
        var parentRel = PathUtil.ParentOf(rel);
        parent = null;
        if (parentRel.Length == 0) return true;
        return remote.TryGetValue(parentRel, out parent);
    }

    /// <summary>With a remote checksum, compare that; otherwise fall back to what the remote reports as size and modified time.</summary>
    private static bool RemoteUnchanged(RemoteNode node, StateEntry s) =>
        node.Md5 != null && s.Md5 != null
            ? string.Equals(node.Md5, s.Md5, StringComparison.OrdinalIgnoreCase)
            : node.ModifiedUtc == s.DriveModifiedUtc && node.Size == s.Size;

    private static bool LocalUnchanged(LocalItem l, StateEntry s) =>
        l.Dehydrated // online-only: nothing local that could have changed
        || (l.Size == s.LocalSize && l.MtimeTicks == s.LocalMtimeTicks)
        || (l.Size == s.Size && string.Equals(PathUtil.Md5Of(l.FullPath), s.Md5, StringComparison.OrdinalIgnoreCase));

    /// <summary>Compares content; a remote without checksums is downloaded to a scratch file next to the target for that.</summary>
    private async Task<bool> SameContentAsync(RemoteNode node, string path, CancellationToken ct)
    {
        if (new FileInfo(path).Length != node.Size) return false;
        if (node.Md5 != null)
            return string.Equals(PathUtil.Md5Of(path), node.Md5, StringComparison.OrdinalIgnoreCase);
        var scratch = path + ".unisono-cmp";
        try
        {
            await _store.DownloadAsync(node, scratch, ct);
            return PathUtil.Md5Of(scratch) == PathUtil.Md5Of(path);
        }
        finally
        {
            if (File.Exists(scratch)) File.Delete(scratch);
        }
    }

    private bool RemoteTreeHasChanges(string rel, Dictionary<string, RemoteNode> remote)
    {
        var prefix = rel + "/";
        foreach (var n in remote.Values.Where(n => !n.IsFolder && n.RelPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            if (!_state.Entries.TryGetValue(n.RelPath, out var s) || (s.Kind == EntryKind.File && !RemoteUnchanged(n, s)))
                return true;
        return false;
    }

    private bool LocalTreeHasChanges(string rel, Dictionary<string, LocalItem> local)
    {
        var prefix = rel + "/";
        var derivedNames = new HashSet<string>(_state.Entries.Values.Where(e => e.LocalName != null).Select(e => e.LocalName!), StringComparer.OrdinalIgnoreCase);
        foreach (var l in local.Values.Where(l => !l.IsDir && l.RelPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            if (_state.Entries.TryGetValue(l.RelPath, out var s))
            {
                if (s.Kind == EntryKind.File && !LocalUnchanged(l, s)) return true;
                continue;
            }
            if (derivedNames.Contains(l.RelPath)) continue; // derived files are keyed by their remote path, not the local name
            return true;
        }
        return false;
    }

    /// <summary>Runs a transfer so the tray can show it (arrows, the file in the tooltip).</summary>
    /// <summary>
    /// Open in another program (Word, Excel...): an exclusive open fails, or Office's owner file "~$..." sits next to it.
    /// An online-only placeholder is never "open": it has no content here to protect.
    /// </summary>
    private static bool OpenElsewhere(string path)
    {
        try
        {
            if (!File.Exists(path) || CloudFiles.IsDehydrated(File.GetAttributes(path))) return false;
            var dir = Path.GetDirectoryName(path)!;
            var name = Path.GetFileName(path);
            if (File.Exists(Path.Combine(dir, "~$" + name)) || (name.Length > 2 && File.Exists(Path.Combine(dir, "~$" + name[2..])))
                || (name.Length > 1 && File.Exists(Path.Combine(dir, "~$" + name[1..])))) return true;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private async Task<T> Tracked<T>(SyncActivity.Direction dir, string rel, long size, Func<Task<T>> op)
    {
        using var t = SyncActivity.Begin(dir, _pair.Name, rel, size);
        var result = await op();
        t.Complete();
        return result;
    }

    private Task Tracked(SyncActivity.Direction dir, string rel, long size, Func<Task> op) =>
        Tracked(dir, rel, size, async () => { await op(); return true; });

    private async Task Try(string rel, CycleStats stats, Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            stats.Errors++;
            stats.LastErrorText = $"{rel}: {Log.Text(ex)}";
            _log.Error($"[{_pair.Name}] {rel}: {ex.Message}");
        }
    }

    /// <summary>Records a done action for the Activity view (not in a preview).</summary>
    private void Journal(string action, string rel, long size = -1, string? detail = null)
    {
        if (!DryRun) ActivityJournal.Add(_pair, action, rel, size, detail);
    }

    private void WarnOnce(string key, string message)
    {
        if (_warnedOnce.Add(key)) _log.Warn($"[{_pair.Name}] {message}");
    }

    private string Local(string rel) => Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));

    private static bool IsUnder(string rel, string folder)
    {
        var f = folder.Replace('\\', '/').Trim('/');
        return f.Length > 0 && rel.StartsWith(f + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static void RemoveTree<T>(Dictionary<string, T> dict, string rel)
    {
        var prefix = rel + "/";
        foreach (var k in dict.Keys.Where(k => k.Equals(rel, StringComparison.OrdinalIgnoreCase) || k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
            dict.Remove(k);
    }
}
