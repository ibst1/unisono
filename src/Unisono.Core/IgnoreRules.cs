using System.Text;
using System.Text.RegularExpressions;

namespace Unisono;

/// <summary>
/// gitignore-style ignore rules: global patterns, the pair's patterns and every .unisonoignore file in the local folder
/// (its patterns apply to its own folder and below). Supported: '#' comments, '!' negation, trailing '/' for folders
/// only, leading or inner '/' to anchor to the file's folder, '*', '?', '**' and [classes]. Matching ignores case, as
/// Windows does. As in git, nothing inside an ignored folder can be re-included.
/// Ignored items are invisible to syncing on both sides, like items outside a pair's folder selection.
/// </summary>
public sealed class IgnoreRules
{
    public const string FileName = ".unisonoignore";

    private sealed record Rule(string Base, Regex Re, bool Negate, bool DirOnly, bool NameOnly, string Source);
    private readonly List<Rule> _rules = new();

    public bool IsEmpty => _rules.Count == 0;

    public static IgnoreRules Build(AppOptions options, SyncPair pair, string root, IEnumerable<string> ignoreFileRels, Log? log = null)
    {
        var rules = new IgnoreRules();
        foreach (var p in options.IgnorePatterns) rules.Add("", p, "settings");
        foreach (var p in pair.IgnorePatterns) rules.Add("", p, "pair");
        // Shallow files first, so deeper ones override (the last matching rule wins).
        foreach (var rel in ignoreFileRels.OrderBy(PathUtil.Depth))
        {
            try
            {
                foreach (var line in File.ReadAllLines(Path.Combine(root, rel.Replace('/', '\\'))))
                    rules.Add(PathUtil.ParentOf(rel), line, rel);
            }
            catch (Exception ex) { log?.Warn($"[{pair.Name}] cannot read {rel}: {ex.Message}"); }
        }
        return rules;
    }

    public void Add(string baseDir, string line, string source)
    {
        var p = line.TrimEnd();
        if (p.Length == 0 || p.StartsWith('#')) return;
        var negate = p.StartsWith('!');
        if (negate) p = p[1..];
        if (p.StartsWith("\\#") || p.StartsWith("\\!")) p = p[1..];
        var dirOnly = p.EndsWith('/');
        p = p.TrimEnd('/');
        if (p.Length == 0) return;
        var nameOnly = !p.Contains('/');
        p = p.TrimStart('/');
        _rules.Add(new Rule(baseDir, new Regex("^" + ToRegex(p) + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), negate, dirOnly, nameOnly, source));
    }

    private static string ToRegex(string glob)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            if (c == '*' && i + 1 < glob.Length && glob[i + 1] == '*')
            {
                var slashAfter = i + 2 < glob.Length && glob[i + 2] == '/';
                sb.Append(slashAfter ? "(?:.*/)?" : ".*");
                i += slashAfter ? 2 : 1;
            }
            else if (c == '*') sb.Append("[^/]*");
            else if (c == '?') sb.Append("[^/]");
            else if (c == '[')
            {
                var end = glob.IndexOf(']', i + 1);
                if (end < 0) { sb.Append("\\["); continue; }
                var cls = glob[(i + 1)..end];
                if (cls.StartsWith('!')) cls = "^" + cls[1..];
                sb.Append('[').Append(cls.Replace("\\", "\\\\")).Append(']');
                i = end;
            }
            else sb.Append(Regex.Escape(c.ToString()));
        }
        return sb.ToString();
    }

    /// <summary>True when the path, or any folder above it, is ignored.</summary>
    public bool IsIgnored(string rel, bool isDir)
    {
        if (_rules.Count == 0) return false;
        var parts = rel.Split('/');
        for (var i = 1; i < parts.Length; i++)
            if (Match(string.Join('/', parts[..i]), isDir: true)) return true;
        return Match(rel, isDir);
    }

    private bool Match(string rel, bool isDir)
    {
        var ignored = false;
        foreach (var r in _rules)
        {
            if (r.DirOnly && !isDir) continue;
            string sub;
            if (r.Base.Length == 0) sub = rel;
            else if (rel.StartsWith(r.Base + "/", StringComparison.OrdinalIgnoreCase)) sub = rel[(r.Base.Length + 1)..];
            else continue;
            var target = r.NameOnly ? PathUtil.NameOf(sub) : sub;
            if (r.Re.IsMatch(target)) ignored = !r.Negate;
        }
        return ignored;
    }
}
