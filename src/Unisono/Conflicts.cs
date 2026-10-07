namespace Unisono.App;

/// <summary>
/// The conflict copies in the pairs' local folders, for the Conflicts window. When a file changed on both sides the
/// engine merges text files whose edits touch different lines; otherwise it keeps both: the remote version under the
/// original name and the local one as "name (conflict COMPUTER yyyy-MM-dd HHmm).ext"; a Word edit of a Google document
/// that could not go back into the document is kept as "name (edited COMPUTER ...).docx". Both kinds sync like any
/// file, so every computer sees them.
/// </summary>
static class Conflicts
{
    /// <param name="CanMerge">A text copy whose common ancestor with the original was kept on this computer.</param>
    public sealed record Copy(string PairId, string PairName, string Rel, string OriginalRel, string Kind, string Machine, string When,
        long Size, DateTime Modified, bool OriginalExists, long OriginalSize, DateTime? OriginalModified, bool IsText, bool CanMerge);

    public static List<Copy> Find(IEnumerable<SyncPair> pairs)
    {
        var found = new List<Copy>();
        foreach (var pair in pairs.Where(p => p.LocalPath.Length > 0 && Directory.Exists(p.LocalFullPath)))
        {
            var root = pair.LocalFullPath.TrimEnd('\\');
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(root, "*(*)*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }); }
            catch { continue; }
            foreach (var file in files)
            {
                var m = ConflictCopy.Name.Match(Path.GetFileName(file));
                if (!m.Success) continue;
                var original = Path.Combine(Path.GetDirectoryName(file)!, m.Groups["stem"].Value + m.Groups["ext"].Value);
                var info = new FileInfo(file);
                var orig = new FileInfo(original);
                var rel = Rel(root, file);
                var isText = TextMerge.Eligible(file) && info.Length <= TextMerge.MaxBytes && (!orig.Exists || orig.Length <= TextMerge.MaxBytes);
                found.Add(new Copy(pair.Id, pair.Name, rel, Rel(root, original), m.Groups["kind"].Value.ToLowerInvariant(),
                    m.Groups["machine"].Value, m.Groups["when"].Value, info.Length, info.LastWriteTime,
                    orig.Exists, orig.Exists ? orig.Length : 0, orig.Exists ? orig.LastWriteTime : null,
                    isText, isText && orig.Exists && MergeBase.Ancestor(pair, rel) != null));
            }
        }
        return found.OrderByDescending(c => c.When).ToList();
    }

    private static string Rel(string root, string full) => full[(root.Length + 1)..].Replace('\\', '/');

    private static (string Copy, string Original) Paths(SyncPair pair, string copyRel)
    {
        var root = pair.LocalFullPath.TrimEnd('\\');
        var copy = Path.GetFullPath(Path.Combine(root, copyRel.Replace('/', '\\')));
        var m = ConflictCopy.Name.Match(Path.GetFileName(copy));
        if (!copy.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase) || !m.Success || !File.Exists(copy))
            throw new InvalidOperationException("That conflict copy is no longer there.");
        return (copy, Path.Combine(Path.GetDirectoryName(copy)!, m.Groups["stem"].Value + m.Groups["ext"].Value));
    }

    /// <summary>What the copy has that the current version does not ('-') and the other way round ('+'), line by line.</summary>
    public static List<TextMerge.DiffLine> Diff(SyncPair pair, string copyRel)
    {
        var (copy, original) = Paths(pair, copyRel);
        if (!File.Exists(original)) throw new InvalidOperationException("The original is gone; only the copy is left.");
        return TextMerge.Diff(File.ReadAllBytes(copy), File.ReadAllBytes(original))
               ?? throw new InvalidOperationException("These files are not plain text, or too different to compare line by line.");
    }

    /// <summary>
    /// keepCopy: the copy's content replaces the original (whose content is kept among the pair's earlier versions)
    /// and the copy goes to the Recycle Bin. keepOriginal: only the copy goes. merge: both sides' edits go into the
    /// original (kept among the earlier versions first) and the copy goes; where both changed the same lines, both
    /// versions of them are written between "&lt;&lt;&lt;&lt;&lt;&lt;&lt;" and "&gt;&gt;&gt;&gt;&gt;&gt;&gt;" lines to sort out by hand.
    /// The next cycle carries it all over.
    /// </summary>
    public static string Resolve(SyncPair pair, string copyRel, string action, AppOptions options)
    {
        var root = pair.LocalFullPath.TrimEnd('\\');
        var (copy, original) = Paths(pair, copyRel);
        if (action == "merge")
        {
            var ancestor = MergeBase.Ancestor(pair, copyRel) ?? throw new InvalidOperationException("The common earlier version of these two is not known on this computer.");
            if (!File.Exists(original)) throw new InvalidOperationException("The original is gone; only the copy is left.");
            var machine = ConflictCopy.Name.Match(Path.GetFileName(copy)).Groups["machine"].Value;
            var outcome = TextMerge.Merge(ancestor, File.ReadAllBytes(original), File.ReadAllBytes(copy), ("current version", $"conflict copy from {machine}"))
                          ?? throw new InvalidOperationException("These files are not plain text, or too different to merge line by line.");
            VersionStore.Save(pair, Rel(root, original), original, options);
            File.WriteAllBytes(original, outcome.Bytes!);
            RecycleBin.Delete(copy);
            MergeBase.DropAncestor(pair, copyRel);
            return outcome.Overlaps == 0
                ? $"merged the conflict copy into {Rel(root, original)}"
                : $"merged the conflict copy into {Rel(root, original)}; {outcome.Overlaps} place(s) changed on both sides are marked with <<<<<<< and >>>>>>>";
        }
        if (action == "keepCopy")
        {
            if (File.Exists(original))
            {
                VersionStore.Save(pair, Rel(root, original), original, options);
                File.Copy(copy, original, overwrite: true);
            }
            else File.Copy(copy, original);
            RecycleBin.Delete(copy);
            MergeBase.DropAncestor(pair, copyRel);
            return $"kept the conflict copy's content as {Rel(root, original)}";
        }
        MergeBase.DropAncestor(pair, copyRel);
        return $"removed the conflict copy {copyRel}: {RecycleBin.Delete(copy)}";
    }
}
