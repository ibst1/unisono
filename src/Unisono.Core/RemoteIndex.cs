using System.Net;
using System.Text.Json.Serialization;
using Google;
using File = Google.Apis.Drive.v3.Data.File;

namespace Unisono;

/// <summary>A file on the remote twice by the same name and with the same content: <paramref name="KeptId"/> is synced, <paramref name="CopyId"/> is left out.</summary>
public sealed record DuplicateFile(string Rel, string KeptId, string CopyId, string Md5, long Size);

public sealed class RemoteNode
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string ParentId { get; set; } = "";
    public string MimeType { get; set; } = "";
    public long Size { get; set; }
    public string? Md5 { get; set; }
    /// <summary>Box gives SHA-1 instead of MD5 (lower-case hex); null elsewhere.</summary>
    public string? Sha1 { get; set; }
    public DateTime ModifiedUtc { get; set; }

    [JsonIgnore] public bool IsFolder => MimeType == DriveClient.FolderMime;
    [JsonIgnore] public bool IsGoogleNative => MimeType.StartsWith("application/vnd.google-apps.", StringComparison.Ordinal) && !IsFolder;

    // Synology Office files (.odoc/.osheet/.oslides) are small pointers into Synology Office's database, like Google Docs.
    public const string SynologyDocument = "application/x-synology-office-document";
    public const string SynologySpreadsheet = "application/x-synology-office-spreadsheet";
    public const string SynologyPresentation = "application/x-synology-office-presentation";
    [JsonIgnore] public bool IsSynologyOffice => MimeType is SynologyDocument or SynologySpreadsheet or SynologyPresentation;

    /// <summary>A document that only exists inside an online editor: exported or linked, never synced as bytes.</summary>
    [JsonIgnore] public bool IsNativeDocument => IsGoogleNative || IsSynologyOffice;
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

    /// <summary>
    /// Lists a folder and everything below it, level by level: up to 40 folders per request and 4 requests at a time.
    /// A tree of thousands of folders (a program folder with git repositories) otherwise takes many minutes.
    /// </summary>
    private async Task ListSubtreeAsync(DriveClient drive, string folderId, CancellationToken ct)
    {
        var level = new List<string> { folderId };
        while (level.Count > 0)
        {
            var batches = level.Chunk(40).ToList();
            var results = new List<(Google.Apis.Drive.v3.Data.File File, string ParentId)>[batches.Count];
            await Parallel.ForEachAsync(Enumerable.Range(0, batches.Count), new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
                async (i, token) => results[i] = await drive.ListChildrenOfAsync(batches[i], token));
            var next = new List<string>();
            foreach (var batch in results)
                foreach (var (child, parent) in batch)
                {
                    _nodes[child.Id] = RemoteNode.From(child, parent);
                    if (DriveClient.IsFolder(child)) next.Add(child.Id);
                }
            level = next;
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
    /// differences); Windows does not. Of files with the same content (same MD5) one is enough here: the others are
    /// left out. Any other second item gets a free name here ("name (2).ext", see <see cref="PathUtil.FreeName"/>) and
    /// keeps its own name on Drive; renaming or moving it here renames it there too, which ends the clash. The order is
    /// fixed (by name, then id), so each keeps its name from one listing to the next. Nodes that are no longer
    /// reachable (moved out, parent trashed) are pruned here.
    /// </summary>
    /// <summary>As of the last <see cref="BuildTree"/>: files that are on Drive twice with the same content, of which only one is synced.</summary>
    public List<DuplicateFile> Duplicates { get; private set; } = new();

    /// <summary>Items already told about (the tree is built every cycle): once per process.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> Told = new();

    public Dictionary<string, RemoteNode> BuildTree(Log log)
    {
        var tree = new Dictionary<string, RemoteNode>(StringComparer.OrdinalIgnoreCase);
        var duplicates = new List<DuplicateFile>();
        var byParent =_nodes.Values.GroupBy(n => n.ParentId).ToDictionary(g => g.Key, g => g.OrderBy(n => n.Name, StringComparer.Ordinal).ThenBy(n => n.Id, StringComparer.Ordinal).ToList());
        var reachable = new HashSet<string>();
        var stack = new Stack<(string Id, string Prefix)>();
        stack.Push((RootId, ""));
        while (stack.Count > 0)
        {
            var (id, prefix) = stack.Pop();
            if (!byParent.TryGetValue(id, out var kids)) continue;
            var names = new HashSet<string>(kids.Select(k => k.Name), StringComparer.OrdinalIgnoreCase);
            foreach (var kid in kids)
            {
                var rel = prefix + kid.Name;
                if (kid.Name.Contains('/') || kid.Name.Contains('\\') || kid.Name is "." or "..")
                {
                    if (Told.TryAdd(kid.Id, true)) log.Warn($"skipping '{rel}': name not representable on Windows");
                    continue;
                }
                if (tree.TryGetValue(rel, out var first))
                {
                    reachable.Add(kid.Id); // still part of the folder, just not shown twice
                    if (!kid.IsFolder && !first.IsFolder && kid.Md5 != null && kid.Md5 == first.Md5 && kid.Size == first.Size)
                    {
                        duplicates.Add(new DuplicateFile(rel, first.Id, kid.Id, kid.Md5, kid.Size));
                        if (Told.TryAdd(kid.Id, true))
                            log.Info($"'{rel}' is twice on Drive with the same content (Drive ids {first.Id} and {kid.Id}): synced once; the copy stays on Drive untouched");
                        continue;
                    }
                    var free = PathUtil.FreeName(kid.Name, kid.IsFolder, n => names.Contains(n) || tree.ContainsKey(prefix + n));
                    names.Add(free);
                    if (Told.TryAdd(kid.Id, true))
                        log.Info($"'{rel}' is twice on Drive{(kid.IsFolder || first.IsFolder ? "" : " with different content")}: the second (Drive id {kid.Id}) is synced here as '{free}'; " +
                                 "renaming it here renames it on Drive too");
                    rel = prefix + free;
                }
                kid.RelPath = rel;
                tree[rel] = kid;
                reachable.Add(kid.Id);
                if (kid.IsFolder) stack.Push((kid.Id, rel + "/"));
            }
        }
        foreach (var id in _nodes.Keys.Where(k => !reachable.Contains(k)).ToList())
            _nodes.Remove(id);
        Duplicates = duplicates;
        return tree;
    }
}
