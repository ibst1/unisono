namespace Unisono;

/// <summary>
/// A Box folder behind <see cref="IRemoteStore"/>. Box has stable ids, a SHA-1 per file and an event stream: the tree
/// is listed in full at the start and every <see cref="SyncPair.FullRescanMinutes"/>, and otherwise only when the
/// stream shows a change in the folder. In between, the cached tree follows this store's own changes at once (the
/// stream reports them a little later), so a file just uploaded never looks deleted remotely. Deletes go to Box's
/// trash; a file's earlier versions serve as the base of a merge.
/// </summary>
public sealed class BoxStore : IRemoteStore
{
    private readonly BoxClient _box;
    private readonly SyncPair _pair;
    private string? _rootId;
    private string? _position;
    private Dictionary<string, RemoteNode>? _tree; // rel path -> node; guarded by _gate
    private DateTime _listedUtc;
    private readonly object _gate = new();
    private readonly HashSet<string> _told = new(StringComparer.Ordinal); // name clashes already reported

    public BoxStore(BoxClient box, SyncPair pair)
    {
        _box = box;
        _pair = pair;
    }

    public string Kind => "Box";

    /// <summary>The root folder's id is kept: a pair never silently follows its path to another folder.</summary>
    public void LoadState(SyncState state) => _rootId = state.RootId;
    public void SaveState(SyncState state) { if (_rootId != null) state.RootId = _rootId; }

    public int ParallelUploads => 4;

    public async Task<Dictionary<string, RemoteNode>> GetTreeAsync(Log log, CancellationToken ct)
    {
        if (_tree == null)
        {
            var id = await _box.ResolveFolderAsync(_pair.RemotePath, ct);
            if (_rootId != null && _rootId != id)
                throw new InvalidOperationException($"State file {SyncState.FilePathFor(_pair)} belongs to a different Box folder (that path now is another folder). Delete it to start over (existing local files will then be matched by content).");
            _rootId = id;
        }
        var full = _tree == null || _pair.FullRescanMinutes > 0 && DateTime.UtcNow - _listedUtc > TimeSpan.FromMinutes(_pair.FullRescanMinutes);
        if (!full)
        {
            var (changed, position) = await _box.ChangedSinceAsync(_position!, _rootId!, ct);
            _position = position;
            full = changed;
        }
        if (full)
        {
            // The position first: what changes while listing shows up next time.
            var position = await _box.StreamPositionAsync(ct);
            var started = DateTime.UtcNow;
            var tree = await ListAsync(log, ct);
            lock (_gate) { _tree = tree; }
            _position = position;
            _listedUtc = DateTime.UtcNow;
            log.Debug($"[{_pair.Name}] listed {tree.Count} items on Box in {(DateTime.UtcNow - started).TotalSeconds:N1} s");
        }
        lock (_gate) return new Dictionary<string, RemoteNode>(_tree!, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The whole tree below the root, a few folders at a time.</summary>
    private async Task<Dictionary<string, RemoteNode>> ListAsync(Log log, CancellationToken ct)
    {
        var tree = new Dictionary<string, RemoteNode>(StringComparer.OrdinalIgnoreCase);
        var level = new List<(string Id, string Rel)> { (_rootId!, "") };
        using var gate = new SemaphoreSlim(4);
        while (level.Count > 0)
        {
            var lists = await Task.WhenAll(level.Select(async f =>
            {
                await gate.WaitAsync(ct);
                try { return (f.Rel, Items: await _box.ListAsync(f.Id, ct)); }
                finally { gate.Release(); }
            }));
            var next = new List<(string, string)>();
            foreach (var (rel, items) in lists)
            {
                var names = new HashSet<string>(items.Select(i => i.Name), StringComparer.OrdinalIgnoreCase);
                foreach (var i in items.OrderBy(i => i.Name, StringComparer.Ordinal).ThenBy(i => i.Id, StringComparer.Ordinal))
                {
                    var childRel = PathUtil.Join(rel, i.Name);
                    if (tree.TryGetValue(childRel, out var first))
                    {
                        // A sibling that differs only in case, which Windows cannot hold: one of identical files is
                        // enough here, another one gets a free name (renaming it here renames it on Box).
                        if (!i.IsFolder && !first.IsFolder && i.Sha1 != null && i.Sha1 == first.Sha1)
                        {
                            if (_told.Add(i.Id)) log.Info($"'{childRel}' is on Box twice, differing only in case, with the same content: synced once");
                            continue;
                        }
                        var free = PathUtil.FreeName(i.Name, i.IsFolder, n => names.Contains(n));
                        names.Add(free);
                        if (_told.Add(i.Id)) log.Info($"'{childRel}' differs from a sibling on Box only in case, which Windows cannot hold: synced here as '{free}'");
                        childRel = PathUtil.Join(rel, free);
                    }
                    tree[childRel] = ToNode(i, childRel);
                    if (i.IsFolder) next.Add((i.Id, childRel));
                }
            }
            level = next;
        }
        return tree;
    }

    private static RemoteNode ToNode(BoxItem i, string rel) => new()
    {
        Id = i.Id, Name = i.Name, ParentId = i.ParentId, RelPath = rel,
        MimeType = i.IsFolder ? DriveClient.FolderMime : "application/octet-stream",
        Size = i.IsFolder ? 0 : i.Size, Sha1 = i.Sha1, ModifiedUtc = i.ModifiedUtc,
    };

    // The cached tree follows this store's own changes.
    private RemoteNode Remember(RemoteNode n) { lock (_gate) { if (_tree != null) _tree[n.RelPath] = n; } return n; }

    private void ForgetSubtree(string rel)
    {
        lock (_gate)
        {
            if (_tree == null) return;
            foreach (var k in _tree.Keys.Where(k => k.Equals(rel, StringComparison.OrdinalIgnoreCase) || k.StartsWith(rel + "/", StringComparison.OrdinalIgnoreCase)).ToList())
                _tree.Remove(k);
        }
    }

    public async Task<RemoteNode?> RefreshAsync(RemoteNode node, CancellationToken ct)
    {
        var item = await _box.GetAsync(node.Id, node.IsFolder, ct);
        return item == null ? null : ToNode(item, node.RelPath);
    }

    public Task DownloadAsync(RemoteNode node, string localPath, CancellationToken ct) =>
        _box.DownloadAsync(node.Id, localPath, node.ModifiedUtc, ct);

    /// <summary>The earlier version with that content (Box keeps SHA-1s, the merge knows the MD5): small text files only, a few versions back.</summary>
    public async Task<byte[]?> EarlierContentAsync(RemoteNode node, string md5, CancellationToken ct)
    {
        var dir = Path.Combine(AppPaths.DataDir, "merge");
        Directory.CreateDirectory(dir);
        foreach (var (versionId, _) in (await _box.VersionsAsync(node.Id, ct)).Take(10))
        {
            var tmp = Path.Combine(dir, Guid.NewGuid().ToString("N"));
            try
            {
                await _box.DownloadAsync(node.Id, tmp, DateTime.UtcNow, ct, versionId);
                var bytes = await File.ReadAllBytesAsync(tmp, ct);
                if (string.Equals(TextMerge.Md5Hex(bytes), md5, StringComparison.OrdinalIgnoreCase)) return bytes;
            }
            finally { try { File.Delete(tmp); } catch { } }
        }
        return null;
    }

    public async Task<RemoteNode> UploadNewAsync(string localPath, string relPath, RemoteNode? parent, CancellationToken ct) =>
        Remember(ToNode(await _box.UploadAsync(localPath, parent?.Id ?? _rootId!, PathUtil.NameOf(relPath), null, ct), relPath));

    public async Task<RemoteNode> UploadUpdateAsync(string localPath, RemoteNode node, CancellationToken ct) =>
        Remember(ToNode(await _box.UploadAsync(localPath, null, null, node.Id, ct), node.RelPath));

    public async Task<RemoteNode> CreateFolderAsync(string relPath, RemoteNode? parent, CancellationToken ct) =>
        Remember(ToNode(await _box.CreateFolderAsync(parent?.Id ?? _rootId!, PathUtil.NameOf(relPath), ct), relPath));

    public async Task<RemoteNode> MoveAsync(RemoteNode node, string newRelPath, RemoteNode? newParent, CancellationToken ct)
    {
        var moved = ToNode(await _box.MoveAsync(node.Id, node.IsFolder, PathUtil.NameOf(newRelPath), newParent?.Id ?? _rootId!, ct), newRelPath);
        lock (_gate)
        {
            if (_tree != null)
            {
                // A folder takes everything in it along (ids stay the same on Box).
                foreach (var (k, v) in _tree.Where(e => e.Key.StartsWith(node.RelPath + "/", StringComparison.OrdinalIgnoreCase)).ToList())
                {
                    _tree.Remove(k);
                    var rel = newRelPath + k[node.RelPath.Length..];
                    v.RelPath = rel;
                    _tree[rel] = v;
                }
                _tree.Remove(node.RelPath);
                _tree[newRelPath] = moved;
            }
        }
        return moved;
    }

    public async Task DeleteAsync(RemoteNode node, CancellationToken ct)
    {
        await _box.DeleteAsync(node.Id, node.IsFolder, ct);
        ForgetSubtree(node.RelPath);
    }

    public bool HasStableIds => true;
    public string RebaseId(string id, string oldFolderId, string newFolderId) => id;

    public RemoteNode HydrationNode(string rel, string identity, long size) =>
        new() { Id = identity, Name = PathUtil.NameOf(rel), RelPath = rel, Size = size, ModifiedUtc = DateTime.UtcNow };

    // Box has no documents that exist only in an online editor that Unisono would export (Box Notes are files).
    public bool CanLink(RemoteNode node) => false;
    public Task<string> LinkUrlAsync(RemoteNode node, CancellationToken ct) => Task.FromResult($"https://app.box.com/{(node.IsFolder ? "folder" : "file")}/{node.Id}");
    public bool CanImport(RemoteNode node) => false;
    public Task<string?> RevisionAsync(RemoteNode node, CancellationToken ct) => Task.FromResult<string?>(null);
    public Task<string?> WhyNotImportAsync(RemoteNode node, CancellationToken ct) => Task.FromResult<string?>("Box has no native documents");
    public Task<(RemoteNode? Fresh, string? Revision, string? Why)> PatchAsync(RemoteNode node, string localPath, CancellationToken ct) =>
        Task.FromResult<(RemoteNode?, string?, string?)>((null, null, "Box has no native documents"));
    public Task<RemoteNode> ImportAsync(RemoteNode node, string localPath, string mime, CancellationToken ct) => throw new NotSupportedException();
    public Task ExportAsync(RemoteNode node, string exportMime, string localPath, CancellationToken ct) => throw new NotSupportedException();
}
