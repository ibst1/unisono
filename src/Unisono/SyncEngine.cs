using System.Text;

namespace Unisono;

public sealed class CycleStats
{
    public int Downloaded, Uploaded, Exported, Linked, Trashed, Recycled, Conflicts, Errors;
    public bool Any => Downloaded + Uploaded + Exported + Linked + Trashed + Recycled + Conflicts + Errors > 0;
    public override string ToString() =>
        $"{Downloaded} down, {Uploaded} up, {Exported} exported, {Linked} linked, {Trashed} trashed on Drive, {Recycled} recycled locally, {Conflicts} conflicts, {Errors} errors";
}

/// <summary>
/// One sync pair. Each cycle compares three things per path: what Drive has (from the change-fed index), what the
/// disk has (a fresh stat walk) and what both looked like last time they agreed (the state). That third leg is what
/// tells a deletion from a never-seen file, and a local edit from a remote one.
///
/// Safety rules: deletes go to the Drive trash / Recycle Bin; a folder is only deleted on the other side when
/// nothing inside it changed since the last sync; when both sides changed, both versions are kept.
/// Derived items (Google Docs exports, cloud-only links) are one-way: Drive is the source, local copies are
/// regenerated, and deleting or editing them locally changes nothing on Drive.
/// </summary>
public sealed class SyncEngine
{
    private readonly DriveClient _drive;
    private readonly SyncPair _pair;
    private readonly Log _log;
    private readonly string _root;
    private readonly SyncState _state;
    private RemoteIndex? _index;
    private DateTime _lastFullIndexUtc;
    private readonly HashSet<string> _warnedOnce = new(StringComparer.OrdinalIgnoreCase);

    public SyncPair Pair => _pair;

    public SyncEngine(DriveClient drive, SyncPair pair, Log log)
    {
        _drive = drive;
        _pair = pair;
        _log = log;
        _root = Path.GetFullPath(pair.LocalPath);
        _state = SyncState.Load(pair);
        _lastFullIndexUtc = _state.IndexBuiltUtc;
    }

    public async Task<CycleStats> RunCycleAsync(CancellationToken ct)
    {
        var stats = new CycleStats();
        if (!Directory.Exists(_root))
        {
            _log.Warn($"[{_pair.Name}] local folder {_root} does not exist; nothing done");
            return stats;
        }

        await EnsureIndexAsync(ct);
        var remote = _index!.BuildTree(_log);
        var local = LocalScanner.Scan(_root, _log);
        try
        {
            await SyncFoldersAsync(remote, local, stats, ct);
            await SyncFilesAsync(remote, local, stats, ct);
        }
        finally
        {
            _index.SaveTo(_state);
            _state.IndexBuiltUtc = _lastFullIndexUtc;
            _state.Save(_pair);
        }
        if (stats.Any) _log.Info($"[{_pair.Name}] {stats}");
        return stats;
    }

    // ------------------------------------------------------------------ index

    private async Task EnsureIndexAsync(CancellationToken ct)
    {
        if (_index == null)
        {
            var folder = await _drive.ResolveFolderAsync(_pair.DrivePath, ct);
            if (_state.RootId != null && _state.RootId != folder.Id)
                throw new InvalidOperationException(
                    $"State file {SyncState.FilePathFor(_pair)} belongs to a different Drive folder. Delete it to start over (existing local files will then be matched by content).");
            _index = RemoteIndex.FromState(_state);
            if (_index == null)
                _index = await RebuildIndexAsync(folder.Id, ct);
        }
        if (_pair.FullRescanMinutes > 0 && DateTime.UtcNow - _lastFullIndexUtc > TimeSpan.FromMinutes(_pair.FullRescanMinutes))
        {
            _index = await RebuildIndexAsync(_index.RootId, ct);
            return;
        }
        try
        {
            await _index.ApplyChangesAsync(_drive, _log, ct);
        }
        catch (StaleChangesTokenException ex)
        {
            _log.Warn($"[{_pair.Name}] {ex.Message}");
            _index = await RebuildIndexAsync(_index.RootId, ct);
        }
    }

    private async Task<RemoteIndex> RebuildIndexAsync(string rootId, CancellationToken ct)
    {
        _log.Debug($"[{_pair.Name}] indexing Drive folder...");
        var index = await RemoteIndex.BuildAsync(_drive, rootId, ct);
        _lastFullIndexUtc = DateTime.UtcNow;
        _log.Info($"[{_pair.Name}] indexed {index.Count} items on Drive");
        return index;
    }

    // ---------------------------------------------------------------- folders

    private async Task SyncFoldersAsync(Dictionary<string, RemoteNode> remote, Dictionary<string, LocalItem> local, CycleStats stats, CancellationToken ct)
    {
        // Drive folders, shallow first so parents exist before children.
        foreach (var node in remote.Values.Where(n => n.IsFolder).OrderBy(n => Depth(n.RelPath)).ToList())
        {
            ct.ThrowIfCancellationRequested();
            var rel = node.RelPath;
            if (!remote.ContainsKey(rel)) continue; // an ancestor was trashed in this pass
            local.TryGetValue(rel, out var l);
            _state.Entries.TryGetValue(rel, out var s);

            if (l is { IsDir: true })
            {
                UpsertFolderEntry(rel, node.Id);
                continue;
            }
            if (l != null)
            {
                WarnOnce(rel, $"'{rel}' is a folder on Drive but a file locally; leaving both alone");
                RemoveTree(remote, rel);
                continue;
            }
            if (s is { Kind: EntryKind.Folder } && !RemoteTreeHasChanges(rel, remote))
            {
                // Deleted locally, unchanged on Drive since we last saw it: propagate.
                await Try(rel, stats, async () =>
                {
                    await _drive.TrashAsync(node.Id, ct);
                    _index!.RemoveSubtree(node.Id);
                    _state.RemoveUnder(rel);
                    RemoveTree(remote, rel);
                    stats.Trashed++;
                    _log.Info($"[{_pair.Name}] trashed folder on Drive (deleted locally): {rel}");
                });
                continue;
            }
            if (s is { Kind: EntryKind.Folder })
            {
                // Deleted locally but Drive has new content in it: keep Drive's version, forget the old state so
                // the file pass treats everything inside as new and downloads it rather than trashing it.
                _log.Warn($"[{_pair.Name}] folder '{rel}' was deleted locally but changed on Drive; restoring it");
                _state.RemoveUnder(rel);
            }
            Directory.CreateDirectory(Local(rel));
            local[rel] = new LocalItem { RelPath = rel, FullPath = Local(rel), IsDir = true };
            UpsertFolderEntry(rel, node.Id);
        }

        // Local folders unknown to Drive, shallow first.
        foreach (var item in local.Values.Where(i => i.IsDir).OrderBy(i => Depth(i.RelPath)).ToList())
        {
            ct.ThrowIfCancellationRequested();
            var rel = item.RelPath;
            if (!local.ContainsKey(rel) || remote.ContainsKey(rel)) continue;
            _state.Entries.TryGetValue(rel, out var s);

            if (s is { Kind: EntryKind.Folder })
            {
                if (!LocalTreeHasChanges(rel, local))
                {
                    await Try(rel, stats, () =>
                    {
                        var where = RecycleBin.Delete(item.FullPath);
                        RemoveTree(local, rel);
                        _state.RemoveUnder(rel);
                        stats.Recycled++;
                        _log.Info($"[{_pair.Name}] moved folder to {where} (deleted on Drive): {rel}");
                        return Task.CompletedTask;
                    });
                    continue;
                }
                _log.Warn($"[{_pair.Name}] folder '{rel}' was deleted on Drive but changed locally; re-creating it on Drive");
                _state.RemoveUnder(rel);
            }
            var parentRel = ParentOf(rel);
            var parentId = parentRel.Length == 0 ? _index!.RootId : remote.GetValueOrDefault(parentRel)?.Id;
            if (parentId == null) continue; // parent could not be created; reported there
            await Try(rel, stats, async () =>
            {
                var created = await _drive.CreateFolderAsync(NameOf(rel), parentId, ct);
                var node = RemoteNode.From(created, parentId);
                node.RelPath = rel;
                _index!.Add(node);
                remote[rel] = node;
                UpsertFolderEntry(rel, node.Id);
                _log.Info($"[{_pair.Name}] created folder on Drive: {rel}");
            });
        }
    }

    // ------------------------------------------------------------------ files

    private async Task SyncFilesAsync(Dictionary<string, RemoteNode> remote, Dictionary<string, LocalItem> local, CycleStats stats, CancellationToken ct)
    {
        // Local paths that are derived from Drive (exports, links) and must not be treated as new local files.
        var derived = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var node in remote.Values.Where(n => !n.IsFolder).OrderBy(n => n.RelPath, StringComparer.OrdinalIgnoreCase).ToList())
        {
            ct.ThrowIfCancellationRequested();
            var rel = node.RelPath;
            var parentRel = ParentOf(rel);
            if (parentRel.Length > 0 && !(local.GetValueOrDefault(parentRel)?.IsDir ?? false)) continue; // parent unresolved

            var kind = Classify(node, local);
            if (kind == EntryKind.File) await SyncFileAsync(node, remote, local, derived, stats, ct);
            else if (kind != EntryKind.Folder) await SyncDerivedAsync(node, kind, remote, local, derived, stats, ct);
            // Skip: nothing local, but a stale derived entry is cleaned up below.
        }

        // Derived entries whose Drive item is gone or is now represented differently: drop the local copy.
        foreach (var s in _state.Entries.Values.Where(e => e.Kind is EntryKind.Export or EntryKind.Link).ToList())
        {
            var stillDerived = remote.TryGetValue(s.Path, out var node) && Classify(node, local) == s.Kind
                               && string.Equals(DerivedName(node, s.Kind), s.LocalName, StringComparison.OrdinalIgnoreCase);
            if (stillDerived) { if (s.LocalName != null) derived.Add(s.LocalName); continue; }
            if (s.LocalName != null)
            {
                derived.Add(s.LocalName);
                if (local.TryGetValue(s.LocalName, out var l) && !l.IsDir)
                {
                    var where = RecycleBin.Delete(l.FullPath);
                    local.Remove(s.LocalName);
                    _log.Info($"[{_pair.Name}] removed {s.Kind.ToString().ToLowerInvariant()} ({where}): {s.LocalName}");
                }
            }
            _state.Entries.Remove(s.Path);
        }

        // Local files Drive does not have.
        foreach (var item in local.Values.Where(i => !i.IsDir).OrderBy(i => i.RelPath, StringComparer.OrdinalIgnoreCase).ToList())
        {
            ct.ThrowIfCancellationRequested();
            var rel = item.RelPath;
            if (!local.ContainsKey(rel) || derived.Contains(rel) || remote.ContainsKey(rel)) continue;
            var parentRel = ParentOf(rel);
            var parentId = parentRel.Length == 0 ? _index!.RootId : remote.GetValueOrDefault(parentRel)?.Id;
            if (parentId == null) continue;
            _state.Entries.TryGetValue(rel, out var s);

            if (s is { Kind: EntryKind.File })
            {
                if (LocalUnchanged(item, s))
                {
                    await Try(rel, stats, () =>
                    {
                        var where = RecycleBin.Delete(item.FullPath);
                        local.Remove(rel);
                        _state.Entries.Remove(rel);
                        stats.Recycled++;
                        _log.Info($"[{_pair.Name}] moved to {where} (deleted on Drive): {rel}");
                        return Task.CompletedTask;
                    });
                    continue;
                }
                _log.Warn($"[{_pair.Name}] '{rel}' was deleted on Drive but changed locally; uploading it again");
            }
            await Try(rel, stats, async () =>
            {
                var created = await _drive.UploadNewAsync(item.FullPath, NameOf(rel), parentId, ct);
                var node = RemoteNode.From(created, parentId);
                node.RelPath = rel;
                _index!.Add(node);
                remote[rel] = node;
                RecordFile(rel, node, item.FullPath);
                stats.Uploaded++;
                _log.Info($"[{_pair.Name}] uploaded: {rel} ({item.Size:N0} bytes)");
            });
        }

        // Entries with neither side left.
        foreach (var key in _state.Entries.Keys.Where(k => !remote.ContainsKey(k) && !local.ContainsKey(k)).ToList())
            _state.Entries.Remove(key);
    }

    private async Task SyncFileAsync(RemoteNode node, Dictionary<string, RemoteNode> remote, Dictionary<string, LocalItem> local, HashSet<string> derived, CycleStats stats, CancellationToken ct)
    {
        var rel = node.RelPath;
        var path = Local(rel);
        local.TryGetValue(rel, out var l);
        _state.Entries.TryGetValue(rel, out var s);

        if (l is { IsDir: true })
        {
            WarnOnce(rel, $"'{rel}' is a file on Drive but a folder locally; leaving both alone");
            return;
        }
        if (s != null && s.Kind != EntryKind.File)
        {
            // Was a link/export; a real local copy has appeared (user hydrated it). Drop the derived file and
            // continue with no history, i.e. adopt if identical, otherwise keep both.
            if (s.LocalName != null)
            {
                derived.Add(s.LocalName);
                if (local.TryGetValue(s.LocalName, out var d) && !d.IsDir) { RecycleBin.Delete(d.FullPath); local.Remove(s.LocalName); }
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
                    await DownloadAsync(node, path, stats, s == null ? "new on Drive" : "changed on Drive after local delete", ct);
                }
                else
                {
                    await _drive.TrashAsync(node.Id, ct);
                    _index!.Remove(node.Id);
                    remote.Remove(rel);
                    _state.Entries.Remove(rel);
                    stats.Trashed++;
                    _log.Info($"[{_pair.Name}] trashed on Drive (deleted locally): {rel}");
                }
                return;
            }

            if (s == null)
            {
                // Both sides have it and we have never synced it: content decides.
                if (SameContent(node, path)) { RecordFile(rel, node, path); return; }
                await ConflictAsync(node, rel, path, remote, stats, "exists on both sides with different content", ct);
                return;
            }

            var remoteChanged = !RemoteUnchanged(node, s);
            var localTouched = l.Size != s.LocalSize || l.MtimeTicks != s.LocalMtimeTicks;
            var localChanged = localTouched && !string.Equals(DriveClient.Md5Of(path), s.Md5, StringComparison.OrdinalIgnoreCase);
            if (localTouched && !localChanged)
            {
                s.LocalSize = l.Size; s.LocalMtimeTicks = l.MtimeTicks; // touched, same bytes
            }

            if (remoteChanged && localChanged)
            {
                if (SameContent(node, path)) { RecordFile(rel, node, path); return; }
                await ConflictAsync(node, rel, path, remote, stats, "changed on both sides", ct);
            }
            else if (remoteChanged)
            {
                await DownloadAsync(node, path, stats, "changed on Drive", ct);
            }
            else if (localChanged)
            {
                var updated = await _drive.UploadUpdateAsync(path, node.Id, ct);
                var fresh = RemoteNode.From(updated, node.ParentId);
                fresh.RelPath = rel;
                _index!.Add(fresh);
                remote[rel] = fresh;
                RecordFile(rel, fresh, path);
                stats.Uploaded++;
                _log.Info($"[{_pair.Name}] uploaded change: {rel} ({l.Size:N0} bytes)");
            }
        });
    }

    private async Task DownloadAsync(RemoteNode node, string path, CycleStats stats, string why, CancellationToken ct)
    {
        var file = new Google.Apis.Drive.v3.Data.File { Id = node.Id, Name = node.Name, ModifiedTimeDateTimeOffset = node.ModifiedUtc };
        await _drive.DownloadAsync(file, path, ct);
        RecordFile(node.RelPath, node, path);
        stats.Downloaded++;
        _log.Info($"[{_pair.Name}] downloaded ({why}): {node.RelPath} ({node.Size:N0} bytes)");
    }

    /// <summary>Keeps both: the local version is renamed and uploaded as its own file, Drive's version takes the original name.</summary>
    private async Task ConflictAsync(RemoteNode node, string rel, string path, Dictionary<string, RemoteNode> remote, CycleStats stats, string why, CancellationToken ct)
    {
        var name = NameOf(rel);
        var ext = Path.GetExtension(name);
        var stem = name[..^ext.Length];
        var conflictName = $"{stem} (conflict {Environment.MachineName} {DateTime.Now:yyyy-MM-dd HHmm}){ext}";
        var conflictRel = ParentOf(rel).Length == 0 ? conflictName : ParentOf(rel) + "/" + conflictName;
        var conflictPath = Local(conflictRel);
        File.Move(path, conflictPath, overwrite: false);
        _log.Warn($"[{_pair.Name}] conflict ({why}): '{rel}' kept as '{conflictName}'");
        stats.Conflicts++;

        await DownloadAsync(node, path, stats, "conflict, Drive version", ct);

        var created = await _drive.UploadNewAsync(conflictPath, conflictName, node.ParentId, ct);
        var cnode = RemoteNode.From(created, node.ParentId);
        cnode.RelPath = conflictRel;
        _index!.Add(cnode);
        remote[conflictRel] = cnode;
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
            WarnOnce(derivedRel, $"cannot write {kind.ToString().ToLowerInvariant()} '{derivedRel}': a real file by that name exists on Drive");
            return;
        }
        var path = Local(derivedRel);
        _state.Entries.TryGetValue(rel, out var s);
        local.TryGetValue(derivedRel, out var l);

        if (s != null && s.Kind == kind && s.DriveId == node.Id && s.LocalName == derivedRel && l is { IsDir: false })
        {
            var upToDate = s.DriveModifiedUtc == node.ModifiedUtc;
            var editedLocally = l.Size != s.LocalSize || l.MtimeTicks != s.LocalMtimeTicks;
            if (editedLocally && kind == EntryKind.Export)
            {
                _log.Warn($"[{_pair.Name}] '{derivedRel}' was edited locally; exports are one-way and the edit will not reach Drive{(upToDate ? "" : " (and Drive has a newer version)")}");
                s.LocalSize = l.Size; s.LocalMtimeTicks = l.MtimeTicks; // warn once per edit
                if (upToDate) return;
            }
            else if (upToDate) return;
        }
        if (s?.LocalName != null && !string.Equals(s.LocalName, derivedRel, StringComparison.OrdinalIgnoreCase)
            && local.TryGetValue(s.LocalName, out var old) && !old.IsDir)
        {
            RecycleBin.Delete(old.FullPath); // representation changed, e.g. export -> link
            local.Remove(s.LocalName);
        }

        await Try(rel, stats, async () =>
        {
            if (kind == EntryKind.Link)
            {
                WriteLink(path, node);
                stats.Linked++;
                _log.Debug($"[{_pair.Name}] link: {derivedRel}");
            }
            else
            {
                var mime = ExportMime(node.MimeType)!;
                await _drive.ExportAsync(node.Id, node.Name, mime, path, node.ModifiedUtc, ct);
                File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
                stats.Exported++;
                _log.Info($"[{_pair.Name}] exported: {derivedRel}");
            }
            var info = new FileInfo(path);
            _state.Entries[rel] = new StateEntry
            {
                Path = rel, Kind = kind, DriveId = node.Id, Md5 = node.Md5, Size = node.Size, DriveModifiedUtc = node.ModifiedUtc,
                LocalName = derivedRel, LocalSize = info.Length, LocalMtimeTicks = info.LastWriteTimeUtc.Ticks,
            };
            local[derivedRel] = new LocalItem { RelPath = derivedRel, FullPath = path, Size = info.Length, MtimeTicks = info.LastWriteTimeUtc.Ticks };
        });
    }

    private static void WriteLink(string path, RemoteNode node)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[InternetShortcut]");
        sb.AppendLine($"URL=https://drive.google.com/open?id={node.Id}");
        if (File.Exists(path)) File.SetAttributes(path, FileAttributes.Normal);
        File.WriteAllText(path, sb.ToString(), Encoding.ASCII);
        File.SetLastWriteTimeUtc(path, node.ModifiedUtc);
    }

    private EntryKind Classify(RemoteNode node, Dictionary<string, LocalItem> local)
    {
        if (node.IsFolder) return EntryKind.Folder;
        if (node.IsGoogleNative)
            return _pair.GoogleDocs switch
            {
                GoogleDocsMode.Skip => EntryKind.Folder, // caller treats Folder from a file as "skip"
                GoogleDocsMode.Link => EntryKind.Link,
                _ => ExportMime(node.MimeType) != null ? EntryKind.Export : EntryKind.Link,
            };
        var cloudOnly = (_pair.CloudOnlyMinMB > 0 && node.Size >= (long)_pair.CloudOnlyMinMB * 1024 * 1024)
                        || _pair.CloudOnlyFolders.Any(f => IsUnder(node.RelPath, f));
        // A cloud-only file that the user has a real local copy of is simply a synced file.
        if (cloudOnly && !(local.TryGetValue(node.RelPath, out var l) && !l.IsDir)) return EntryKind.Link;
        return EntryKind.File;
    }

    private static string? DerivedName(RemoteNode node, EntryKind kind) => kind switch
    {
        EntryKind.Link => node.RelPath + ".url",
        EntryKind.Export => node.RelPath + ExportExt(node.MimeType),
        _ => null,
    };

    private static string? ExportMime(string mime) => mime switch
    {
        "application/vnd.google-apps.document" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "application/vnd.google-apps.spreadsheet" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        "application/vnd.google-apps.presentation" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        "application/vnd.google-apps.drawing" => "image/png",
        _ => null,
    };

    private static string? ExportExt(string mime) => mime switch
    {
        "application/vnd.google-apps.document" => ".docx",
        "application/vnd.google-apps.spreadsheet" => ".xlsx",
        "application/vnd.google-apps.presentation" => ".pptx",
        "application/vnd.google-apps.drawing" => ".png",
        _ => null,
    };

    // ---------------------------------------------------------------- helpers

    private void RecordFile(string rel, RemoteNode node, string path)
    {
        var info = new FileInfo(path);
        _state.Entries[rel] = new StateEntry
        {
            Path = rel, Kind = EntryKind.File, DriveId = node.Id, Md5 = node.Md5, Size = node.Size, DriveModifiedUtc = node.ModifiedUtc,
            LocalSize = info.Length, LocalMtimeTicks = info.LastWriteTimeUtc.Ticks,
        };
    }

    private void UpsertFolderEntry(string rel, string id)
    {
        if (_state.Entries.TryGetValue(rel, out var s) && s.Kind == EntryKind.Folder && s.DriveId == id) return;
        _state.Entries[rel] = new StateEntry { Path = rel, Kind = EntryKind.Folder, DriveId = id };
    }

    private static bool RemoteUnchanged(RemoteNode node, StateEntry s) =>
        node.Md5 != null && s.Md5 != null
            ? string.Equals(node.Md5, s.Md5, StringComparison.OrdinalIgnoreCase)
            : node.ModifiedUtc == s.DriveModifiedUtc && node.Size == s.Size;

    private static bool LocalUnchanged(LocalItem l, StateEntry s) =>
        (l.Size == s.LocalSize && l.MtimeTicks == s.LocalMtimeTicks)
        || (l.Size == s.Size && string.Equals(DriveClient.Md5Of(l.FullPath), s.Md5, StringComparison.OrdinalIgnoreCase));

    private static bool SameContent(RemoteNode node, string path) =>
        node.Md5 != null && new FileInfo(path).Length == node.Size
        && string.Equals(DriveClient.Md5Of(path), node.Md5, StringComparison.OrdinalIgnoreCase);

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
        foreach (var l in local.Values.Where(l => !l.IsDir && l.RelPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            if (_state.Entries.TryGetValue(l.RelPath, out var s))
            {
                if (s.Kind == EntryKind.File && !LocalUnchanged(l, s)) return true;
                continue;
            }
            // Derived files are keyed by their Drive path, not the local name.
            if (_state.Entries.Values.Any(e => e.LocalName != null && e.LocalName.Equals(l.RelPath, StringComparison.OrdinalIgnoreCase))) continue;
            return true;
        }
        return false;
    }

    private async Task Try(string rel, CycleStats stats, Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            stats.Errors++;
            _log.Error($"[{_pair.Name}] {rel}: {ex.Message}");
        }
    }

    private void WarnOnce(string key, string message)
    {
        if (_warnedOnce.Add(key)) _log.Warn($"[{_pair.Name}] {message}");
    }

    private string Local(string rel) => Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));

    private static string ParentOf(string rel) { var i = rel.LastIndexOf('/'); return i < 0 ? "" : rel[..i]; }
    private static string NameOf(string rel) { var i = rel.LastIndexOf('/'); return i < 0 ? rel : rel[(i + 1)..]; }
    private static int Depth(string rel) => rel.Count(c => c == '/');
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
