namespace Unisono;

/// <summary>
/// The .unisonoignore at the top of a pair's local folder, which the pair dialog's Ignore field shows and edits and
/// "Ignore it" / "Don't sync this" add to. It syncs with the folder, so its patterns apply on every computer.
/// </summary>
public static class IgnoreFile
{
    public static string PathFor(string localFullPath) => Path.Combine(localFullPath, IgnoreRules.FileName);

    /// <summary>The file's lines joined with "\n"; empty when there is no file.</summary>
    public static string Read(string localFullPath)
    {
        var file = PathFor(localFullPath);
        return File.Exists(file) ? File.ReadAllText(file).Replace("\r\n", "\n").Trim('\n') : "";
    }

    /// <summary>Writes the patterns (one per line); an empty text removes the file (to the Recycle Bin). Null when nothing changed.</summary>
    public static string? Write(string localFullPath, string text)
    {
        var file = PathFor(localFullPath);
        var wanted = text.Replace("\r\n", "\n").Trim('\n');
        if (wanted == Read(localFullPath)) return null;
        if (wanted.Trim().Length == 0)
            return File.Exists(file) ? $"removed {file} (no ignore patterns left): {RecycleBin.Delete(file)}" : null;
        File.WriteAllText(file, wanted.Replace("\n", "\r\n") + "\r\n", new System.Text.UTF8Encoding(false));
        return $"wrote ignore patterns to {file}";
    }

    /// <summary>
    /// Adds a pattern for exactly this item (relative to the pair folder): anchored with a leading '/', a trailing '/'
    /// for a folder, and '[' written as a class so it is not read as one. Returns the pattern.
    /// </summary>
    public static string AddItem(string localFullPath, string rel, bool isDir)
    {
        var pattern = "/" + rel.Replace('\\', '/').Trim('/').Replace("[", "[[]") + (isDir ? "/" : "");
        AddPatterns(localFullPath, new[] { pattern });
        return pattern;
    }

    /// <summary>Adds the patterns the file does not have yet, after a comment line when given; returns those added.</summary>
    public static List<string> AddPatterns(string localFullPath, IEnumerable<string> patterns, string? comment = null)
    {
        var current = Read(localFullPath);
        var lines = current.Length == 0 ? new List<string>() : current.Split('\n').ToList();
        var added = patterns.Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(p => !lines.Any(l => l.Trim().Equals(p, StringComparison.OrdinalIgnoreCase))).ToList();
        if (added.Count == 0) return added;
        if (comment != null) lines.Add("# " + comment);
        lines.AddRange(added);
        Write(localFullPath, string.Join("\n", lines));
        return added;
    }
}
