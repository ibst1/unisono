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
