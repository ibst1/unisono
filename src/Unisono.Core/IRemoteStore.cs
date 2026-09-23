namespace Unisono;

/// <summary>
/// The remote side of a sync pair. Google Drive and a Synology NAS both fit behind this: the engine only needs a
/// path-keyed tree, transfers with the modified time preserved, folder creation and a (preferably recoverable) delete.
/// Backends differ in what they can offer: Drive has a change feed and md5 per file, Synology has neither, so the
/// engine falls back to size + modified time and to hashing after download.
/// </summary>
public interface IRemoteStore
{
    /// <summary>"Drive" or "Synology"; shown in logs.</summary>
    string Kind { get; }

    /// <summary>Restore backend state (Drive index and changes token) persisted in the pair's state file.</summary>
    void LoadState(SyncState state);
    void SaveState(SyncState state);

    /// <summary>Current path-keyed tree of the remote folder. Sets <see cref="RemoteNode.RelPath"/> on every node.</summary>
    Task<Dictionary<string, RemoteNode>> GetTreeAsync(Log log, CancellationToken ct);

    /// <summary>
    /// Fresh metadata for one item straight from the remote, bypassing any cached tree; null when it is gone.
    /// Drive's change feed can lag by minutes, so the engine asks this before overwriting or deleting remotely.
    /// </summary>
    Task<RemoteNode?> RefreshAsync(RemoteNode node, CancellationToken ct);

    Task DownloadAsync(RemoteNode node, string localPath, CancellationToken ct);

    /// <summary>Create a file under <paramref name="parent"/> (null = the pair root) with the local content and modified time.</summary>
    Task<RemoteNode> UploadNewAsync(string localPath, string relPath, RemoteNode? parent, CancellationToken ct);

    Task<RemoteNode> UploadUpdateAsync(string localPath, RemoteNode node, CancellationToken ct);

    Task<RemoteNode> CreateFolderAsync(string relPath, RemoteNode? parent, CancellationToken ct);

    /// <summary>Delete recoverably where the backend allows it (Drive trash, NAS recycle bin), subtree included.</summary>
    Task DeleteAsync(RemoteNode node, CancellationToken ct);

    /// <summary>A browser URL for the item, used for cloud-only links; null when the backend has no useful one.</summary>
    bool CanLink(RemoteNode node);

    /// <summary>A browser URL for the item, used for links (cloud-only files, native documents). Only called when <see cref="CanLink"/>.</summary>
    Task<string> LinkUrlAsync(RemoteNode node, CancellationToken ct);

    /// <summary>Export a native document (Google Docs, Synology Office) to an Office file.</summary>
    Task ExportAsync(RemoteNode node, string exportMime, string localPath, CancellationToken ct);
}
