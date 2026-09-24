namespace Unisono;

/// <summary>Google Drive behind <see cref="IRemoteStore"/>: a change-fed index, md5 per file, trash, Docs export.</summary>
public sealed class DriveStore : IRemoteStore
{
    private readonly DriveClient _drive;
    private readonly SyncPair _pair;
    private RemoteIndex? _index;
    private string? _rootId;
    private DateTime _lastFullIndexUtc;

    public DriveStore(DriveClient drive, SyncPair pair)
    {
        _drive = drive;
        _pair = pair;
    }

    public string Kind => "Drive";

    public void LoadState(SyncState state)
    {
        _index = RemoteIndex.FromState(state);
        _rootId = state.RootId;
        _lastFullIndexUtc = state.IndexBuiltUtc;
    }

    public void SaveState(SyncState state)
    {
        _index?.SaveTo(state);
        state.IndexBuiltUtc = _lastFullIndexUtc;
    }

    public async Task<Dictionary<string, RemoteNode>> GetTreeAsync(Log log, CancellationToken ct)
    {
        if (_index == null)
        {
            var folder = await _drive.ResolveFolderAsync(_pair.RemotePath, ct);
            if (_rootId != null && _rootId != folder.Id)
                throw new InvalidOperationException(
                    $"State file {SyncState.FilePathFor(_pair)} belongs to a different Drive folder. Delete it to start over (existing local files will then be matched by content).");
            _index = await RebuildAsync(folder.Id, log, ct);
        }
        else if (_pair.FullRescanMinutes > 0 && DateTime.UtcNow - _lastFullIndexUtc > TimeSpan.FromMinutes(_pair.FullRescanMinutes))
        {
            _index = await RebuildAsync(_index.RootId, log, ct);
        }
        else
        {
            try
            {
                await _index.ApplyChangesAsync(_drive, log, ct);
            }
            catch (StaleChangesTokenException ex)
            {
                log.Warn($"[{_pair.Name}] {ex.Message}");
                _index = await RebuildAsync(_index.RootId, log, ct);
            }
        }
        return _index.BuildTree(log);
    }

    private async Task<RemoteIndex> RebuildAsync(string rootId, Log log, CancellationToken ct)
    {
        log.Debug($"[{_pair.Name}] indexing Drive folder...");
        var index = await RemoteIndex.BuildAsync(_drive, rootId, ct);
        _lastFullIndexUtc = DateTime.UtcNow;
        _rootId = rootId;
        log.Info($"[{_pair.Name}] indexed {index.Count} items on Drive");
        return index;
    }

    private string ParentId(RemoteNode? parent) => parent?.Id ?? _index!.RootId;

    public async Task<RemoteNode?> RefreshAsync(RemoteNode node, CancellationToken ct)
    {
        var f = await _drive.GetFileAsync(node.Id, ct);
        if (f == null) { if (node.IsFolder) _index!.RemoveSubtree(node.Id); else _index!.Remove(node.Id); return null; }
        var fresh = RemoteNode.From(f, f.Parents?.FirstOrDefault() ?? node.ParentId);
        fresh.RelPath = node.RelPath;
        _index!.Add(fresh);
        return fresh;
    }

    public bool CanImport(RemoteNode node) => node.MimeType is "application/vnd.google-apps.document"
        or "application/vnd.google-apps.spreadsheet" or "application/vnd.google-apps.presentation";

    public Task<string?> RevisionAsync(RemoteNode node, CancellationToken ct) =>
        node.MimeType == "application/vnd.google-apps.document" ? _drive.DocsRevisionAsync(node.Id, ct) : Task.FromResult<string?>(null);

    public async Task<string?> WhyNotImportAsync(RemoteNode node, CancellationToken ct)
    {
        var risk = await _drive.RoundTripRiskAsync(node.Id, node.MimeType == "application/vnd.google-apps.document", ct);
        if (risk == null)
            return "Unisono cannot check it for tabs: the Google Docs API is not enabled for Unisono's Google project";
        if (risk.Value.Tabs > 1) return $"it has {risk.Value.Tabs} tabs, which a Word file cannot hold (they would be merged into one)";
        if (risk.Value.Comments > 0) return $"it has {risk.Value.Comments} open comment(s), which would lose their place";
        return null;
    }

    public async Task<RemoteNode> ImportAsync(RemoteNode node, string localPath, string mime, CancellationToken ct)
    {
        var updated = await _drive.ImportIntoAsync(localPath, node.Id, mime, ct);
        var fresh = RemoteNode.From(updated, node.ParentId);
        fresh.RelPath = node.RelPath;
        _index!.Add(fresh);
        return fresh;
    }

    public RemoteNode HydrationNode(string rel, string identity, long size) =>
        new() { Id = identity, Name = PathUtil.NameOf(rel), RelPath = rel, Size = size, ModifiedUtc = DateTime.UtcNow };

    public async Task DownloadAsync(RemoteNode node, string localPath, CancellationToken ct)
    {
        var file = new Google.Apis.Drive.v3.Data.File { Id = node.Id, Name = node.Name, ModifiedTimeDateTimeOffset = node.ModifiedUtc };
        await _drive.DownloadAsync(file, localPath, ct);
    }

    public async Task<RemoteNode> UploadNewAsync(string localPath, string relPath, RemoteNode? parent, CancellationToken ct)
    {
        var parentId = ParentId(parent);
        var created = await _drive.UploadNewAsync(localPath, PathUtil.NameOf(relPath), parentId, ct);
        var node = RemoteNode.From(created, parentId);
        node.RelPath = relPath;
        _index!.Add(node);
        return node;
    }

    public async Task<RemoteNode> UploadUpdateAsync(string localPath, RemoteNode node, CancellationToken ct)
    {
        var updated = await _drive.UploadUpdateAsync(localPath, node.Id, ct);
        var fresh = RemoteNode.From(updated, node.ParentId);
        fresh.RelPath = node.RelPath;
        _index!.Add(fresh);
        return fresh;
    }

    public async Task<RemoteNode> CreateFolderAsync(string relPath, RemoteNode? parent, CancellationToken ct)
    {
        var parentId = ParentId(parent);
        var created = await _drive.CreateFolderAsync(PathUtil.NameOf(relPath), parentId, ct);
        var node = RemoteNode.From(created, parentId);
        node.RelPath = relPath;
        _index!.Add(node);
        return node;
    }

    public async Task<RemoteNode> MoveAsync(RemoteNode node, string newRelPath, RemoteNode? newParent, CancellationToken ct)
    {
        var parentId = ParentId(newParent);
        var moved = await _drive.MoveAsync(node.Id, PathUtil.NameOf(newRelPath), node.ParentId, parentId, ct);
        var fresh = RemoteNode.From(moved, parentId);
        fresh.RelPath = newRelPath;
        _index!.Add(fresh); // children keep pointing at the same folder id
        return fresh;
    }

    public bool HasStableIds => true;

    public string RebaseId(string id, string oldFolderId, string newFolderId) => id;

    public async Task DeleteAsync(RemoteNode node, CancellationToken ct)
    {
        await _drive.TrashAsync(node.Id, ct);
        if (node.IsFolder) _index!.RemoveSubtree(node.Id); else _index!.Remove(node.Id);
    }

    public bool CanLink(RemoteNode node) => true;

    public Task<string> LinkUrlAsync(RemoteNode node, CancellationToken ct) => Task.FromResult($"https://drive.google.com/open?id={node.Id}");

    public Task ExportAsync(RemoteNode node, string exportMime, string localPath, CancellationToken ct) =>
        _drive.ExportAsync(node.Id, node.Name, exportMime, localPath, node.ModifiedUtc, ct);
}
