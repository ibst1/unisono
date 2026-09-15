namespace Unisono;

public sealed class LocalItem
{
    public string RelPath { get; init; } = "";
    public string FullPath { get; init; } = "";
    public bool IsDir { get; init; }
    public long Size { get; init; }
    public long MtimeTicks { get; init; }
}

/// <summary>
/// Full stat walk of the local root. Cheap enough to run every cycle for a few thousand files, and it makes the
/// FileSystemWatcher a mere trigger: nothing depends on catching every event.
/// </summary>
public static class LocalScanner
{
    public const string PartSuffix = ".unisono-part";

    public static bool IsIgnored(string name) =>
        name.EndsWith(PartSuffix, StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("~$", StringComparison.Ordinal) ||            // Office lock files
        name.StartsWith(".~lock.", StringComparison.Ordinal) ||       // LibreOffice lock files
        name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase);

    public static Dictionary<string, LocalItem> Scan(string root, Log log)
    {
        var items = new Dictionary<string, LocalItem>(StringComparer.OrdinalIgnoreCase);
        Walk(new DirectoryInfo(root), "", items, log);
        return items;
    }

    private static void Walk(DirectoryInfo dir, string prefix, Dictionary<string, LocalItem> items, Log log)
    {
        IEnumerable<FileSystemInfo> entries;
        try { entries = dir.EnumerateFileSystemInfos(); }
        catch (Exception ex) { log.Warn($"cannot list {dir.FullName}: {ex.Message}"); return; }

        foreach (var e in entries)
        {
            if (IsIgnored(e.Name)) continue;
            // Junctions and symlinks could loop or point outside the tree; system files are Windows' own.
            if ((e.Attributes & FileAttributes.ReparsePoint) != 0 || (e.Attributes & FileAttributes.System) != 0) continue;
            var rel = prefix + e.Name;
            if (e is DirectoryInfo d)
            {
                items[rel] = new LocalItem { RelPath = rel, FullPath = d.FullName, IsDir = true };
                Walk(d, rel + "/", items, log);
            }
            else if (e is FileInfo f)
            {
                items[rel] = new LocalItem { RelPath = rel, FullPath = f.FullName, Size = f.Length, MtimeTicks = f.LastWriteTimeUtc.Ticks };
            }
        }
    }
}
