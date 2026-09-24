namespace Unisono;

/// <summary>
/// An ordinary folder as the "remote" side: a network share (\\server\share\...), a mapped drive, a USB disk or
/// another local folder. Ids are full paths; files are identified by size and modified time, which are copied along.
/// Copies are written next to the target under a temporary name and swapped in, so a reader never sees half a file.
/// Deletions go to the Recycle Bin where Windows has one for that drive, otherwise to Unisono's own trash folder.
/// </summary>
public sealed class FolderStore : IRemoteStore
{
    private readonly string _root;

    public FolderStore(SyncPair pair) => _root = pair.RemoteFullPath;

    public string Kind => "Folder";

    public void LoadState(SyncState state) { }
    public void SaveState(SyncState state) { }

    public Task<Dictionary<string, RemoteNode>> GetTreeAsync(Log log, CancellationToken ct)
    {
        if (!Directory.Exists(_root))
            throw new DirectoryNotFoundException($"'{_root}' cannot be reached (is the network drive connected?).");
        var tree = new Dictionary<string, RemoteNode>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(DirectoryInfo Dir, string Rel)>();
        queue.Enqueue((new DirectoryInfo(_root), ""));
        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (dir, rel) = queue.Dequeue();
            // Unlike the local scan, a folder that cannot be listed fails the whole cycle: skipping it would look as if
            // everything in it had been deleted on this side, and the engine would delete it on the other.
            foreach (var e in dir.EnumerateFileSystemInfos())
            {
                if (LocalScanner.IsIgnored(e.Name)) continue;
                if (LocalScanner.IsLinkOrSystem(e)) continue;
                var childRel = PathUtil.Join(rel, e.Name);
                tree[childRel] = ToNode(e, childRel);
                if (e is DirectoryInfo d) queue.Enqueue((d, childRel));
            }
        }
        return Task.FromResult(tree);
    }

    private static RemoteNode ToNode(FileSystemInfo e, string rel) => new()
    {
        Id = e.FullName,
        Name = e.Name,
        ParentId = Path.GetDirectoryName(e.FullName) ?? "",
        MimeType = e is DirectoryInfo ? DriveClient.FolderMime : "application/octet-stream",
        Size = e is FileInfo f ? f.Length : 0,
        Md5 = null,
        ModifiedUtc = e.LastWriteTimeUtc,
        RelPath = rel,
    };

    private static RemoteNode? Stat(string path, string rel)
    {
        if (File.Exists(path)) return ToNode(new FileInfo(path), rel);
        if (Directory.Exists(path)) return ToNode(new DirectoryInfo(path), rel);
        return null;
    }

    public Task<RemoteNode?> RefreshAsync(RemoteNode node, CancellationToken ct) => Task.FromResult(Stat(node.Id, node.RelPath));

    public Task DownloadAsync(RemoteNode node, string localPath, CancellationToken ct) => CopyAsync(node.Id, localPath, ct);

    public RemoteNode HydrationNode(string rel, string identity, long size) =>
        new() { Id = Path.Combine(_root, rel.Replace('/', '\\')), Name = PathUtil.NameOf(rel), RelPath = rel, Size = size, ModifiedUtc = DateTime.UtcNow };

    public async Task<RemoteNode> UploadNewAsync(string localPath, string relPath, RemoteNode? parent, CancellationToken ct)
    {
        var target = Path.Combine(parent?.Id ?? _root, PathUtil.NameOf(relPath));
        await CopyAsync(localPath, target, ct);
        return Stat(target, relPath) ?? throw new IOException($"'{relPath}' is missing right after copying it");
    }

    public async Task<RemoteNode> UploadUpdateAsync(string localPath, RemoteNode node, CancellationToken ct)
    {
        await CopyAsync(localPath, node.Id, ct);
        return Stat(node.Id, node.RelPath) ?? throw new IOException($"'{node.RelPath}' is missing right after copying it");
    }

    public Task<RemoteNode> CreateFolderAsync(string relPath, RemoteNode? parent, CancellationToken ct)
    {
        var path = Path.Combine(parent?.Id ?? _root, PathUtil.NameOf(relPath));
        return Task.FromResult(ToNode(Directory.CreateDirectory(path), relPath));
    }

    public Task<RemoteNode> MoveAsync(RemoteNode node, string newRelPath, RemoteNode? newParent, CancellationToken ct)
    {
        var target = Path.Combine(newParent?.Id ?? _root, PathUtil.NameOf(newRelPath));
        if (node.IsFolder) Directory.Move(node.Id, target);
        else File.Move(node.Id, target);
        return Task.FromResult(Stat(target, newRelPath) ?? throw new IOException($"'{newRelPath}' is missing right after moving it"));
    }

    public Task DeleteAsync(RemoteNode node, CancellationToken ct)
    {
        if (File.Exists(node.Id) || Directory.Exists(node.Id)) RecycleBin.Delete(node.Id);
        return Task.CompletedTask;
    }

    /// <summary>Ids are paths, so everything inside a moved folder gets a new id.</summary>
    public bool HasStableIds => false;

    public string RebaseId(string id, string oldFolderId, string newFolderId) =>
        id.StartsWith(oldFolderId + "\\", StringComparison.OrdinalIgnoreCase) ? newFolderId + id[oldFolderId.Length..] : id;

    public bool CanLink(RemoteNode node) => false;
    public bool CanImport(RemoteNode node) => false;
    public Task<RemoteNode> ImportAsync(RemoteNode node, string localPath, string mime, CancellationToken ct) => throw new NotSupportedException();
    public Task<string?> WhyNotImportAsync(RemoteNode node, CancellationToken ct) => Task.FromResult<string?>("not a document");
    public Task<string> LinkUrlAsync(RemoteNode node, CancellationToken ct) => throw new NotSupportedException();
    public Task ExportAsync(RemoteNode node, string exportMime, string localPath, CancellationToken ct) => throw new NotSupportedException();

    /// <summary>Copies with the modified time, via a temporary file next to the target that the scans ignore.</summary>
    private static async Task CopyAsync(string from, string to, CancellationToken ct)
    {
        var part = to + LocalScanner.PartSuffix;
        try
        {
            // Shared read: Office keeps its documents open, and a plain File.Copy would fail on them.
            await using (var src = PathUtil.OpenShared(from))
            await using (var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
                await src.CopyToAsync(dst, 1 << 16, ct);
            File.SetLastWriteTimeUtc(part, File.GetLastWriteTimeUtc(from));
            if (File.Exists(to)) File.SetAttributes(to, FileAttributes.Normal);
            File.Move(part, to, overwrite: true);
        }
        catch
        {
            try { File.Delete(part); } catch { }
            throw;
        }
    }
}
