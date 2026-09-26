using System.Text.RegularExpressions;

namespace Unisono.App;

/// <summary>
/// The conflict copies in the pairs' local folders, for the Conflicts window. When a file changed on both sides the
/// engine keeps both: the remote version under the original name and the local one as
/// "name (conflict COMPUTER yyyy-MM-dd HHmm).ext"; a Word edit of a Google document that could not go back into the
/// document is kept as "name (edited COMPUTER ...).docx". Both kinds sync like any file, so every computer sees them.
/// </summary>
static class Conflicts
{
    private static readonly Regex CopyName = new(
        @"^(?<stem>.+) \((?<kind>conflict|edited) (?<machine>[^()]+?) (?<when>\d{4}-\d{2}-\d{2} \d{4})\)(?<ext>\.[^.]*)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public sealed record Copy(string PairId, string PairName, string Rel, string OriginalRel, string Kind, string Machine, string When,
        long Size, DateTime Modified, bool OriginalExists, long OriginalSize, DateTime? OriginalModified);

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
                var m = CopyName.Match(Path.GetFileName(file));
                if (!m.Success) continue;
                var original = Path.Combine(Path.GetDirectoryName(file)!, m.Groups["stem"].Value + m.Groups["ext"].Value);
                var info = new FileInfo(file);
                var orig = new FileInfo(original);
                found.Add(new Copy(pair.Id, pair.Name, Rel(root, file), Rel(root, original), m.Groups["kind"].Value.ToLowerInvariant(),
                    m.Groups["machine"].Value, m.Groups["when"].Value, info.Length, info.LastWriteTime,
                    orig.Exists, orig.Exists ? orig.Length : 0, orig.Exists ? orig.LastWriteTime : null));
            }
        }
        return found.OrderByDescending(c => c.When).ToList();
    }

    private static string Rel(string root, string full) => full[(root.Length + 1)..].Replace('\\', '/');

    /// <summary>
    /// keepCopy: the copy's content replaces the original (whose content is kept among the pair's earlier versions)
    /// and the copy goes to the Recycle Bin. keepOriginal: only the copy goes. The next cycle carries both over.
    /// </summary>
    public static string Resolve(SyncPair pair, string copyRel, string action, AppOptions options)
    {
        var root = pair.LocalFullPath.TrimEnd('\\');
        var copy = Path.Combine(root, copyRel.Replace('/', '\\'));
        var m = CopyName.Match(Path.GetFileName(copy));
        if (!m.Success || !File.Exists(copy)) throw new InvalidOperationException("That conflict copy is no longer there.");
        var original = Path.Combine(Path.GetDirectoryName(copy)!, m.Groups["stem"].Value + m.Groups["ext"].Value);
        if (action == "keepCopy")
        {
            if (File.Exists(original))
            {
                VersionStore.Save(pair, Rel(root, original), original, options);
                File.Copy(copy, original, overwrite: true);
            }
            else File.Copy(copy, original);
            RecycleBin.Delete(copy);
            return $"kept the conflict copy's content as {Rel(root, original)}";
        }
        return $"removed the conflict copy {copyRel}: {RecycleBin.Delete(copy)}";
    }
}
