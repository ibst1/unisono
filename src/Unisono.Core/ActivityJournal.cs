using System.Text.Json;

namespace Unisono;

/// <summary>
/// What the sync did, file by file, for the window's Activity view: one JSON line per action in
/// DataDir\activity\yyyy-MM-dd.jsonl (a file per day, kept <see cref="KeepDays"/> days). The log says the same in prose;
/// this is the structured form the view can filter and act on.
/// </summary>
public static class ActivityJournal
{
    public const int KeepDays = 60;

    /// <param name="Action">What happened: uploaded, uploaded change, downloaded, deleted remotely, deleted here,
    /// moved remotely, moved here, conflict, exported, written back, restored, freed up space.</param>
    /// <param name="Size">Bytes, or -1 when it does not apply (folders, moves).</param>
    /// <param name="Detail">Why, where to, or where from.</param>
    public sealed record Entry(DateTime Time, string PairId, string Pair, string Action, string Rel, long Size, string? Detail);

    private static readonly object Gate = new();
    private static string Dir => Path.Combine(AppPaths.DataDir, "activity");
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>Raised after every entry (on the syncing thread).</summary>
    public static event Action? Added;

    public static void Add(SyncPair pair, string action, string rel, long size = -1, string? detail = null)
    {
        try
        {
            var line = JsonSerializer.Serialize(new Entry(DateTime.Now, pair.Id, pair.Name, action, rel, size, detail), Json);
            lock (Gate)
            {
                Directory.CreateDirectory(Dir);
                File.AppendAllText(Path.Combine(Dir, $"{DateTime.Now:yyyy-MM-dd}.jsonl"), line + "\n");
            }
        }
        catch { return; } // the journal must never stop a sync
        try { Added?.Invoke(); } catch { }
    }

    /// <summary>The days that have entries, newest first.</summary>
    public static List<DateTime> Days()
    {
        if (!Directory.Exists(Dir)) return new();
        return Directory.EnumerateFiles(Dir, "*.jsonl")
            .Select(f => DateTime.TryParseExact(Path.GetFileNameWithoutExtension(f), "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out var d) ? d : (DateTime?)null)
            .Where(d => d != null).Select(d => d!.Value).OrderByDescending(d => d).ToList();
    }

    /// <summary>One day's entries, newest first.</summary>
    public static List<Entry> Read(DateTime day)
    {
        var file = Path.Combine(Dir, $"{day:yyyy-MM-dd}.jsonl");
        if (!File.Exists(file)) return new();
        string[] lines;
        lock (Gate) lines = File.ReadAllLines(file);
        var entries = new List<Entry>();
        foreach (var l in lines)
        {
            try { if (l.Length > 0 && JsonSerializer.Deserialize<Entry>(l, Json) is { } e) entries.Add(e); }
            catch { } // a line cut short by a crash
        }
        entries.Reverse();
        return entries;
    }

    /// <summary>Drops the days older than <see cref="KeepDays"/>.</summary>
    public static void Prune()
    {
        try
        {
            foreach (var day in Days().Where(d => d < DateTime.Today.AddDays(-KeepDays)))
                File.Delete(Path.Combine(Dir, $"{day:yyyy-MM-dd}.jsonl"));
        }
        catch { }
    }
}
