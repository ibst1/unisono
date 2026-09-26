using System.Collections.Concurrent;

namespace Unisono;

/// <summary>
/// An encrypted pair's remote side: a Cryptomator vault (<see cref="CryptomatorVault"/>) inside the real store (Drive,
/// NAS, folder). The engine sees the plain tree it always sees; this class translates it to and from the vault's layout
/// (directories by id under d/, encrypted names, shortened long names) and encrypts and decrypts file content on the way.
///
/// Ids: a file is its content file's remote id, a directory its ".c9r" (or ".c9s") folder's id. Since directory
/// contents live under their own id rather than inside their parent, moving a directory moves only that small folder:
/// the ids below it do not change, on any backend. Items that are not of this vault are left out.
/// Remote checksums say nothing about the plain content, so the engine goes by size and date, as on a NAS.
/// </summary>
public sealed class CryptomatorStore : IRemoteStore
{
    private readonly IRemoteStore _inner;
    private readonly CryptomatorVault _vault;
    private readonly string _pairName;
    private readonly Log _log;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _folders = new(1, 1);

    /// <summary>One plain item and where it lives in the vault.</summary>
    private sealed class Item
    {
        public string Rel = "";
        public bool IsDir;
        /// <summary>The file's content (.c9r file or contents.c9r), or a directory's dir.c9r.</summary>
        public RemoteNode Content = null!;
        /// <summary>What represents the item in its parent's folder: the .c9r file, the .c9r folder or the .c9s folder.</summary>
        public RemoteNode Container = null!;
        public bool Shortened;
        public string ParentDirId = "";
        public string? DirId;
    }

    private Dictionary<string, RemoteNode> _innerTree = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, Item> _byId = new();
    private Dictionary<string, Item> _byRel = new(StringComparer.OrdinalIgnoreCase);
    // Small vault files (dir.c9r ids, name.c9s names) by remote id + date + size: read once.
    private readonly ConcurrentDictionary<string, string> _smallFiles = new();
    private SyncState? _state;
    private int _foreignReported;

    public CryptomatorStore(IRemoteStore inner, CryptomatorVault vault, string pairName, Log log)
    {
        _inner = inner;
        _vault = vault;
        _pairName = pairName;
        _log = log;
    }

    public string Kind => _inner.Kind + " (encrypted)";
    public int ParallelUploads => _inner.ParallelUploads;
    // Directory contents live under their id, so an item's id changes only when the item itself is renamed or moved.
    public bool HasStableIds => true;
    public string RebaseId(string id, string oldFolderId, string newFolderId) => id;

    public void LoadState(SyncState state)
    {
        _inner.LoadState(state);
        _state = state;
        foreach (var (k, v) in state.VaultFiles) _smallFiles[k] = v;
    }

    public void SaveState(SyncState state)
    {
        _inner.SaveState(state);
        state.VaultFiles = new Dictionary<string, string>(_smallFiles);
    }

    // ------------------------------------------------------------------ tree

    public async Task<Dictionary<string, RemoteNode>> GetTreeAsync(Log log, CancellationToken ct)
    {
        var inner = await _inner.GetTreeAsync(log, ct);
        if (!inner.ContainsKey(CryptomatorVault.VaultFile)) throw new InvalidOperationException($"{CryptomatorVault.VaultFile} is missing on the other side: this is not the encrypted folder any more");
        var children = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var rel in inner.Keys)
        {
            var parent = PathUtil.ParentOf(rel);
            if (!children.TryGetValue(parent, out var list)) children[parent] = list = new();
            list.Add(rel);
        }

        var byId = new Dictionary<string, Item>();
        var byRel = new Dictionary<string, Item>(StringComparer.OrdinalIgnoreCase);
        var plain = new Dictionary<string, RemoteNode>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>();
        var foreign = 0;
        var queue = new Queue<(string DirId, string Rel, string ParentNodeId)>();
        queue.Enqueue(("", "", ""));
        while (queue.Count > 0)
        {
            var (dirId, rel, parentNodeId) = queue.Dequeue();
            if (!seen.Add(dirId) || !children.TryGetValue(_vault.DirPath(dirId), out var entries)) continue;
            foreach (var entryRel in entries)
            {
                ct.ThrowIfCancellationRequested();
                var name = PathUtil.NameOf(entryRel);
                var node = inner[entryRel];
                var item = new Item { ParentDirId = dirId, Container = node };
                string? encrypted = null;
                if (name.EndsWith(".c9r", StringComparison.Ordinal) && name != "dirid.c9r")
                {
                    encrypted = name;
                    if (!node.IsFolder) item.Content = node;
                    else if (inner.TryGetValue(entryRel + "/dir.c9r", out var dirFile)) { item.IsDir = true; item.Content = dirFile; }
                    else encrypted = null; // a symlink or something else Unisono does not sync
                }
                else if (name.EndsWith(".c9s", StringComparison.Ordinal) && node.IsFolder && inner.TryGetValue(entryRel + "/name.c9s", out var nameFile))
                {
                    item.Shortened = true;
                    if (inner.TryGetValue(entryRel + "/contents.c9r", out var contents)) item.Content = contents;
                    else if (inner.TryGetValue(entryRel + "/dir.c9r", out var dirFile)) { item.IsDir = true; item.Content = dirFile; }
                    if (item.Content != null) encrypted = await SmallFileAsync(nameFile, ct);
                }
                if (encrypted == null) continue;
                var plainName = _vault.DecryptName(encrypted, dirId);
                if (plainName == null) { foreign++; continue; }
                item.Rel = PathUtil.Join(rel, plainName);
                if (item.IsDir) item.DirId = await SmallFileAsync(item.Content, ct);
                byId[node.Id] = item;
                byRel[item.Rel] = item;
                plain[item.Rel] = Wrap(item, parentNodeId);
                if (item.IsDir) queue.Enqueue((item.DirId!, item.Rel, node.Id));
            }
        }
        if (foreign > 0 && Interlocked.Exchange(ref _foreignReported, 1) == 0)
            log.Warn($"[{_pairName}] {foreign} item(s) in the vault could not be decrypted (another vault's or damaged) and are left alone");
        lock (_gate) { _innerTree = inner; _byId = byId; _byRel = byRel; }
        return plain;
    }

    private static RemoteNode Wrap(Item item, string parentNodeId) => new()
    {
        Id = item.Container.Id, Name = PathUtil.NameOf(item.Rel), ParentId = parentNodeId, RelPath = item.Rel,
        MimeType = item.IsDir ? DriveClient.FolderMime : "application/octet-stream",
        Size = item.IsDir ? 0 : CryptomatorVault.PlainSize(item.Content.Size),
        Md5 = null, // the ciphertext's checksum says nothing about the plain content
        ModifiedUtc = item.IsDir ? item.Container.ModifiedUtc : item.Content.ModifiedUtc,
    };

    private RemoteNode Wrap(Item item)
    {
        string parentId;
        lock (_gate) parentId = _byRel.TryGetValue(PathUtil.ParentOf(item.Rel), out var p) ? p.Container.Id : "";
        return Wrap(item, parentId);
    }

    /// <summary>A small vault file's text (a directory id, a long name), read once per version.</summary>
    private async Task<string> SmallFileAsync(RemoteNode node, CancellationToken ct)
    {
        var key = $"{node.Id}|{node.ModifiedUtc.Ticks}|{node.Size}";
        if (_smallFiles.TryGetValue(key, out var text)) return text;
        var tmp = TempFile();
        try
        {
            await _inner.DownloadAsync(node, tmp, ct);
            text = (await File.ReadAllTextAsync(tmp, ct)).Trim();
        }
        finally { TryDelete(tmp); }
        _smallFiles[key] = text;
        return text;
    }

    private Item ItemOf(RemoteNode node)
    {
        lock (_gate)
            if (_byId.TryGetValue(node.Id, out var item) || (node.RelPath.Length > 0 && _byRel.TryGetValue(node.RelPath, out item))) return item;
        throw new InvalidOperationException($"'{node.RelPath}' is not known in the vault (yet)");
    }

    /// <summary>The directory id and its physical folder for a (plain) parent; null = the vault's root directory.</summary>
    private async Task<(string DirId, string Path, RemoteNode Node)> DirOfAsync(RemoteNode? parent, CancellationToken ct)
    {
        var dirId = parent == null ? "" : ItemOf(parent).DirId ?? throw new InvalidOperationException($"'{parent.RelPath}' is not a folder");
        var path = _vault.DirPath(dirId);
        return (dirId, path, await EnsureInnerFolderAsync(path, ct));
    }

    private async Task<RemoteNode> EnsureInnerFolderAsync(string rel, CancellationToken ct)
    {
        lock (_gate) if (_innerTree.TryGetValue(rel, out var known)) return known;
        await _folders.WaitAsync(ct);
        try { return await EnsureCoreAsync(rel); }
        finally { _folders.Release(); }

        async Task<RemoteNode> EnsureCoreAsync(string r)
        {
            lock (_gate) if (_innerTree.TryGetValue(r, out var n)) return n;
            var parent = PathUtil.ParentOf(r);
            var parentNode = parent.Length == 0 ? null : await EnsureCoreAsync(parent);
            var created = await _inner.CreateFolderAsync(r, parentNode, ct);
            lock (_gate) _innerTree[r] = created;
            return created;
        }
    }

    private async Task<RemoteNode> UploadTextAsync(string rel, string text, RemoteNode? parent, CancellationToken ct)
    {
        var tmp = TempFile();
        try
        {
            await File.WriteAllTextAsync(tmp, text, new System.Text.UTF8Encoding(false), ct);
            var node = await _inner.UploadNewAsync(tmp, rel, parent, ct);
            _smallFiles[$"{node.Id}|{node.ModifiedUtc.Ticks}|{node.Size}"] = text;
            lock (_gate) _innerTree[rel] = node;
            return node;
        }
        finally { TryDelete(tmp); }
    }

    /// <summary>
    /// The container for a new name in a directory: a short name is used as is; a long one gets a ".c9s" folder with
    /// name.c9s. Returns the path where the item itself (file content, or directory marker) goes.
    /// </summary>
    private async Task<(string ItemPath, RemoteNode? ItemParent, RemoteNode? C9s)> PlaceAsync(string encrypted, string dirPath, RemoteNode dirNode, CancellationToken ct)
    {
        if (encrypted.Length <= CryptomatorVault.ShorteningThreshold) return (dirPath + "/" + encrypted, dirNode, null);
        var c9sPath = dirPath + "/" + CryptomatorVault.ShortName(encrypted);
        var c9s = await _inner.CreateFolderAsync(c9sPath, dirNode, ct);
        lock (_gate) _innerTree[c9sPath] = c9s;
        await UploadTextAsync(c9sPath + "/name.c9s", encrypted, c9s, ct);
        return (c9sPath, c9s, c9s);
    }

    private void Register(Item item)
    {
        lock (_gate)
        {
            _byId[item.Container.Id] = item;
            _byRel[item.Rel] = item;
        }
    }

    private void Forget(Item item)
    {
        lock (_gate)
        {
            _byId.Remove(item.Container.Id);
            _byRel.Remove(item.Rel);
        }
    }

    // ------------------------------------------------------------- transfers

    // Through temporary ciphertext files in the data folder (never inside a synced folder).
    private static string TempFile()
    {
        var dir = Path.Combine(AppPaths.DataDir, "crypt-tmp");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, Guid.NewGuid().ToString("N"));
    }

    private async Task<string> EncryptAsync(string localPath, CancellationToken ct)
    {
        var enc = TempFile();
        await Task.Run(() => _vault.EncryptFile(localPath, enc), ct);
        File.SetLastWriteTimeUtc(enc, File.GetLastWriteTimeUtc(localPath)); // the stores carry the modified time over
        return enc;
    }

    public async Task DownloadAsync(RemoteNode node, string localPath, CancellationToken ct)
    {
        Item item;
        try { item = ItemOf(node); }
        catch (InvalidOperationException)
        {
            // An online-only file opened before this store has read the vault: read it now.
            await GetTreeAsync(_log, ct);
            item = ItemOf(node);
        }
        var enc = TempFile();
        var dec = TempFile();
        try
        {
            await _inner.DownloadAsync(item.Content, enc, ct);
            await Task.Run(() => _vault.DecryptFile(enc, dec), ct);
            File.SetLastWriteTimeUtc(dec, File.GetLastWriteTimeUtc(enc));
            File.Move(dec, localPath, overwrite: true);
        }
        finally { TryDelete(enc); TryDelete(dec); }
    }

    public async Task<RemoteNode> UploadNewAsync(string localPath, string relPath, RemoteNode? parent, CancellationToken ct)
    {
        var (dirId, dirPath, dirNode) = await DirOfAsync(parent, ct);
        var enc = await EncryptAsync(localPath, ct);
        try
        {
            var (itemPath, itemParent, c9s) = await PlaceAsync(_vault.EncryptName(PathUtil.NameOf(relPath), dirId), dirPath, dirNode, ct);
            var content = await _inner.UploadNewAsync(enc, c9s != null ? itemPath + "/contents.c9r" : itemPath, itemParent, ct);
            var item = new Item { Rel = relPath, Content = content, Container = c9s ?? content, Shortened = c9s != null, ParentDirId = dirId };
            Register(item);
            return Wrap(item);
        }
        finally { TryDelete(enc); }
    }

    public async Task<RemoteNode> UploadUpdateAsync(string localPath, RemoteNode node, CancellationToken ct)
    {
        var item = ItemOf(node);
        var enc = await EncryptAsync(localPath, ct);
        try
        {
            var fresh = await _inner.UploadUpdateAsync(enc, item.Content, ct);
            if (!item.Shortened) { Forget(item); item.Container = fresh; }
            item.Content = fresh;
            Register(item);
            return Wrap(item);
        }
        finally { TryDelete(enc); }
    }

    public async Task<RemoteNode> CreateFolderAsync(string relPath, RemoteNode? parent, CancellationToken ct)
    {
        var (dirId, dirPath, dirNode) = await DirOfAsync(parent, ct);
        var newId = Guid.NewGuid().ToString();
        var encrypted = _vault.EncryptName(PathUtil.NameOf(relPath), dirId);
        var (container, dirFile) = await NewDirMarkerAsync(encrypted, newId, dirPath, dirNode, ct);
        await EnsureInnerFolderAsync(_vault.DirPath(newId), ct);
        var item = new Item { Rel = relPath, IsDir = true, Content = dirFile, Container = container, Shortened = encrypted.Length > CryptomatorVault.ShorteningThreshold, ParentDirId = dirId, DirId = newId };
        Register(item);
        return Wrap(item);
    }

    /// <summary>A directory's entry in its parent: the ".c9r" (or ".c9s") folder with dir.c9r naming its id.</summary>
    private async Task<(RemoteNode Container, RemoteNode DirFile)> NewDirMarkerAsync(string encrypted, string dirId, string parentPath, RemoteNode parentNode, CancellationToken ct)
    {
        var (itemPath, itemParent, c9s) = await PlaceAsync(encrypted, parentPath, parentNode, ct);
        var container = c9s ?? await _inner.CreateFolderAsync(itemPath, itemParent, ct);
        lock (_gate) _innerTree[itemPath] = container;
        var dirFile = await UploadTextAsync(itemPath + "/dir.c9r", dirId, container, ct);
        return (container, dirFile);
    }

    public async Task<RemoteNode> MoveAsync(RemoteNode node, string newRelPath, RemoteNode? newParent, CancellationToken ct)
    {
        var item = ItemOf(node);
        var (dirId, dirPath, dirNode) = await DirOfAsync(newParent, ct);
        var encrypted = _vault.EncryptName(PathUtil.NameOf(newRelPath), dirId);
        var longName = encrypted.Length > CryptomatorVault.ShorteningThreshold;
        Forget(item);
        if (!item.Shortened && !longName)
        {
            // The usual case: rename or move the item's own entry (a file, or a directory's small folder).
            var moved = await _inner.MoveAsync(item.Container, dirPath + "/" + encrypted, dirNode, ct);
            if (!item.IsDir) item.Content = moved;
            item.Container = moved;
        }
        else if (!item.IsDir)
        {
            // Into or out of a ".c9s" folder: move the content, drop the old stand-in.
            var (itemPath, itemParent, c9s) = await PlaceAsync(encrypted, dirPath, dirNode, ct);
            var moved = await _inner.MoveAsync(item.Content, c9s != null ? itemPath + "/contents.c9r" : itemPath, itemParent, ct);
            if (item.Shortened) await _inner.DeleteAsync(item.Container, ct);
            item.Content = moved;
            item.Container = c9s ?? moved;
        }
        else
        {
            // A directory keeps its id (and so its contents): a new entry naming the same id, then the old one goes.
            var (container, dirFile) = await NewDirMarkerAsync(encrypted, item.DirId!, dirPath, dirNode, ct);
            await _inner.DeleteAsync(item.Container, ct);
            item.Container = container;
            item.Content = dirFile;
        }
        var oldRel = item.Rel;
        item.Rel = newRelPath;
        item.ParentDirId = dirId;
        item.Shortened = longName;
        Register(item);
        if (item.IsDir)
            lock (_gate)
                foreach (var d in _byRel.Where(x => x.Key.StartsWith(oldRel + "/", StringComparison.OrdinalIgnoreCase)).ToList())
                {
                    _byRel.Remove(d.Key);
                    d.Value.Rel = newRelPath + d.Key[oldRel.Length..];
                    _byRel[d.Value.Rel] = d.Value;
                }
        return Wrap(item);
    }

    public async Task DeleteAsync(RemoteNode node, CancellationToken ct)
    {
        var item = ItemOf(node);
        await _inner.DeleteAsync(item.Container, ct);
        Forget(item);
        if (!item.IsDir) return;
        // A directory's contents live under its id (and its subdirectories' under theirs): remove those folders too.
        List<Item> below;
        lock (_gate) below = _byRel.Where(x => x.Key.StartsWith(item.Rel + "/", StringComparison.OrdinalIgnoreCase)).Select(x => x.Value).ToList();
        foreach (var dir in below.Where(d => d.IsDir).Append(item))
        {
            RemoteNode? physical;
            lock (_gate) _innerTree.TryGetValue(_vault.DirPath(dir.DirId!), out physical);
            if (physical != null)
                try { await _inner.DeleteAsync(physical, ct); }
                catch (Exception ex) { _log.Debug($"[{_pairName}] could not remove the vault folder of {dir.Rel}: {ex.Message}"); }
        }
        foreach (var b in below) Forget(b);
    }

    public async Task<RemoteNode?> RefreshAsync(RemoteNode node, CancellationToken ct)
    {
        var item = ItemOf(node);
        var fresh = await _inner.RefreshAsync(item.IsDir ? item.Container : item.Content, ct);
        if (fresh == null) return null;
        if (item.IsDir) item.Container = fresh;
        else
        {
            item.Content = fresh;
            if (!item.Shortened) item.Container = fresh;
        }
        return Wrap(item);
    }

    public RemoteNode HydrationNode(string rel, string identity, long size) =>
        new() { Id = identity, Name = PathUtil.NameOf(rel), RelPath = rel, Size = size, ModifiedUtc = DateTime.UtcNow };

    public bool CanLink(RemoteNode node) => false;
    public Task<string> LinkUrlAsync(RemoteNode node, CancellationToken ct) => throw new NotSupportedException("an encrypted pair has no links");
    public bool CanImport(RemoteNode node) => false;
    public Task<string?> RevisionAsync(RemoteNode node, CancellationToken ct) => Task.FromResult<string?>(null);
    public Task<string?> WhyNotImportAsync(RemoteNode node, CancellationToken ct) => Task.FromResult<string?>("the pair is encrypted");
    public Task<(RemoteNode? Fresh, string? Revision, string? Why)> PatchAsync(RemoteNode node, string localPath, CancellationToken ct) =>
        Task.FromResult<(RemoteNode?, string?, string?)>((null, null, "the pair is encrypted"));
    public Task<RemoteNode> ImportAsync(RemoteNode node, string localPath, string mime, CancellationToken ct) => throw new NotSupportedException("the pair is encrypted");
    public Task ExportAsync(RemoteNode node, string exportMime, string localPath, CancellationToken ct) => throw new NotSupportedException("the pair is encrypted");

    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
}
