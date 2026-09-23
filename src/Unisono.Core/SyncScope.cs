namespace Unisono;

/// <summary>
/// Which relative paths of a pair take part in syncing (selective sync). A folder is in scope when it is inside an
/// included folder, or is an ancestor of one ("bridge" folder, needed to reach it). Files are in scope only inside
/// included folders. Excluded folders and everything in them are out. Without includes, everything not excluded is in.
/// </summary>
public sealed class SyncScope
{
    private readonly string[] _include;
    private readonly string[] _exclude;

    /// <param name="others">All other pairs. A pair whose local folder lies inside this one's (nested pairs, e.g. a Drive
    /// folder inside a NAS folder) is excluded here, locally and remotely, so each file belongs to exactly one pair.</param>
    public SyncScope(SyncPair pair, IEnumerable<SyncPair>? others = null)
    {
        _include = Normalize(pair.IncludeFolders);
        _exclude = Normalize(pair.ExcludeFolders.Concat(NestedPaths(pair, others ?? Array.Empty<SyncPair>())));
    }

    public static IEnumerable<string> NestedPaths(SyncPair pair, IEnumerable<SyncPair> others)
    {
        var root = Path.GetFullPath(pair.LocalPath).TrimEnd('\\') + "\\";
        foreach (var o in others)
        {
            if (ReferenceEquals(o, pair) || string.IsNullOrEmpty(o.LocalPath)) continue;
            var theirs = Path.GetFullPath(o.LocalPath).TrimEnd('\\');
            if (theirs.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                yield return theirs[root.Length..].Replace('\\', '/');
        }
    }

    public bool IsEverything => _include.Length == 0 && _exclude.Length == 0;

    private static string[] Normalize(IEnumerable<string> list) =>
        list.Select(s => s.Replace('\\', '/').Trim('/')).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private static bool Under(string rel, string folder) =>
        rel.Equals(folder, StringComparison.OrdinalIgnoreCase) || rel.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase);

    public bool Contains(string rel, bool isDir)
    {
        if (_exclude.Any(x => Under(rel, x))) return false;
        if (_include.Length == 0) return true;
        if (_include.Any(i => Under(rel, i))) return true;
        return isDir && IsBridge(rel);
    }

    /// <summary>A folder that is only in scope because an included folder lies inside it; its own files are not synced.</summary>
    public bool IsBridge(string rel) =>
        _include.Length > 0 && !_include.Any(i => Under(rel, i)) && _include.Any(i => i.StartsWith(rel + "/", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A folder that must never be deleted as a whole, because it holds content outside the scope: a bridge folder, or a
    /// folder with an excluded folder somewhere below it. Deleting it on one side only removes its in-scope content on
    /// the other, item by item.
    /// </summary>
    public bool IsProtected(string rel) =>
        IsBridge(rel) || _exclude.Any(x => x.StartsWith(rel + "/", StringComparison.OrdinalIgnoreCase));

    public Dictionary<string, T> Filter<T>(Dictionary<string, T> items, Func<T, bool> isDir)
    {
        if (IsEverything) return items;
        var result = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in items)
            if (Contains(k, isDir(v))) result[k] = v;
        return result;
    }
}
