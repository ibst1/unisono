namespace Unisono;

/// <summary>
/// Local version history: before Unisono overwrites a local file with the remote version, the local one is copied to
/// versions\&lt;pair&gt;\&lt;relative path&gt;\&lt;yyyy-MM-dd HHmmss&gt;.&lt;ext&gt;, and old copies are pruned by count and age.
/// Deletions do not need this: they already go to the Recycle Bin, and conflicts keep both files.
/// </summary>
public static class VersionStore
{
    public static string Root => Path.Combine(AppPaths.DataDir, "versions");

    public static string DirFor(SyncPair pair) => Path.Combine(Root, pair.Id);

    /// <summary>Returns the copy's path, or null when versioning is off or the file is gone.</summary>
    public static string? Save(SyncPair pair, string rel, string fullPath, AppOptions options)
    {
        if (options.KeepVersions <= 0 || !File.Exists(fullPath)) return null;
        var dir = Path.Combine(DirFor(pair), rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, $"{File.GetLastWriteTime(fullPath):yyyy-MM-dd HHmmss}{Path.GetExtension(rel)}");
        if (!File.Exists(target))
        {
            // Shared read: Office keeps its documents open, and a plain File.Copy would fail on them.
            using (var src = PathUtil.OpenShared(fullPath))
            using (var dst = File.Create(target))
                src.CopyTo(dst);
            File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(fullPath));
        }
        Prune(dir, options);
        return target;
    }

    /// <summary>One kept copy of a file.</summary>
    public sealed record Version(string Rel, string CopyPath, DateTime Taken, long Size);

    /// <summary>Every kept copy of the pair, grouped by the file it belongs to, newest first.</summary>
    public static List<(string Rel, List<Version> Versions)> List(SyncPair pair)
    {
        var root = DirFor(pair);
        if (!Directory.Exists(root)) return new();
        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(f => new FileInfo(f))
            .Select(f => new Version(Path.GetRelativePath(root, f.DirectoryName!).Replace('\\', '/'), f.FullName, f.CreationTime, f.Length))
            .GroupBy(v => v.Rel, StringComparer.OrdinalIgnoreCase)
            .Select(g => (g.Key, g.OrderByDescending(v => Path.GetFileName(v.CopyPath), StringComparer.Ordinal).ToList()))
            .OrderByDescending(x => x.Item2[0].Taken)
            .ToList();
    }

    /// <summary>Puts a kept copy back in place. The file it replaces is kept as a version first, so this can be undone too.</summary>
    public static void Restore(SyncPair pair, string rel, string copyPath, AppOptions options)
    {
        var root = Path.GetFullPath(DirFor(pair)) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(copyPath).StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("That is not one of this pair's kept versions.");
        var target = Path.Combine(pair.LocalFullPath, rel.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(target))
        {
            Save(pair, rel, target, new AppOptions { KeepVersions = Math.Max(options.KeepVersions, 1), KeepVersionsDays = options.KeepVersionsDays });
            File.SetAttributes(target, FileAttributes.Normal);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(copyPath, target, overwrite: true);
        File.SetLastWriteTime(target, DateTime.Now); // the newest state now: the next sync uploads it
    }

    private static void Prune(string dir, AppOptions options)
    {
        var files = new DirectoryInfo(dir).GetFiles().OrderByDescending(f => f.Name, StringComparer.Ordinal).ToList();
        var cutoff = options.KeepVersionsDays > 0 ? DateTime.Now.AddDays(-options.KeepVersionsDays) : DateTime.MinValue;
        for (var i = 0; i < files.Count; i++)
        {
            // The newest copy survives the age limit, so a file edited long ago still has one version.
            if (i >= options.KeepVersions || (i > 0 && files[i].CreationTime < cutoff))
            {
                try { files[i].Attributes = FileAttributes.Normal; files[i].Delete(); } catch { }
            }
        }
    }
}
