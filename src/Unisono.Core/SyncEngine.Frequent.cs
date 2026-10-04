namespace Unisono;

// Files whose changes are transferred again and again are often better ignored - but only if a program writes them for
// itself (logs, status, caches, databases). A document being written or code being programmed changes as often, and
// must simply keep syncing. So what counts is the kind of file, by type, name and how its content changes:
//   edited by people (documents, notes, source code)       never asked about
//   written by programs (logs, state, caches, databases,   asked after MachineTransfers changes within FrequentWindow
//     or a file that only ever grows at its end)
//   anything else (data exports, unknown types)            asked after OtherTransfers changes within FrequentWindow
// Asked once per file and run of the app; FrequentOk files never.
public sealed partial class SyncEngine
{
    public const int FrequentTransfers = 6;      // written by programs
    public const int OtherFrequentTransfers = 30; // neither clearly edited by people nor written by programs
    public static readonly TimeSpan FrequentWindow = TimeSpan.FromHours(1);

    private enum ChangeKind { Edited, Machine, Other }

    private static readonly HashSet<string> EditedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        // documents and notes
        ".doc", ".docx", ".docm", ".dot", ".dotx", ".xls", ".xlsx", ".xlsm", ".xlsb", ".ppt", ".pptx", ".pptm", ".odt", ".ods",
        ".odp", ".rtf", ".md", ".markdown", ".tex", ".pdf", ".one", ".vsdx", ".drawio", ".canvas", ".excalidraw", ".svg",
        ".pages", ".numbers", ".key", ".odoc", ".osheet", ".oslides",
        // source code and project files
        ".cs", ".csproj", ".sln", ".props", ".targets", ".vb", ".fs", ".py", ".ipynb", ".r", ".rmd", ".qmd", ".jl", ".js",
        ".mjs", ".cjs", ".ts", ".tsx", ".jsx", ".vue", ".svelte", ".html", ".htm", ".css", ".scss", ".less", ".ahk", ".ah2",
        ".ps1", ".psm1", ".psd1", ".bat", ".cmd", ".sh", ".bash", ".zsh", ".c", ".h", ".cpp", ".cc", ".hpp", ".java", ".kt",
        ".go", ".rs", ".swift", ".m", ".sql", ".lua", ".pl", ".rb", ".php", ".scala", ".dart", ".gradle", ".cmake", ".mk",
        ".yaml", ".yml", ".toml", ".gitignore", ".gitattributes", ".editorconfig", ".unisonoignore", ".dockerfile",
    };

    private static readonly HashSet<string> MachineTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ".log", ".db", ".sqlite", ".sqlite3", ".db-wal", ".db-shm", ".db-journal", ".ldb", ".lock", ".pid", ".cache", ".tmp",
        ".temp", ".etl", ".dmp", ".trace",
    };

    // Words in a file or folder name that mark what a program keeps for itself.
    private static readonly string[] MachineWords =
    {
        "log", "logs", "status", "state", "session", "workspace", "cache", "lock", "heartbeat", "history", "recent", "usage",
        "telemetry", "journal", "progress", "pid",
    };

    private readonly Dictionary<string, List<DateTime>> _changeTimes = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _frequentReported = new(StringComparer.OrdinalIgnoreCase);
    // Per file: the start of its content and its size at the last change, and how many changes in a row only appended.
    private readonly Dictionary<string, (byte[] Head, long Size, int Appends)> _shapes = new(StringComparer.OrdinalIgnoreCase);

    private void ChangeTransferred(string rel, string path, CycleStats stats)
    {
        if (DryRun || _pair.FrequentOk.Contains(rel, StringComparer.OrdinalIgnoreCase)) return;
        var appends = NoteShape(rel, path);
        var kind = KindOf(rel, appends);
        if (kind == ChangeKind.Edited) return;
        var limit = kind == ChangeKind.Machine ? FrequentTransfers : OtherFrequentTransfers;
        var now = DateTime.UtcNow;
        lock (_changeTimes)
        {
            if (!_changeTimes.TryGetValue(rel, out var times)) _changeTimes[rel] = times = new();
            times.Add(now);
            times.RemoveAll(t => now - t > FrequentWindow);
            if (times.Count < limit || !_frequentReported.Add(rel)) return;
        }
        var why = kind == ChangeKind.Machine
            ? (appends >= 3 ? "it only grows at its end, like a log" : "a log, status or cache file")
            : $"{limit} times within an hour";
        stats.Frequent.Add(rel);
        stats.Notices.Add($"{PathUtil.NameOf(rel)} in {_pair.Name} changes very often ({why}). Open Unisono to ignore it or keep syncing it.");
        _log.Warn($"[{_pair.Name}] '{rel}' changes very often ({why}); it is probably better ignored");
    }

    private static ChangeKind KindOf(string rel, int appends)
    {
        var name = PathUtil.NameOf(rel);
        var ext = Path.GetExtension(name);
        if (MachineTypes.Contains(ext) || System.Text.RegularExpressions.Regex.IsMatch(name, @"\.log\.\d+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            return ChangeKind.Machine;
        // Word for word, so "catalog.md" or "dialogue.docx" do not count; folders too (logs/today.txt, .obsidian/workspace.json).
        var words = System.Text.RegularExpressions.Regex.Split(rel.ToLowerInvariant(), "[^a-z0-9]+");
        if (words.Any(w => MachineWords.Contains(w)) && !EditedTypes.Contains(ext)) return ChangeKind.Machine;
        if (EditedTypes.Contains(ext)) return ChangeKind.Edited;
        if (appends >= 3) return ChangeKind.Machine;
        return ChangeKind.Other;
    }

    /// <summary>
    /// Compares the file with how it looked at its last change: grown, with the start unchanged, counts as an append.
    /// Returns how many changes in a row were appends (3 or more: written like a log).
    /// </summary>
    private int NoteShape(string rel, string path)
    {
        byte[] head;
        long size;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            size = fs.Length;
            head = new byte[(int)Math.Min(4096, size)];
            var read = 0;
            while (read < head.Length && fs.Read(head, read, head.Length - read) is var n && n > 0) read += n;
        }
        catch { return 0; } // online-only, gone, locked: no verdict
        lock (_shapes)
        {
            var appends = 0;
            if (_shapes.TryGetValue(rel, out var was) && size > was.Size && head.AsSpan(0, was.Head.Length).SequenceEqual(was.Head))
                appends = was.Appends + 1;
            _shapes[rel] = (head, size, appends);
            return appends;
        }
    }
}
