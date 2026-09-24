namespace Unisono;

/// <summary>
/// Files on demand (<see cref="SyncPair.OnDemand"/>). The local folder is a Cloud Files sync root and the usual three-way
/// compare stays as it is; what changes is how remote content lands locally:
///
///   new or changed remotely     an online-only placeholder (no download), unless the file is on this computer already
///                               or it or its folder is pinned ("Always keep on this device"): then a real download
///   pinned but online-only      downloaded (the user chose "Always keep on this device")
///   unpinned but local          made online-only again, if nothing changed locally ("Free up space")
///   written locally             uploaded as before, then marked in sync
///   ordinary files and folders  (the folder was synced in full before, or the user created them) become placeholders
///
/// An online-only placeholder has no local content, so it can never hold a local change: it is never hashed, copied
/// to the version store or treated as a conflict, and reading it would only download it.
/// </summary>
public sealed partial class SyncEngine
{
    private bool OnDemand => _pair.OnDemand;
    private bool _registered;
    /// <summary>Paths already downloaded or described in this cycle; the pin pass leaves them alone.</summary>
    private readonly HashSet<string> _doneThisCycle = new(StringComparer.OrdinalIgnoreCase);

    private void EnsureSyncRoot()
    {
        if (!OnDemand || DryRun || _registered) return;
        if (CloudFiles.WhyNot(_root, _pair) is { } why) throw new InvalidOperationException(why);
        if (!CloudFiles.IsRegistered(_pair)) CloudFiles.Register(_pair);
        _registered = true;
    }

    private static bool ParentPinned(string path)
    {
        try { return CloudFiles.IsPinned(File.GetAttributes(Path.GetDirectoryName(path)!)); }
        catch { return false; }
    }

    /// <summary>Should remote content for this path be downloaded (true) or only described by a placeholder (false)?</summary>
    private bool KeepLocal(string path)
    {
        if (!OnDemand) return true;
        if (File.Exists(path))
        {
            var a = File.GetAttributes(path);
            if (CloudFiles.IsPinned(a)) return true;
            if (CloudFiles.IsUnpinned(a) || CloudFiles.IsDehydrated(a)) return false;
            return true; // on this computer already: keep it up to date
        }
        return ParentPinned(path);
    }

    /// <summary>Creates or updates an online-only placeholder for a remote file.</summary>
    private void Placeholder(RemoteNode node, string path, CycleStats stats, string why)
    {
        stats.Online++;
        _doneThisCycle.Add(node.RelPath);
        if (Planned("show online-only", node.RelPath, $"{why}, {node.Size:N0} bytes")) return;
        var modified = node.ModifiedUtc == default ? DateTime.UtcNow : node.ModifiedUtc;
        if (File.Exists(path)) CloudFiles.UpdateOnline(path, node.Size, modified, node.Id);
        else CloudFiles.CreatePlaceholder(path, isDir: false, node.Size, modified, node.Id);
        RecordFile(node.RelPath, node, path);
        _log.Debug($"[{_pair.Name}] online-only ({why}): {node.RelPath}");
    }

    /// <summary>After Unisono wrote a real file into the sync root (download, restore): make it an in-sync placeholder.</summary>
    private void Adopt(string path, string identity, bool pin)
    {
        if (!OnDemand || DryRun) return;
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0) CloudFiles.Convert(path, identity);
            else CloudFiles.MarkInSync(path);
            if (pin) CloudFiles.SetPinState(path, 1);
        }
        catch (Exception ex) { _log.Debug($"[{_pair.Name}] {Path.GetFileName(path)} stays pending for now: {ex.Message}"); }
    }

    /// <summary>After an upload of a local file: it now matches the remote, which Explorer shows as in sync.</summary>
    private void Uploaded(string path, string identity) => Adopt(path, identity, pin: false);

    /// <summary>Deletes a local item for a remote deletion. Online-only content goes directly (there is nothing to keep).</summary>
    private string RemoveLocal(string fullPath)
    {
        if (OnDemand && File.Exists(fullPath) && CloudFiles.IsDehydrated(File.GetAttributes(fullPath)))
        {
            File.Delete(fullPath);
            return "nowhere (it was online-only)";
        }
        if (OnDemand && Directory.Exists(fullPath))
        {
            // The Recycle Bin lies outside the sync root, and online-only placeholders cannot be moved out of it.
            foreach (var f in Directory.EnumerateFiles(fullPath, "*", SearchOption.AllDirectories))
                if (CloudFiles.IsDehydrated(File.GetAttributes(f))) File.Delete(f);
        }
        return RecycleBin.Delete(fullPath);
    }

    /// <summary>
    /// Pin states set in Explorer, and ordinary items that should be placeholders. Runs after the file pass, on what the
    /// scan saw at the start of the cycle; anything created in this cycle is handled on the next one.
    /// </summary>
    private async Task ApplyOnDemandAsync(Dictionary<string, RemoteNode> remote, Dictionary<string, LocalItem> local, CycleStats stats, CancellationToken ct)
    {
        if (!OnDemand || DryRun) return;
        foreach (var l in local.Values.OrderBy(i => PathUtil.Depth(i.RelPath)).ToList())
        {
            ct.ThrowIfCancellationRequested();
            if (_doneThisCycle.Contains(l.RelPath) || !_state.Entries.TryGetValue(l.RelPath, out var s)) continue; // not synced (yet)
            var path = l.FullPath;
            if (!File.Exists(path) && !Directory.Exists(path)) continue;   // gone during the cycle
            try
            {
                if (l.IsDir)
                {
                    if (s.Kind == EntryKind.Folder && !l.IsPlaceholder) CloudFiles.Convert(path, s.DriveId);
                    continue;
                }
                if (s.Kind != EntryKind.File)
                {
                    // Exports and links are always local (they are generated here); shown as in sync and kept.
                    if (!l.IsPlaceholder) { CloudFiles.Convert(path, s.DriveId); CloudFiles.SetPinState(path, 1); }
                    continue;
                }
                if (!remote.TryGetValue(l.RelPath, out var node) || node.IsFolder) continue;
                var pinned = l.Pinned || (!l.Unpinned && ParentPinned(path));
                if (l.Dehydrated && pinned && Down)
                {
                    await Try(l.RelPath, stats, () => DownloadAsync(node, path, stats, "kept on this device", ct, keep: true));
                }
                else if (!l.Dehydrated && l.Unpinned && LocalUnchanged(l, s))
                {
                    if (!l.IsPlaceholder) CloudFiles.Convert(path, s.DriveId);
                    else CloudFiles.MarkInSync(path);
                    CloudFiles.Dehydrate(path);
                    stats.Online++;
                    _log.Info($"[{_pair.Name}] freed up space: {l.RelPath} ({l.Size:N0} bytes, online-only now)");
                }
                else if (!l.IsPlaceholder && LocalUnchanged(l, s))
                {
                    CloudFiles.Convert(path, s.DriveId);
                }
            }
            catch (Exception ex) { _log.Debug($"[{_pair.Name}] {l.RelPath}: {ex.Message}"); } // open elsewhere etc.: next cycle
        }
    }
}
