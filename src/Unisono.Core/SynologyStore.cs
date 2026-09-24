namespace Unisono;

/// <summary>
/// A folder on a Synology NAS behind <see cref="IRemoteStore"/>, via the File Station API. No change feed and no
/// checksums: the tree is re-listed every cycle and files are identified by size and modified time, which Unisono
/// sets itself on upload. Deletes land in the shared folder's recycle bin when that is enabled in DSM.
/// </summary>
public sealed class SynologyStore : IRemoteStore
{
    private readonly SynologyClient _nas;
    private readonly SyncPair _pair;
    private readonly string _root;

    public SynologyStore(SynologyClient nas, SyncPair pair)
    {
        _nas = nas;
        _pair = pair;
        _root = "/" + pair.RemotePath.Replace('\\', '/').Trim('/');
    }

    public string Kind => "Synology";

    public void LoadState(SyncState state) { }
    public void SaveState(SyncState state) { }

    private string FullPath(string rel) => rel.Length == 0 ? _root : _root + "/" + rel;

    public async Task<Dictionary<string, RemoteNode>> GetTreeAsync(Log log, CancellationToken ct)
    {
        if (await _nas.GetInfoAsync(_root, ct) is not { IsDir: true })
            throw new DirectoryNotFoundException($"'{_root}' does not exist on the NAS (paths start with the shared folder, e.g. /home/Sync).");
        var tree = new Dictionary<string, RemoteNode>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(string Path, string Rel)>();
        queue.Enqueue((_root, ""));
        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (path, rel) = queue.Dequeue();
            foreach (var e in await _nas.ListAsync(path, ct))
            {
                if (e.Name.StartsWith("#", StringComparison.Ordinal) || e.Name == "@eaDir") continue; // #recycle, #snapshot, thumbnails
                var childRel = PathUtil.Join(rel, e.Name);
                if (tree.ContainsKey(childRel))
                {
                    log.Warn($"skipping '{childRel}': a sibling differs only in case, which Windows cannot hold");
                    continue;
                }
                var node = ToNode(e, path, childRel);
                tree[childRel] = node;
                if (e.IsDir) queue.Enqueue((e.Path, childRel));
            }
        }
        return tree;
    }

    private static RemoteNode ToNode(SynologyEntry e, string parentPath, string rel) => new()
    {
        Id = e.Path,
        Name = e.Name,
        ParentId = parentPath,
        MimeType = e.IsDir ? DriveClient.FolderMime : MimeFor(e.Name),
        Size = e.Size,
        Md5 = null,
        ModifiedUtc = e.MtimeUtc,
        RelPath = rel,
    };

    public async Task<RemoteNode?> RefreshAsync(RemoteNode node, CancellationToken ct)
    {
        var info = await _nas.GetInfoAsync(node.Id, ct);
        return info == null ? null : ToNode(info, node.ParentId, node.RelPath);
    }

    /// <summary>Synology Office has no import API that File Station reaches: its exports stay read-only.</summary>
    public bool CanImport(RemoteNode node) => false;
    public Task<RemoteNode> ImportAsync(RemoteNode node, string localPath, string mime, CancellationToken ct) => throw new NotSupportedException();
    public Task<(RemoteNode? Fresh, string? Revision, string? Why)> PatchAsync(RemoteNode node, string localPath, CancellationToken ct) =>
        Task.FromResult<(RemoteNode?, string?, string?)>((null, null, "Synology Office cannot import"));
    public Task<string?> RevisionAsync(RemoteNode node, CancellationToken ct) => Task.FromResult<string?>(null);
    public Task<string?> WhyNotImportAsync(RemoteNode node, CancellationToken ct) => Task.FromResult<string?>("Synology Office cannot import");

    public RemoteNode HydrationNode(string rel, string identity, long size) =>
        new() { Id = FullPath(rel), Name = PathUtil.NameOf(rel), RelPath = rel, Size = size, ModifiedUtc = DateTime.UtcNow };

    public Task DownloadAsync(RemoteNode node, string localPath, CancellationToken ct) =>
        _nas.DownloadAsync(node.Id, localPath, node.ModifiedUtc, ct);

    public async Task<RemoteNode> UploadNewAsync(string localPath, string relPath, RemoteNode? parent, CancellationToken ct)
    {
        var folder = parent?.Id ?? _root;
        var name = PathUtil.NameOf(relPath);
        await _nas.UploadAsync(localPath, folder, name, ct);
        var info = await _nas.GetInfoAsync(folder + "/" + name, ct)
                   ?? throw new IOException($"'{relPath}' is missing on the NAS right after upload");
        return ToNode(info, folder, relPath);
    }

    public async Task<RemoteNode> UploadUpdateAsync(string localPath, RemoteNode node, CancellationToken ct)
    {
        await _nas.UploadAsync(localPath, node.ParentId, node.Name, ct);
        var info = await _nas.GetInfoAsync(node.Id, ct)
                   ?? throw new IOException($"'{node.RelPath}' is missing on the NAS right after upload");
        return ToNode(info, node.ParentId, node.RelPath);
    }

    public async Task<RemoteNode> CreateFolderAsync(string relPath, RemoteNode? parent, CancellationToken ct)
    {
        var folder = parent?.Id ?? _root;
        var created = await _nas.CreateFolderAsync(folder, PathUtil.NameOf(relPath), ct);
        return ToNode(created, folder, relPath);
    }

    public Task DeleteAsync(RemoteNode node, CancellationToken ct) => _nas.DeleteAsync(node.Id, ct);

    public async Task<RemoteNode> MoveAsync(RemoteNode node, string newRelPath, RemoteNode? newParent, CancellationToken ct)
    {
        var folder = newParent?.Id ?? _root;
        var newName = PathUtil.NameOf(newRelPath);
        var path = node.Id;
        // Rename first (in the old folder), then move: File Station has separate calls for the two.
        if (!PathUtil.NameOf(path).Equals(newName, StringComparison.Ordinal)) path = await _nas.RenameAsync(path, newName, ct);
        if (!node.ParentId.Equals(folder, StringComparison.Ordinal)) path = await _nas.MoveAsync(path, folder, ct);
        var info = await _nas.GetInfoAsync(path, ct) ?? throw new IOException($"'{newRelPath}' is missing on the NAS right after moving it");
        return ToNode(info, folder, newRelPath);
    }

    /// <summary>NAS ids are paths, so everything inside a moved folder gets a new id.</summary>
    public bool HasStableIds => false;

    public string RebaseId(string id, string oldFolderId, string newFolderId) =>
        id.StartsWith(oldFolderId + "/", StringComparison.Ordinal) ? newFolderId + id[oldFolderId.Length..] : id;

    private static string MimeFor(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".odoc" => RemoteNode.SynologyDocument,
        ".osheet" => RemoteNode.SynologySpreadsheet,
        ".oslides" => RemoteNode.SynologyPresentation,
        _ => "application/octet-stream",
    };

    /// <summary>
    /// Synology Office and Drive address files by Drive path: the user's home is /mydrive, a shared folder enabled as a
    /// Drive team folder is /team-folders/&lt;name&gt;. File Station paths are /home/..., /homes/&lt;user&gt;/... or /&lt;share&gt;/....
    /// </summary>
    private string DrivePath(RemoteNode node) => DrivePathFor(node.Id, _nas.User);

    /// <summary>File Station path to Synology Drive path (see above); also used by Explorer's "Open in Synology Drive".</summary>
    public static string DrivePathFor(string fileStationPath, string user)
    {
        var parts = fileStationPath.Trim('/').Split('/');
        if (parts[0].Equals("home", StringComparison.OrdinalIgnoreCase))
            return ("/mydrive/" + string.Join('/', parts.Skip(1))).TrimEnd('/');
        if (parts[0].Equals("homes", StringComparison.OrdinalIgnoreCase) && parts.Length > 1 && parts[1].Equals(user, StringComparison.OrdinalIgnoreCase))
            return ("/mydrive/" + string.Join('/', parts.Skip(2))).TrimEnd('/');
        return "/team-folders/" + string.Join('/', parts);
    }

    /// <summary>Only Synology Office documents get links; ordinary files have no browser view worth pointing to.</summary>
    public bool CanLink(RemoteNode node) => node.IsSynologyOffice;

    public async Task<string> LinkUrlAsync(RemoteNode node, CancellationToken ct) =>
        $"{_nas.Server}/d/f/{await _nas.DrivePermanentLinkAsync(DrivePath(node), ct)}";

    public Task ExportAsync(RemoteNode node, string exportMime, string localPath, CancellationToken ct) =>
        _nas.ExportOfficeAsync(DrivePath(node), localPath, node.ModifiedUtc, ct);

    /// <summary>Creates the root folder chain (init --create).</summary>
    public async Task EnsureRootAsync(CancellationToken ct)
    {
        if (await _nas.GetInfoAsync(_root, ct) is { IsDir: true }) return;
        var parts = _root.Trim('/').Split('/');
        if (parts.Length < 2) throw new InvalidOperationException("Give a path inside a shared folder, e.g. /home/Sync; shared folders themselves are created in DSM.");
        var current = "/" + parts[0];
        foreach (var part in parts.Skip(1))
        {
            var next = current + "/" + part;
            if (await _nas.GetInfoAsync(next, ct) is not { IsDir: true })
                await _nas.CreateFolderAsync(current, part, ct);
            current = next;
        }
    }
}
