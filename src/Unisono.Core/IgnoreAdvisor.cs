namespace Unisono;

/// <summary>One pattern Unisono suggests adding to the pair's .unisonoignore, with what it would leave out.</summary>
public sealed record SuggestedPattern(string Pattern, int Files, long Bytes, string Example, string Note, bool Recommended);

/// <summary>A group of suggested patterns with one reason: build output, a .gitignore, temporary files, bulk churn.</summary>
public sealed record IgnoreSuggestion(string Kind, string Title, List<SuggestedPattern> Patterns);

/// <summary>
/// Finds synced items that are probably better ignored, without asking anyone else: build output next to the project
/// file that rebuilds it, what the folder's own .gitignore says is not source, well-known temporary and cache files,
/// and folders whose files are created and deleted in bulk. Only suggests; the user picks what goes into .unisonoignore.
/// Patterns are written for the .unisonoignore at the top of the pair, anchored to the folders found.
/// </summary>
public sealed class IgnoreAdvisor
{
    // Project files and the folders their builds write, next to them.
    private static readonly (string File, string[] Outputs)[] Projects =
    {
        ("*.csproj", new[] { "bin", "obj" }), ("*.vbproj", new[] { "bin", "obj" }), ("*.fsproj", new[] { "bin", "obj" }),
        ("*.vcxproj", new[] { "x64", "Debug", "Release" }),
        ("package.json", new[] { "node_modules" }),
        ("Cargo.toml", new[] { "target" }), ("pom.xml", new[] { "target" }),
        ("build.gradle", new[] { "build", ".gradle" }), ("build.gradle.kts", new[] { "build", ".gradle" }),
        ("CMakeLists.txt", new[] { "build" }),
    };

    // Temporary, cache and log files wherever they are (LocalScanner already skips Office locks and Thumbs.db).
    private static readonly (string Pattern, string Note)[] Junk =
    {
        ("__pycache__/", "Python's compiled cache"), (".pytest_cache/", "pytest's cache"), (".mypy_cache/", "mypy's cache"),
        (".ruff_cache/", "ruff's cache"), (".vs/", "Visual Studio's per-computer state"), ("*.tmp", "temporary files"),
        ("*.temp", "temporary files"), ("*.log", "log files"), (".DS_Store", "macOS folder settings"), ("*.swp", "editor swap files"),
        ("*.crdownload", "unfinished browser downloads"), ("*.part", "unfinished downloads"),
    };

    // Names a .gitignore line may use for things that can be rebuilt or thrown away: those are ticked in advance.
    // Not "dist": in many repos it is the finished program on its way to other computers.
    private static readonly HashSet<string> Disposable = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "build", "out", "target", "node_modules", "__pycache__", ".vs", ".gradle", ".pytest_cache",
        ".mypy_cache", ".ruff_cache", ".venv", "venv", "coverage", ".cache", "*.log", "*.tmp", "*.pyc", "*.pyo", "*.class",
        "*.o", "*.obj", "*.cache", "*.pdb", "*.user", "*.suo", "x64", "Debug", "Release",
    };

    /// <summary>Churn over this window, per folder, decides "created and deleted in bulk".</summary>
    public static readonly TimeSpan ChurnWindow = TimeSpan.FromHours(1);
    public const int ChurnMin = 300, ChurnEachWay = 50;

    private HashSet<string>? _lastFiles;
    private readonly Dictionary<string, List<(DateTime At, int Added, int Removed)>> _churn = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SuggestedPattern> _churnFound = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Notes which files came and went since the last scan (for the bulk-churn suggestion). Call on every local scan
    /// with the visible items; cheap.
    /// </summary>
    public void Observe(IReadOnlyDictionary<string, LocalItem> visible)
    {
        var files = new HashSet<string>(visible.Values.Where(i => !i.IsDir).Select(i => i.RelPath), StringComparer.OrdinalIgnoreCase);
        var last = _lastFiles;
        _lastFiles = files;
        if (last == null) return;
        var now = DateTime.UtcNow;
        var tally = new Dictionary<string, (int Added, int Removed)>(StringComparer.OrdinalIgnoreCase);
        void Count(string rel, bool added)
        {
            for (var dir = PathUtil.ParentOf(rel); dir.Length > 0; dir = PathUtil.ParentOf(dir))
            {
                tally.TryGetValue(dir, out var t);
                tally[dir] = added ? (t.Added + 1, t.Removed) : (t.Added, t.Removed + 1);
            }
        }
        foreach (var f in files) if (!last.Contains(f)) Count(f, true);
        foreach (var f in last) if (!files.Contains(f)) Count(f, false);
        foreach (var (dir, t) in tally)
        {
            if (!_churn.TryGetValue(dir, out var list)) _churn[dir] = list = new();
            list.Add((now, t.Added, t.Removed));
        }
        foreach (var dir in _churn.Keys.ToList())
        {
            _churn[dir].RemoveAll(x => now - x.At > ChurnWindow);
            if (_churn[dir].Count == 0) _churn.Remove(dir);
        }
    }

    /// <summary>The suggestions for the pair's visible items (ignored and out-of-scope items already left out).</summary>
    public List<IgnoreSuggestion> Suggest(string root, IReadOnlyDictionary<string, LocalItem> visible, IEnumerable<string> dismissed, Log? log = null)
    {
        var skip = new HashSet<string>(dismissed, StringComparer.OrdinalIgnoreCase);
        // A git repository's own folder is its history, not files to judge: nothing in it is suggested.
        var items = visible.Values.Where(i => !InGitDir(i.RelPath)).ToList();
        var dirs = new HashSet<string>(items.Where(i => i.IsDir).Select(i => i.RelPath), StringComparer.OrdinalIgnoreCase);
        var files = items.Where(i => !i.IsDir).ToList();
        // Files and bytes below every folder.
        var below = new Dictionary<string, (int Files, long Bytes)>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in files)
            for (var dir = PathUtil.ParentOf(f.RelPath); dir.Length > 0; dir = PathUtil.ParentOf(dir))
            {
                below.TryGetValue(dir, out var b);
                below[dir] = (b.Files + 1, b.Bytes + f.Size);
            }
        (int Files, long Bytes) Below(string dir) => below.TryGetValue(dir, out var b) ? b : (0, 0);

        var result = new List<IgnoreSuggestion>();
        var covered = new List<string>(); // folders already suggested: later groups leave what is inside them alone
        bool Covered(string rel) => covered.Any(c => rel.Equals(c, StringComparison.OrdinalIgnoreCase) || rel.StartsWith(c + "/", StringComparison.OrdinalIgnoreCase));

        // 1. Build output next to the project file that makes it.
        var build = new List<SuggestedPattern>();
        foreach (var f in files)
        {
            var name = PathUtil.NameOf(f.RelPath);
            foreach (var (glob, outputs) in Projects)
            {
                if (!Glob(glob, name)) continue;
                var dir = PathUtil.ParentOf(f.RelPath);
                foreach (var o in outputs)
                {
                    var rel = dir.Length == 0 ? o : dir + "/" + o;
                    var pattern = Anchored(rel, isDir: true);
                    if (!dirs.Contains(rel) || Covered(rel) || skip.Contains(pattern)) continue;
                    var (n, bytes) = Below(rel);
                    if (n == 0) continue;
                    covered.Add(rel);
                    build.Add(new SuggestedPattern(pattern, n, bytes, rel, $"rebuilt from {name}", true));
                }
            }
        }
        // Python virtual environments: a folder holding pyvenv.cfg.
        foreach (var f in files.Where(f => PathUtil.NameOf(f.RelPath).Equals("pyvenv.cfg", StringComparison.OrdinalIgnoreCase)))
        {
            var rel = PathUtil.ParentOf(f.RelPath);
            var pattern = Anchored(rel, isDir: true);
            if (rel.Length == 0 || Covered(rel) || skip.Contains(pattern)) continue;
            var (n, bytes) = Below(rel);
            covered.Add(rel);
            build.Add(new SuggestedPattern(pattern, n, bytes, rel, "a Python virtual environment (recreated with pip)", true));
        }
        if (build.Count > 0) result.Add(new IgnoreSuggestion("build", "Build output that can be rebuilt", build));

        // 2. What each folder's .gitignore says is not source (groups with something ticked first).
        var gitGroups = new List<IgnoreSuggestion>();
        foreach (var gi in files.Where(f => PathUtil.NameOf(f.RelPath).Equals(".gitignore", StringComparison.OrdinalIgnoreCase)).OrderBy(f => f.RelPath))
        {
            var dir = PathUtil.ParentOf(gi.RelPath);
            string[] lines;
            try { lines = File.ReadAllLines(Path.Combine(root, gi.RelPath.Replace('/', '\\'))); }
            catch (Exception ex) { log?.Warn($"cannot read {gi.RelPath}: {ex.Message}"); continue; }
            var found = new List<SuggestedPattern>();
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#') || line.StartsWith('!')) continue;
                var pattern = Translate(dir, line);
                if (pattern == null || skip.Contains(pattern)) continue;
                var one = new IgnoreRules();
                one.Add("", pattern, gi.RelPath);
                var inDir = dir.Length == 0 ? files : files.Where(f => f.RelPath.StartsWith(dir + "/", StringComparison.OrdinalIgnoreCase));
                var hits = inDir.Where(f => !Covered(f.RelPath) && one.IsIgnored(f.RelPath, false)).ToList();
                if (hits.Count == 0) continue;
                var example = hits.OrderBy(h => PathUtil.Depth(h.RelPath)).First().RelPath;
                var core = line.Trim('/').Replace("**/", "");
                var tick = Disposable.Contains(core);
                found.Add(new SuggestedPattern(pattern, hits.Count, hits.Sum(h => h.Size), example,
                    tick ? $"'{line}' in the .gitignore" : $"'{line}' in the .gitignore; git leaves it out, but it may be something you want on every computer (settings, keys, finished programs)", tick));
                foreach (var d in dirs.Where(d => (dir.Length == 0 || d.StartsWith(dir + "/", StringComparison.OrdinalIgnoreCase)) && !Covered(d) && one.IsIgnored(d, true)))
                    covered.Add(d);
            }
            if (found.Count > 0)
                gitGroups.Add(new IgnoreSuggestion("gitignore:" + gi.RelPath, $"Not source according to {gi.RelPath}", found));
        }

        result.AddRange(gitGroups.OrderBy(g => g.Patterns.Any(p => p.Recommended) ? 0 : 1));

        // 3a. Temporary, cache and log files anywhere.
        var junk = new List<SuggestedPattern>();
        foreach (var (pattern, note) in Junk)
        {
            if (skip.Contains(pattern)) continue;
            var one = new IgnoreRules();
            one.Add("", pattern, "advisor");
            var hits = files.Where(f => !Covered(f.RelPath) && one.IsIgnored(f.RelPath, false)).ToList();
            if (hits.Count == 0) continue;
            junk.Add(new SuggestedPattern(pattern, hits.Count, hits.Sum(h => h.Size), hits.OrderBy(h => PathUtil.Depth(h.RelPath)).First().RelPath, note, true));
        }
        if (junk.Count > 0) result.Add(new IgnoreSuggestion("junk", "Temporary, cache and log files", junk));

        // 3b. Folders whose files come and go in bulk (tool output the rules above do not know).
        foreach (var dir in FindChurn())
        {
            var pattern = Anchored(dir, isDir: true);
            if (!dirs.Contains(dir) || Covered(dir) || skip.Contains(pattern)) { _churnFound.Remove(dir); continue; }
            if (!_churnFound.ContainsKey(dir))
            {
                var (added, removed) = ChurnOf(dir);
                var (n, bytes) = Below(dir);
                _churnFound[dir] = new SuggestedPattern(pattern, n, bytes, dir, $"{added} files created and {removed} deleted within an hour", false);
            }
        }
        foreach (var dir in _churnFound.Keys.ToList())
            if (!dirs.Contains(dir) || Covered(dir) || skip.Contains(_churnFound[dir].Pattern)) _churnFound.Remove(dir);
        if (_churnFound.Count > 0) result.Add(new IgnoreSuggestion("churn", "Files created and deleted in bulk", _churnFound.Values.ToList()));
        return KeepPrograms(result, files);
    }

    // Used only to build other programs: not what "keep every program" protects.
    private static readonly HashSet<string> BuildTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "apphost.exe", "singlefilehost.exe", "createdump.exe", "testhost.exe", "testhost.x86.exe", "Ahk2Exe.exe",
        "AutoHotkey32.exe", "AutoHotkey64.exe", "AutoHotkeyA32.exe", "AutoHotkeyU32.exe", "AutoHotkeyU64.exe",
    };

    /// <summary>
    /// Runnable programs always sync, so they need not be rebuilt on every computer: a pattern that would hide the only
    /// copy of a program (an .exe no other synced place has under the same name) is not ticked in advance.
    /// </summary>
    private static List<IgnoreSuggestion> KeepPrograms(List<IgnoreSuggestion> groups, List<LocalItem> files)
    {
        var exes = files.Where(f => f.RelPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !BuildTools.Contains(PathUtil.NameOf(f.RelPath))).ToList();
        if (exes.Count == 0) return groups;
        var ticked = new IgnoreRules();
        foreach (var p in groups.SelectMany(g => g.Patterns).Where(p => p.Recommended)) ticked.Add("", p.Pattern, "advisor");
        var hidden = exes.Where(e => ticked.IsIgnored(e.RelPath, false)).ToList();
        var onlyCopies = hidden.Where(h => !exes.Any(e => e != h && PathUtil.NameOf(e.RelPath).Equals(PathUtil.NameOf(h.RelPath), StringComparison.OrdinalIgnoreCase)
            && !ticked.IsIgnored(e.RelPath, false))).ToList();
        if (onlyCopies.Count == 0) return groups;
        return groups.Select(g => g with
        {
            Patterns = g.Patterns.Select(p =>
            {
                if (!p.Recommended) return p;
                var one = new IgnoreRules();
                one.Add("", p.Pattern, "advisor");
                var holds = onlyCopies.Where(e => one.IsIgnored(e.RelPath, false)).Select(e => PathUtil.NameOf(e.RelPath)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                return holds.Count == 0 ? p : p with { Recommended = false, Note = $"{p.Note}; holds {string.Join(", ", holds)}, the only copy of that program here" };
            }).ToList(),
        }).ToList();
    }

    private (int Added, int Removed) ChurnOf(string dir) =>
        _churn.TryGetValue(dir, out var l) ? (l.Sum(x => x.Added), l.Sum(x => x.Removed)) : (0, 0);

    /// <summary>
    /// Folders with enough churn both ways, as precise as possible: a folder is left out when the folders reported
    /// below it already hold 80 % of its churn.
    /// </summary>
    private List<string> FindChurn()
    {
        var candidates = _churn.Keys.Select(d => (Dir: d, C: ChurnOf(d)))
            .Where(x => x.C.Added + x.C.Removed >= ChurnMin && x.C.Added >= ChurnEachWay && x.C.Removed >= ChurnEachWay)
            .OrderByDescending(x => PathUtil.Depth(x.Dir)).ToList();
        var reported = new List<(string Dir, int Total)>();
        foreach (var (dir, c) in candidates)
        {
            var total = c.Added + c.Removed;
            var inside = reported.Where(r => r.Dir.StartsWith(dir + "/", StringComparison.OrdinalIgnoreCase)).ToList();
            var topmost = inside.Where(r => !inside.Any(o => o.Dir != r.Dir && r.Dir.StartsWith(o.Dir + "/", StringComparison.OrdinalIgnoreCase)));
            if (topmost.Sum(r => r.Total) >= total * 0.8) continue;
            reported.Add((dir, total));
        }
        return reported.Select(r => r.Dir).ToList();
    }

    /// <summary>A pattern for exactly this item, anchored to the pair folder (as <see cref="IgnoreFile.AddItem"/> writes).</summary>
    public static string Anchored(string rel, bool isDir) => "/" + rel.Replace("[", "[[]") + (isDir ? "/" : "");

    /// <summary>
    /// A .gitignore line of the folder <paramref name="dir"/> as a pattern for the pair's top .unisonoignore: a bare
    /// name matches at any depth below the folder, a path is anchored to it. Null for what cannot be carried over.
    /// </summary>
    public static string? Translate(string dir, string line)
    {
        var dirOnly = line.EndsWith('/');
        var p = line.TrimEnd('/');
        if (p.StartsWith("\\#") || p.StartsWith("\\!")) p = p[1..];
        if (p.Length == 0 || p == "*" || p == "**" || p == "/") return null; // would ignore the whole folder
        var nameOnly = !p.Contains('/');
        p = p.TrimStart('/');
        var basePart = dir.Length == 0 ? "" : "/" + dir.Replace("[", "[[]");
        var body = nameOnly ? (dir.Length == 0 ? p : basePart + "/**/" + p) : basePart + "/" + p;
        return body + (dirOnly ? "/" : "");
    }

    private static bool InGitDir(string rel) =>
        rel.Equals(".git", StringComparison.OrdinalIgnoreCase) || rel.StartsWith(".git/", StringComparison.OrdinalIgnoreCase)
        || rel.EndsWith("/.git", StringComparison.OrdinalIgnoreCase) || rel.Contains("/.git/", StringComparison.OrdinalIgnoreCase);

    private static bool Glob(string glob, string name) =>
        glob.StartsWith('*') ? name.EndsWith(glob[1..], StringComparison.OrdinalIgnoreCase) : name.Equals(glob, StringComparison.OrdinalIgnoreCase);
}
