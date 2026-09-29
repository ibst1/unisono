namespace Unisono;

public sealed class LocalItem
{
    public string RelPath { get; init; } = "";
    public string FullPath { get; init; } = "";
    public bool IsDir { get; init; }
    public long Size { get; init; }
    public long MtimeTicks { get; init; }
    public FileAttributes Attributes { get; init; }

    /// <summary>A files-on-demand placeholder (Unisono's own, OneDrive's, Work Folders') whose content is not here.
    /// Reading it would download it, so nothing may hash or copy it.</summary>
    public bool Dehydrated => CloudFiles.IsDehydrated(Attributes);
    public bool Pinned => CloudFiles.IsPinned(Attributes);
    public bool Unpinned => CloudFiles.IsUnpinned(Attributes);
    public bool IsPlaceholder => (Attributes & FileAttributes.ReparsePoint) != 0;
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
        name.EndsWith(SyncEngine.ParkedSuffix, StringComparison.OrdinalIgnoreCase) || // a running program moved aside
        name.StartsWith("~$", StringComparison.Ordinal) ||            // Office lock files
        name.StartsWith(".~lock.", StringComparison.Ordinal) ||       // LibreOffice lock files
        name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase) ||
        IsOfficeTemp(name);

    /// <summary>Office's save dance: "~WRL0001.tmp", "~DF3A.tmp", and Word/Excel's "8A3F2B1C.tmp" (8 hex digits).</summary>
    private static bool IsOfficeTemp(string name)
    {
        if (!name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) return false;
        if (name.StartsWith('~')) return true;
        return name.Length == 12 && name[..8].All(Uri.IsHexDigit);
    }

    /// <summary>Written to within the last 2 s: probably still being saved, so not uploaded until it has settled.</summary>
    public static bool Unsettled(LocalItem l) => !l.IsDir && l.MtimeTicks > DateTime.UtcNow.AddSeconds(-2).Ticks;

    /// <summary>
    /// Junctions and symlinks could loop or point outside the tree; system files are Windows' own. Cloud placeholders
    /// (files on demand, OneDrive, Work Folders) are reparse points too, but ordinary content: only real links have a target.
    /// </summary>
    public static bool IsLinkOrSystem(FileSystemInfo e)
    {
        if ((e.Attributes & FileAttributes.System) != 0) return true;
        if ((e.Attributes & FileAttributes.ReparsePoint) == 0) return false;
        try { return e.LinkTarget != null; }
        catch { return true; }
    }

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
            if (IsLinkOrSystem(e)) continue;
            var rel = prefix + e.Name;
            if (e is DirectoryInfo d)
            {
                items[rel] = new LocalItem { RelPath = rel, FullPath = d.FullName, IsDir = true, Attributes = d.Attributes };
                Walk(d, rel + "/", items, log);
            }
            else if (e is FileInfo f)
            {
                items[rel] = new LocalItem { RelPath = rel, FullPath = f.FullName, Size = f.Length, MtimeTicks = f.LastWriteTimeUtc.Ticks, Attributes = f.Attributes };
            }
        }
    }
}
