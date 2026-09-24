namespace Unisono;

/// <summary>
/// Rename and move detection. Without it a rename is a delete plus an upload (or download): correct, but the data
/// travels again and the remote item loses its identity (Drive revisions, sharing, links). Recognised moves:
///
///   local folder rename/move  a synced folder is gone locally, an unknown folder appeared, and most files in it match
///                              the old folder's files by relative path, size and modified time -> one remote move
///   local file rename/move    a synced file is gone locally, exactly one unknown file has its size and modified time,
///                              and the checksum matches -> remote move
///   remote folder move        Drive: a folder id seen before shows up at another path; NAS (ids are paths): a new folder
///                              holds most of a vanished folder's files (same relative path, size, time) -> local move
///   remote file move          Drive: same id at another path; NAS: exactly one new file with the same size and
///                              modified time as a vanished one -> local move
///
/// Anything ambiguous (several candidates, a changed file, a missing parent, a protected folder) is left to the normal
/// passes, which handle it as a delete plus a copy. One-way pairs only move on the side they may change.
/// </summary>
public sealed partial class SyncEngine
{
    private async Task DetectFolderMovesAsync(Dictionary<string, RemoteNode> remote, Dictionary<string, LocalItem> local, CycleStats stats, CancellationToken ct)
    {
        if (Up)
        {
            var newFolders = local.Values.Where(i => i.IsDir && !_state.Entries.ContainsKey(i.RelPath) && !remote.ContainsKey(i.RelPath)).ToList();
            var gone = _state.Entries.Values
                .Where(e => e.Kind == EntryKind.Folder && !local.ContainsKey(e.Path) && remote.TryGetValue(e.Path, out var n) && n.IsFolder && n.Id == e.DriveId)
                .OrderBy(e => PathUtil.Depth(e.Path)).ToList();
            foreach (var old in gone)
            {
                ct.ThrowIfCancellationRequested();
                if (!remote.ContainsKey(old.Path) || IsProtected(old.Path)) continue; // moved with a parent already, or partly synced
                var oldFiles = _state.Entries.Values.Where(e => e.Kind == EntryKind.File && e.Path.StartsWith(old.Path + "/", StringComparison.OrdinalIgnoreCase)).ToList();
                if (oldFiles.Count == 0) continue;

                // The candidate whose content matches best; it must be a clear winner.
                var scored = newFolders.Where(n => local.ContainsKey(n.RelPath) && !IsProtected(n.RelPath))
                    .Select(n => (Folder: n, Score: oldFiles.Count(e =>
                        local.TryGetValue(n.RelPath + e.Path[old.Path.Length..], out var li) && !li.IsDir
                        && li.Size == e.LocalSize && li.MtimeTicks == e.LocalMtimeTicks)))
                    .Where(x => x.Score * 2 >= oldFiles.Count && x.Score > 0)
                    .OrderByDescending(x => x.Score).ToList();
                if (scored.Count == 0 || (scored.Count > 1 && scored[1].Score == scored[0].Score)) continue;
                var target = scored[0].Folder.RelPath;
                if (remote.ContainsKey(target) || !TryParent(target, remote, out var parent)) continue;

                var from = old.Path;
                await Try(from, stats, async () =>
                {
                    var oldNode = remote[from];
                    var moved = Planned("move folder remotely", old.Path, "to " + target) ? DryNode(target, parent, folder: true)
                        : await _store.MoveAsync(oldNode, target, parent, ct);
                    RekeyRemote(remote, from, target, oldNode.Id, moved);
                    RekeyState(from, target, oldNode.Id, moved.Id);
                    MovedInSync(target);
                    newFolders.RemoveAll(n => n.RelPath.Equals(target, StringComparison.OrdinalIgnoreCase));
                    stats.Moved++;
                    if (!DryRun) _log.Info($"[{_pair.Name}] moved folder remotely (moved locally): {from} -> {target}");
                });
            }
        }

        if (Down)
        {
            var goneHere = _state.Entries.Values.Where(e => e.Kind == EntryKind.Folder && !remote.ContainsKey(e.Path) && local.TryGetValue(e.Path, out var l) && l.IsDir).ToList();
            var byId = goneHere.GroupBy(e => e.DriveId).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.First());
            foreach (var node in remote.Values.Where(n => n.IsFolder && !_state.Entries.ContainsKey(n.RelPath) && !local.ContainsKey(n.RelPath))
                         .OrderBy(n => PathUtil.Depth(n.RelPath)).ToList())
            {
                ct.ThrowIfCancellationRequested();
                StateEntry? old;
                if (_store.HasStableIds) byId.TryGetValue(node.Id, out old);
                else old = MatchByContent(node, goneHere, remote); // NAS: ids are paths, so recognise the folder by what is in it
                if (old == null || !local.ContainsKey(old.Path) || IsProtected(old.Path) || IsProtected(node.RelPath)) continue;
                goneHere.Remove(old);
                var parentRel = PathUtil.ParentOf(node.RelPath);
                if (parentRel.Length > 0 && !(local.GetValueOrDefault(parentRel)?.IsDir ?? false)) continue;
                var from = old.Path; var fromId = old.DriveId;
                await Try(from, stats, () =>
                {
                    if (!Planned("move folder locally", old.Path, "to " + node.RelPath))
                        Directory.Move(Local(from), Local(node.RelPath));
                    RekeyLocal(local, from, node.RelPath);
                    RekeyState(from, node.RelPath, fromId, node.Id); // NAS: the old path becomes the new one below it
                    MovedInSync(node.RelPath);
                    stats.Moved++;
                    if (!DryRun) _log.Info($"[{_pair.Name}] moved folder locally (moved remotely): {from} -> {node.RelPath}");
                    return Task.CompletedTask;
                });
            }
        }
    }

    private async Task DetectFileMovesAsync(Dictionary<string, RemoteNode> remote, Dictionary<string, LocalItem> local, CycleStats stats, CancellationToken ct)
    {
        var derivedNames = new HashSet<string>(_state.Entries.Values.Where(e => e.LocalName != null).Select(e => e.LocalName!), StringComparer.OrdinalIgnoreCase);

        if (Up)
        {
            var gone = _state.Entries.Values
                .Where(e => e.Kind == EntryKind.File && !local.ContainsKey(e.Path) && remote.TryGetValue(e.Path, out var n) && !n.IsFolder && RemoteUnchanged(n, e))
                .ToList();
            var fresh = local.Values.Where(i => !i.IsDir && !_state.Entries.ContainsKey(i.RelPath) && !remote.ContainsKey(i.RelPath) && !derivedNames.Contains(i.RelPath) && !LocalScanner.Unsettled(i))
                .ToList();
            foreach (var old in gone)
            {
                ct.ThrowIfCancellationRequested();
                var candidates = fresh.Where(f => f.Size == old.LocalSize && f.MtimeTicks == old.LocalMtimeTicks).ToList();
                if (candidates.Count != 1) continue;
                var f = candidates[0];
                // The same file must not also be the best match for another vanished file.
                if (gone.Count(g => g.LocalSize == f.Size && g.LocalMtimeTicks == f.MtimeTicks) != 1) continue;
                // An online-only placeholder cannot be hashed without downloading it; its unique size and time suffice.
                if (!f.Dehydrated && !string.Equals(PathUtil.Md5Of(f.FullPath), old.Md5, StringComparison.OrdinalIgnoreCase)) continue;
                if (!TryParent(f.RelPath, remote, out var parent)) continue;

                var from = old.Path;
                await Try(from, stats, async () =>
                {
                    var oldNode = remote[from];
                    var moved = Planned("move remotely", old.Path, "to " + f.RelPath) ? DryNode(f.RelPath, parent, folder: false)
                        : await _store.MoveAsync(oldNode, f.RelPath, parent, ct);
                    remote.Remove(from);
                    remote[f.RelPath] = moved;
                    MoveEntry(from, f.RelPath, moved);
                    MovedInSync(f.RelPath);
                    fresh.Remove(f);
                    stats.Moved++;
                    if (!DryRun) _log.Info($"[{_pair.Name}] moved remotely (moved locally): {from} -> {f.RelPath}");
                });
            }
        }

        if (Down)
        {
            var gone = _state.Entries.Values
                .Where(e => e.Kind == EntryKind.File && !remote.ContainsKey(e.Path) && local.TryGetValue(e.Path, out var l) && !l.IsDir && LocalUnchanged(l, e))
                .ToList();
            var fresh = remote.Values.Where(n => !n.IsFolder && !_state.Entries.ContainsKey(n.RelPath) && !local.ContainsKey(n.RelPath)).ToList();
            foreach (var old in gone)
            {
                ct.ThrowIfCancellationRequested();
                List<RemoteNode> candidates = _store.HasStableIds
                    ? fresh.Where(n => n.Id == old.DriveId).ToList()
                    : fresh.Where(n => n.Size == old.Size && n.ModifiedUtc == old.DriveModifiedUtc).ToList();
                if (candidates.Count != 1) continue;
                var node = candidates[0];
                if (!_store.HasStableIds && gone.Count(g => g.Size == node.Size && g.DriveModifiedUtc == node.ModifiedUtc) != 1) continue;
                if (node.IsNativeDocument) continue; // exports and links are regenerated instead
                var parentRel = PathUtil.ParentOf(node.RelPath);
                if (parentRel.Length > 0 && !(local.GetValueOrDefault(parentRel)?.IsDir ?? false)) continue;
                if (File.Exists(Local(node.RelPath)) || Directory.Exists(Local(node.RelPath))) continue;

                var from = old.Path;
                await Try(from, stats, () =>
                {
                    if (!Planned("move locally", old.Path, "to " + node.RelPath))
                        File.Move(Local(old.Path), Local(node.RelPath));
                    var l = local[old.Path];
                    local.Remove(old.Path);
                    local[node.RelPath] = new LocalItem { RelPath = node.RelPath, FullPath = Local(node.RelPath), Size = l.Size, MtimeTicks = l.MtimeTicks, Attributes = l.Attributes };
                    MoveEntry(old.Path, node.RelPath, node);
                    MovedInSync(node.RelPath);
                    fresh.Remove(node);
                    stats.Moved++;
                    if (!DryRun) _log.Info($"[{_pair.Name}] moved locally (moved remotely): {from} -> {node.RelPath}");
                    return Task.CompletedTask;
                });
            }
        }
    }

    /// <summary>
    /// The vanished folder whose synced files reappear under <paramref name="node"/> with the same relative path, size and
    /// modified time, for at least half of them; null when there is no clear winner.
    /// </summary>
    private StateEntry? MatchByContent(RemoteNode node, List<StateEntry> gone, Dictionary<string, RemoteNode> remote)
    {
        var scored = gone.Select(old =>
            {
                var files = _state.Entries.Values.Where(e => e.Kind == EntryKind.File && e.Path.StartsWith(old.Path + "/", StringComparison.OrdinalIgnoreCase)).ToList();
                var hits = files.Count(e => remote.TryGetValue(node.RelPath + e.Path[old.Path.Length..], out var r) && !r.IsFolder
                                            && r.Size == e.Size && r.ModifiedUtc == e.DriveModifiedUtc);
                return (Old: old, Hits: hits, Files: files.Count);
            })
            .Where(x => x.Hits > 0 && x.Hits * 2 >= x.Files)
            .OrderByDescending(x => x.Hits).ToList();
        if (scored.Count == 0 || (scored.Count > 1 && scored[1].Hits == scored[0].Hits)) return null;
        return scored[0].Old;
    }

    /// <summary>Files on demand: Windows marks a renamed or moved placeholder out of sync; carried over, it is in sync again.</summary>
    private void MovedInSync(string rel)
    {
        if (!OnDemand || DryRun) return;
        try { CloudFiles.MarkInSync(Local(rel)); }
        catch (Exception ex) { _log.Debug($"[{_pair.Name}] {rel}: {ex.Message}"); }
    }

    /// <summary>The state entry of a moved file: new key and remote identity, same content and local stamps.</summary>
    private void MoveEntry(string oldRel, string newRel, RemoteNode node)
    {
        if (!_state.Entries.Remove(oldRel, out var e)) return;
        e.Path = newRel;
        e.DriveId = node.Id;
        // What the remote reports after the move, so the next cycle does not mistake it for a change.
        if (node.ModifiedUtc != default) e.DriveModifiedUtc = node.ModifiedUtc;
        _state.Entries[newRel] = e;
    }

    /// <summary>Re-keys a moved folder and everything under it in the remote tree (NAS ids are paths and move along).</summary>
    private void RekeyRemote(Dictionary<string, RemoteNode> remote, string oldRel, string newRel, string oldId, RemoteNode moved)
    {
        foreach (var key in remote.Keys.Where(k => k.Equals(oldRel, StringComparison.OrdinalIgnoreCase) || k.StartsWith(oldRel + "/", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            var n = remote[key];
            remote.Remove(key);
            if (key.Equals(oldRel, StringComparison.OrdinalIgnoreCase)) { remote[newRel] = moved; continue; }
            n.RelPath = newRel + key[oldRel.Length..];
            n.Id = _store.RebaseId(n.Id, oldId, moved.Id);
            n.ParentId = n.ParentId == oldId ? moved.Id : _store.RebaseId(n.ParentId, oldId, moved.Id);
            remote[n.RelPath] = n;
        }
    }

    private void RekeyLocal(Dictionary<string, LocalItem> local, string oldRel, string newRel)
    {
        foreach (var key in local.Keys.Where(k => k.Equals(oldRel, StringComparison.OrdinalIgnoreCase) || k.StartsWith(oldRel + "/", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            var l = local[key];
            local.Remove(key);
            var rel = newRel + key[oldRel.Length..];
            local[rel] = new LocalItem { RelPath = rel, FullPath = Local(rel), IsDir = l.IsDir, Size = l.Size, MtimeTicks = l.MtimeTicks, Attributes = l.Attributes };
        }
    }

    /// <summary>Re-keys the state of a moved folder: paths, derived local names and (for a NAS) remote ids.</summary>
    private void RekeyState(string oldRel, string newRel, string oldId, string newId)
    {
        foreach (var e in _state.Entries.Values.Where(e => e.Path.Equals(oldRel, StringComparison.OrdinalIgnoreCase) || e.Path.StartsWith(oldRel + "/", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            _state.Entries.Remove(e.Path);
            e.Path = newRel + e.Path[oldRel.Length..];
            if (e.LocalName != null && e.LocalName.StartsWith(oldRel + "/", StringComparison.OrdinalIgnoreCase))
                e.LocalName = newRel + e.LocalName[oldRel.Length..];
            e.DriveId = e.DriveId == oldId ? newId : _store.RebaseId(e.DriveId, oldId, newId);
            _state.Entries[e.Path] = e;
        }
    }
}
