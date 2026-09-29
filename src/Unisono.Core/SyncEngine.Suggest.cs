namespace Unisono;

// Suggestions of what to ignore (see IgnoreAdvisor): worked out on local scans, at most every SuggestInterval unless
// asked for afresh, and told once per pattern (the tray) with the pair's card showing them until the user chooses.
public sealed partial class SyncEngine
{
    public static readonly TimeSpan SuggestInterval = TimeSpan.FromMinutes(15);
    private readonly IgnoreAdvisor _advisor = new();
    private DateTime _lastSuggestUtc = DateTime.MinValue;

    /// <summary>The latest suggestions; replaced (never changed) when worked out again, which bumps <see cref="SuggestionsVersion"/>.</summary>
    public List<IgnoreSuggestion> Suggestions { get; private set; } = new();
    public int SuggestionsVersion { get; private set; }

    /// <summary>Work the suggestions out again on the next local scan, whatever the interval says.</summary>
    public void SuggestAgain() => _lastSuggestUtc = DateTime.MinValue;

    private void Advise(Dictionary<string, LocalItem> local, CycleStats stats)
    {
        if (DryRun) return;
        _advisor.Observe(local);
        if (DateTime.UtcNow - _lastSuggestUtc < SuggestInterval) return;
        _lastSuggestUtc = DateTime.UtcNow;
        List<IgnoreSuggestion> found;
        try { found = _advisor.Suggest(_root, local, _pair.SuggestOk, _log); }
        catch (Exception ex) { _log.Warn($"[{_pair.Name}] could not work out what to suggest ignoring: {Log.Text(ex)}"); return; }
        Suggestions = found;
        SuggestionsVersion++;
        // Told once per pattern, and only for those ticked in advance or seen churning: the rest wait quietly on the pair's card.
        var fresh = found.SelectMany(s => s.Patterns.Where(p => p.Recommended || s.Kind == "churn"))
            .Where(p => !_state.SuggestNotified.Contains(p.Pattern, StringComparer.OrdinalIgnoreCase)).ToList();
        if (fresh.Count == 0) return;
        _state.SuggestNotified.AddRange(fresh.Select(p => p.Pattern));
        var kinds = found.Where(s => s.Patterns.Any(fresh.Contains)).Select(s => s.Kind.Split(':')[0] switch
        {
            "build" => "build output", "gitignore" => "files a .gitignore leaves out", "junk" => "temporary files", _ => "files created and deleted in bulk",
        }).Distinct();
        stats.Notices.Add($"Some items in {_pair.Name} probably need not sync ({string.Join(", ", kinds)}). Open Unisono to choose.");
        _log.Info($"[{_pair.Name}] suggests ignoring: {string.Join(", ", fresh.Select(p => p.Pattern))}");
    }
}
