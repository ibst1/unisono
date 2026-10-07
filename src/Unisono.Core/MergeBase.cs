namespace Unisono;

/// <summary>
/// The last synced content of each mergeable text file (see <see cref="TextMerge"/>), under base\&lt;pair&gt;\&lt;path&gt;:
/// the common ancestor a three-way merge needs when the file then changes on both sides. Only the md5 is in the state
/// file, which is how a kept copy is checked before use; a copy that does not match is never used.
/// A conflict copy made when the edits overlapped gets the ancestor too (base\&lt;pair&gt;.ancestors\&lt;copy path&gt;), so
/// the Conflicts window can merge it later.
/// </summary>
public static class MergeBase
{
    private static string Root => Path.Combine(AppPaths.DataDir, "base");
    private static string DirFor(SyncPair pair) => Path.Combine(Root, pair.Id);
    private static string AncestorDirFor(SyncPair pair) => Path.Combine(Root, pair.Id + ".ancestors");
    private static string Under(string dir, string rel) => Path.Combine(dir, rel.Replace('/', Path.DirectorySeparatorChar));

    public static bool Wanted(string rel, long size) => TextMerge.Eligible(rel) && size <= TextMerge.MaxBytes;

    public static bool Has(SyncPair pair, string rel) => File.Exists(Under(DirFor(pair), rel));

    /// <summary>Keeps the file's current content as the pair's base for <paramref name="rel"/>.</summary>
    public static void Save(SyncPair pair, string rel, byte[] content) => Write(Under(DirFor(pair), rel), content);

    /// <summary>The kept base when its md5 is <paramref name="md5"/>; null otherwise.</summary>
    public static byte[]? Read(SyncPair pair, string rel, string md5)
    {
        try
        {
            var path = Under(DirFor(pair), rel);
            if (!File.Exists(path)) return null;
            var bytes = File.ReadAllBytes(path);
            return string.Equals(TextMerge.Md5Hex(bytes), md5, StringComparison.OrdinalIgnoreCase) ? bytes : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public static void SaveAncestor(SyncPair pair, string copyRel, byte[] content) => Write(Under(AncestorDirFor(pair), copyRel), content);

    public static byte[]? Ancestor(SyncPair pair, string copyRel)
    {
        var path = Under(AncestorDirFor(pair), copyRel);
        try { return File.Exists(path) ? File.ReadAllBytes(path) : null; }
        catch (IOException) { return null; }
    }

    public static void DropAncestor(SyncPair pair, string copyRel)
    {
        try { File.Delete(Under(AncestorDirFor(pair), copyRel)); } catch { /* left for the next prune */ }
    }

    /// <summary>Removes kept bases and ancestors of files the pair no longer has, and empty folders.</summary>
    public static void Prune(SyncPair pair, ICollection<string> liveRels)
    {
        foreach (var dir in new[] { DirFor(pair), AncestorDirFor(pair) })
        {
            if (!Directory.Exists(dir)) continue;
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).ToList())
                    if (!liveRels.Contains(Path.GetRelativePath(dir, f).Replace(Path.DirectorySeparatorChar, '/')))
                        File.Delete(f);
                foreach (var d in Directory.EnumerateDirectories(dir, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length).ToList())
                    if (!Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Forgets everything kept for a pair (the pair was removed).</summary>
    public static void Forget(SyncPair pair)
    {
        foreach (var dir in new[] { DirFor(pair), AncestorDirFor(pair) })
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
    }

    private static void Write(string path, byte[] content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }
}
