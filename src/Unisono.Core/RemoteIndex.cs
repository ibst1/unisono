using System.Net;
using System.Text.Json.Serialization;
using Google;
using File = Google.Apis.Drive.v3.Data.File;

namespace Unisono;

public sealed class RemoteNode
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string ParentId { get; set; } = "";
    public string MimeType { get; set; } = "";
    public long Size { get; set; }
    public string? Md5 { get; set; }
    public DateTime ModifiedUtc { get; set; }

    [JsonIgnore] public bool IsFolder => MimeType == DriveClient.FolderMime;
    [JsonIgnore] public bool IsGoogleNative => MimeType.StartsWith("application/vnd.google-apps.", StringComparison.Ordinal) && !IsFolder;
    /// <summary>Set by <see cref="RemoteIndex.BuildTree"/>.</summary>
    [JsonIgnore] public string RelPath { get; set; } = "";

    public static RemoteNode From(File f, string parentId) => new()
    {
        Id = f.Id,
        Name = f.Name,
        ParentId = parentId,
        MimeType = f.MimeType ?? "",
        Size = f.Size ?? 0,
        Md5 = f.Md5Checksum?.ToLowerInvariant(),
        ModifiedUtc = f.ModifiedTimeDateTimeOffset?.UtcDateTime ?? DateTime.MinValue,
    };
}

public sealed class StaleChangesTokenException : Exception
{
    public StaleChangesTokenException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Id-keyed mirror of the Drive subtree under one folder, kept current with changes.list. Paths are derived
/// from parent links when the tree is built, so a move on Drive is just a parent change here.
/// </summary>
public sealed class RemoteIndex
{
    public string RootId { get; }
    public string ChangesToken { get; private set; }
    private readonly Dictionary<string, RemoteNode> _nodes;

    private RemoteIndex(string rootId, string token, IEnumerable<RemoteNode> nodes)
    {
        RootId = rootId;
        ChangesToken = token;
        _nodes = nodes.ToDictionary(n => n.Id);
    }

    public static async Task<RemoteIndex> BuildAsync(DriveClient drive, string rootId, CancellationToken ct)
    {
        // Token first, listing second: anything that changes during the listing is replayed on the next poll.
        var token = await drive.GetStartPageTokenAsync(ct);
        var index = new RemoteIndex(rootId, token, Array.Empty<RemoteNode>());
        await index.ListSubtreeAsync(drive, rootId, ct);
        return index;
    }

    public static RemoteIndex? FromState(SyncState state) =>
        state.RootId == null || state.ChangesToken == null ? null : new RemoteIndex(state.RootId, state.ChangesToken, state.Index);

    public void SaveTo(SyncState state)
    {
        state.RootId = RootId;
        state.ChangesToken = ChangesToken;
        state.Index = _nodes.Values.ToList();
    }

    public int Count => _nodes.Count;

    /// <summary>Pulls everything that changed on Drive since the token. Returns true when the subtree was affected.</summary>
    public async Task<bool> ApplyChangesAsync(DriveClient drive, Log log, CancellationToken ct)
    {
        List<Google.Apis.Drive.v3.Data.Change> changes;
        string newToken;
        try
        {
            (changes, newToken) = await drive.ListChangesAsync(ChangesToken, ct);
        }
        catch (GoogleApiException ex) when (ex.HttpStatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            throw new StaleChangesTokenException("Drive no longer accepts the stored changes token; a full re-index is needed.", ex);
        }

        var affected = false;
        var relist = new List<string>();
        foreach (var c in changes)
        {
            if (c.FileId == RootId && (c.Removed == true || c.File?.Trashed == true))
                throw new InvalidOperationException("The synced Drive folder itself was deleted or trashed.");

            if (c.Removed == true || c.File == null || c.File.Trashed == true)
            {
                if (_nodes.Remove(c.FileId)) affected = true;
                continue;
            }
            var parent = c.File.Parents?.FirstOrDefault();
            var inTree = parent != null && (parent == RootId || _nodes.ContainsKey(parent));
            if (!inTree)
            {
                if (_nodes.Remove(c.FileId)) affected = true; // moved out of the tree
                continue;
            }
            var wasKnown = _nodes.TryGetValue(c.FileId, out var old);
            var node = RemoteNode.From(c.File, parent!);
            _nodes[c.FileId] = node;
            affected = true;
            // A folder that is new here, or moved in from elsewhere, brings children we have never listed.
            if (node.IsFolder && (!wasKnown || old!.ParentId != parent))
                relist.Add(node.Id);
        }
        foreach (var id in relist)
            await ListSubtreeAsync(drive, id, ct);

        ChangesToken = newToken;
        if (changes.Count > 0)
            log.Debug($"changes: {changes.Count} on Drive, subtree {(affected ? "affected" : "untouched")}");
        return affected;
    }

    private async Task ListSubtreeAsync(DriveClient drive, string folderId, CancellationToken ct)
    {
        var queue = new Queue<string>();
        queue.Enqueue(folderId);
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            foreach (var child in await drive.ListChildrenAsync(id, ct))
            {
                _nodes[child.Id] = RemoteNode.From(child, id);
                if (DriveClient.IsFolder(child)) queue.Enqueue(child.Id);
            }
        }
    }

    public void Add(RemoteNode node) => _nodes[node.Id] = node;

    public void Remove(string id) => _nodes.Remove(id);

    public void RemoveSubtree(string id)
    {
        var byParent = _nodes.Values.GroupBy(n => n.ParentId).ToDictionary(g => g.Key, g => g.ToList());
        var stack = new Stack<string>();
        stack.Push(id);
        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            _nodes.Remove(cur);
            if (byParent.TryGetValue(cur, out var kids))
                foreach (var k in kids) stack.Push(k.Id);
        }
    }

    /// <summary>
    /// Path-keyed view of everything reachable from the root. Drive allows sibling name clashes (and case-only
    /// differences); Windows does not, so the first one wins and the rest are reported and left out. Nodes that
    /// are no longer reachable (moved out, parent trashed) are pruned here.
    /// </summary>
    public Dictionary<string, RemoteNode> BuildTree(Log log)
    {
        var tree = new Dictionary<string, RemoteNode>(StringComparer.OrdinalIgnoreCase);
        var byParent = _nodes.Values.GroupBy(n => n.ParentId).ToDictionary(g => g.Key, g => g.OrderBy(n => n.Name, StringComparer.Ordinal).ThenBy(n => n.Id, StringComparer.Ordinal).ToList());
        var reachable = new HashSet<string>();
        var stack = new Stack<(string Id, string Prefix)>();
        stack.Push((RootId, ""));
        while (stack.Count > 0)
        {
            var (id, prefix) = stack.Pop();
            if (!byParent.TryGetValue(id, out var kids)) continue;
            foreach (var kid in kids)
            {
                var rel = prefix + kid.Name;
                if (kid.Name.Contains('/') || kid.Name.Contains('\\') || kid.Name is "." or "..")
                {
                    log.Warn($"skipping '{rel}': name not representable on Windows");
                    continue;
                }
                if (tree.ContainsKey(rel))
                {
                    log.Warn($"skipping duplicate '{rel}' (Drive id {kid.Id}); Windows cannot hold two files by that name");
                    continue;
                }
                kid.RelPath = rel;
                tree[rel] = kid;
                reachable.Add(kid.Id);
                if (kid.IsFolder) stack.Push((kid.Id, rel + "/"));
            }
        }
        foreach (var id in _nodes.Keys.Where(k => !reachable.Contains(k)).ToList())
            _nodes.Remove(id);
        return tree;
    }
}
