namespace Unisono.App;

/// <summary>What a local file or folder in a synced folder corresponds to remotely.</summary>
sealed record LocatedItem(
    SyncPair Pair,
    string Rel,
    bool IsDir,
    /// <summary>Drive: the file or folder id. NAS: the File Station path.</summary>
    string RemoteId,
    /// <summary>The local file is an export or link standing for a native document (Google Doc, Synology Office).</summary>
    bool IsDerived)
{
    public string Name => Rel.Length == 0 ? Path.GetFileName(Pair.LocalFullPath.TrimEnd('\\')) : PathUtil.NameOf(Rel);
}

/// <summary>Maps an Explorer path to its pair and remote item, using the pair's sync state (no network).</summary>
static class ItemLocator
{
    /// <param name="kind">"drive", "nas", or anything else for the innermost pair of any kind.</param>
    public static LocatedItem Locate(string kind, string path)
    {
        path = Path.GetFullPath(path.Trim().TrimEnd('\\'));
        var config = Config.Load();
        var pair = config.ActivePairs
            .Where(p => p.LocalPath.Length > 0 && IsUnder(path, p.LocalFullPath))
            .Where(p => kind switch
            {
                "drive" or "docs" => p.Kind == RemoteKind.Drive,
                "nas" => p.Kind == RemoteKind.Synology,
                _ => true,
            })
            .OrderByDescending(p => p.LocalFullPath.Length) // pairs can nest: the innermost wins
            .FirstOrDefault()
            ?? throw new InvalidOperationException(kind switch
            {
                "drive" or "docs" => $"{path} is not inside a folder Unisono syncs with Google Drive.",
                "nas" => $"{path} is not inside a folder Unisono syncs with a NAS.",
                _ => $"{path} is not inside a folder Unisono syncs.",
            });

        var rel = Path.GetRelativePath(pair.LocalFullPath, path).Replace('\\', '/');
        if (rel == ".") rel = "";
        var isDir = Directory.Exists(path);
        var state = SyncState.Load(pair);

        if (rel.Length == 0)
        {
            var root = pair.Kind == RemoteKind.Synology ? pair.RemotePath : state.RootId ?? throw NotYet(rel);
            return new LocatedItem(pair, rel, true, root, false);
        }
        if (state.Entries.TryGetValue(rel, out var e) && e.Kind is EntryKind.File or EntryKind.Folder)
            return new LocatedItem(pair, rel, e.Kind == EntryKind.Folder || isDir, e.DriveId, false);
        // An export (Plan.docx) or link (Plan.url) stands for the native document it came from.
        var derived = state.Entries.Values.FirstOrDefault(x => x.LocalName != null && x.LocalName.Equals(rel, StringComparison.OrdinalIgnoreCase));
        if (derived != null) return new LocatedItem(pair, rel, false, derived.DriveId, true);
        throw NotYet(rel);
    }

    private static Exception NotYet(string rel) =>
        new InvalidOperationException($"'{(rel.Length == 0 ? "This folder" : rel)}' has not been synced yet, so there is nothing online for it. Try again after the next sync.");

    private static bool IsUnder(string path, string folder)
    {
        var f = folder.TrimEnd('\\');
        return path.Equals(f, StringComparison.OrdinalIgnoreCase) || path.StartsWith(f + "\\", StringComparison.OrdinalIgnoreCase);
    }
}
