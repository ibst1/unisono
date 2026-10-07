using System.Security.Cryptography;

namespace Unisono;

/// <summary>Relative paths are '/'-separated and named as on the remote side; these helpers keep that in one place.</summary>
public static class PathUtil
{
    public static string ParentOf(string rel) { var i = rel.LastIndexOf('/'); return i < 0 ? "" : rel[..i]; }
    public static string NameOf(string rel) { var i = rel.LastIndexOf('/'); return i < 0 ? rel : rel[(i + 1)..]; }
    public static int Depth(string rel) => rel.Count(c => c == '/');
    public static string Join(string parentRel, string name) => parentRel.Length == 0 ? name : parentRel + "/" + name;

    public static string Md5Of(string localPath)
    {
        using var md5 = MD5.Create();
        using var fs = OpenShared(localPath);
        return Convert.ToHexString(md5.ComputeHash(fs)).ToLowerInvariant();
    }

    /// <summary>
    /// "name (2).ext", "name (3).ext"... (folders: "name (2)"), the first one <paramref name="taken"/> says is free:
    /// how a second remote item with a name Windows can only hold once (Drive's duplicates, a NAS's case-only twins)
    /// is shown here.
    /// </summary>
    public static string FreeName(string name, bool isFolder, Func<string, bool> taken)
    {
        var ext = isFolder ? "" : Path.GetExtension(name);
        var stem = name[..^ext.Length];
        for (var n = 2; ; n++)
        {
            var candidate = $"{stem} ({n}){ext}";
            if (!taken(candidate)) return candidate;
        }
    }

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// Why Windows cannot hold this name (one path segment), or null. Box, Drive and a NAS allow names such as
    /// "Canvas 14:28:23" or "draft?" that no Windows folder can.
    /// </summary>
    public static string? WindowsProblem(string name)
    {
        foreach (var c in name)
        {
            if (c < 32) return "a control character";
            if ("<>:\"\\|?*".IndexOf(c) >= 0) return $"'{c}'";
        }
        if (name.EndsWith('.') || name.EndsWith(' ')) return "a dot or a space at the end";
        var stem = name.Split('.')[0].TrimEnd(' ');
        return Reserved.Contains(stem) ? $"the reserved name {stem}" : null;
    }

    /// <summary>Read even while another program has the file open for writing (Office keeps its documents open).</summary>
    public static FileStream OpenShared(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
}
