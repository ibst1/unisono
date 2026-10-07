using System.Text.Json;
using System.Text.Json.Serialization;

namespace Unisono;

/// <summary>
/// Several local folders kept identical (e.g. the .obsidian settings of six Obsidian vaults): one of them is the hub,
/// and each member is synced with it by an ordinary two-way folder pair. Those pairs are made from the group when the
/// config is loaded (<see cref="SyncGroups.Expand"/>) and never stored, so the engine needs little new: a change in one
/// member reaches the hub and from there the others. A member's files that have never been synced with the hub take the
/// hub's version (the member's is kept among the previous versions), so joining a folder causes no conflict copies.
/// The members lie inside other pairs' folders and are left out of those (nested pairs); the hub is not, so exactly one
/// copy goes to the cloud, and there too the hub's files win over copies without history (see SyncEngine).
/// The definition is also written as a file (<see cref="SyncGroups.FileName"/>) into the hub and every member, and that
/// one file stays part of the folders' own pairs (<see cref="SyncScope"/>), so it reaches other computers through any of
/// the vaults: there Unisono offers to join the group, and follows when it changes, the hub included.
/// </summary>
public sealed class SyncGroup
{
    /// <summary>The same on every computer: links the config entry and the group files.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    /// <summary>A full path, written with %USERPROFILE% and the like where it lies inside them.</summary>
    public string Hub { get; set; } = "";
    /// <summary>Written like the hub. Members whose folder this computer does not have are left out here (see Expand).</summary>
    public List<string> Members { get; set; } = new();
    /// <summary>Shared by all the group's pairs: files that may change very often, suggestions answered with "keep syncing".</summary>
    public List<string> FrequentOk { get; set; } = new();
    public List<string> SuggestOk { get; set; } = new();
    public int PollSeconds { get; set; } = 30;
    /// <summary>When the definition last changed (UTC), on whichever computer: of several differing group files the latest wins.</summary>
    public DateTime Changed { get; set; }

    [JsonIgnore] public string HubFullPath => SyncGroups.Full(Hub);

    /// <summary>The group's folders: the hub first.</summary>
    [JsonIgnore] public IEnumerable<string> Folders => Members.Prepend(Hub);

    /// <summary>The definition as the group files and this config last agreed on it: tells a change made here from one that came with a file.</summary>
    [JsonIgnore] internal string? Synced { get; set; }
}

/// <summary>The group file: the group as every computer sees it. The same bytes in every folder and on every computer.</summary>
public sealed class GroupFile
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Hub { get; set; } = "";
    public List<string> Members { get; set; } = new();
    public List<string> FrequentOk { get; set; } = new();
    public List<string> SuggestOk { get; set; } = new();
    public int PollSeconds { get; set; } = 30;
    public DateTime Changed { get; set; }
    /// <summary>The group was removed on all computers; those that still have it remove it too.</summary>
    public bool Removed { get; set; }
}

/// <summary>A group file found in a synced folder, for a group this computer has not joined.</summary>
public sealed record GroupOffer(string Id, string Name, string Hub, List<string> Members, List<string> MembersHere, List<SyncPair> Replaces, GroupFile File);

public static class SyncGroups
{
    public const string FileName = ".unisono-group.json";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public static string Full(string path) =>
        string.IsNullOrWhiteSpace(path) ? "" : Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim())).TrimEnd('\\');

    public static bool Same(string a, string b) => Full(a).Equals(Full(b), StringComparison.OrdinalIgnoreCase);

    private static bool Inside(string path, string folder) =>
        Full(path).StartsWith(Full(folder) + "\\", StringComparison.OrdinalIgnoreCase);

    /// <summary>The folder pair that syncs a member with its group's hub. Same strings as a hand-made pair would have, so its id (and history) match.</summary>
    public static SyncPair PairFor(SyncGroup g, string member) => new()
    {
        Kind = RemoteKind.Folder,
        RemotePath = g.Hub,
        LocalPath = member,
        Direction = SyncDirection.TwoWay,
        // Unisono writes the group file into every folder itself; synced between them, two computers' copies could meet.
        IgnorePatterns = new() { "/" + FileName },
        FrequentOk = g.FrequentOk,
        SuggestOk = g.SuggestOk,
        PollSeconds = g.PollSeconds,
        Group = g,
    };

    /// <summary>
    /// Replaces the group pairs in the config's pair list with fresh ones. A member gets a pair when its folder is on
    /// this computer, or when it was synced here before (then its folder was deleted, which the pair reports).
    /// </summary>
    public static void Expand(Config config)
    {
        config.Pairs.RemoveAll(p => p.Group != null);
        foreach (var g in config.Groups)
            foreach (var m in g.Members)
            {
                var pair = PairFor(g, m);
                if (Directory.Exists(Full(m)) || File.Exists(SyncState.FilePathFor(pair))) config.Pairs.Add(pair);
            }
    }

    /// <summary>What is wrong with this group, or null. Pairs that are not the group's own and would overlap it are not checked here: see <see cref="Replaced"/>.</summary>
    public static string? Problem(Config config, SyncGroup g)
    {
        if (g.Name.Trim().Length == 0) return "Give the group a name.";
        if (g.Hub.Trim().Length == 0) return "Choose the hub folder.";
        if (!Directory.Exists(g.HubFullPath)) return $"The hub folder {g.HubFullPath} does not exist.";
        if (g.Members.Count == 0) return "Add at least one folder to keep in step with the hub.";
        var all = g.Folders.ToList();
        for (var i = 0; i < all.Count; i++)
            for (var j = i + 1; j < all.Count; j++)
            {
                if (Same(all[i], all[j])) return $"{Full(all[j])} is in the group twice.";
                if (Inside(all[i], all[j]) || Inside(all[j], all[i])) return $"{Full(all[i])} and {Full(all[j])} lie inside each other.";
            }
        foreach (var other in config.Groups.Where(o => o.Id != g.Id))
            foreach (var f in other.Folders)
                if (all.Any(x => Same(x, f) || Inside(x, f) || Inside(f, x))) return $"{Full(f)} is already part of the group '{other.Name}'.";
        // The hub must reach the cloud (or wherever) through its own pair, so it must not be another pair's local side.
        foreach (var p in config.Pairs.Where(p => p.Group == null))
            if (all.Any(x => Inside(p.LocalFullPath, x))) return $"The pair {p.Name} lies inside {Full(all.First(x => Inside(p.LocalFullPath, x)))}.";
        return null;
    }

    /// <summary>
    /// The hand-made folder pairs a group takes the place of: those between two of its folders, or with one of them as
    /// their local side (a member must be nobody else's, and the hub must stay part of its own folder's pair).
    /// </summary>
    public static List<SyncPair> Replaced(Config config, SyncGroup g)
    {
        var all = g.Folders.ToList();
        return config.Pairs.Where(p => p.Group == null && p.Kind == RemoteKind.Folder && all.Any(x => Same(x, p.LocalPath))).ToList();
    }

    /// <summary>
    /// Takes the group in (new or changed, the hub too): removes the pairs it replaces, keeping the history of those whose
    /// id a group pair takes over, forgets the history of its own pairs that are no more, and makes the group's pairs.
    /// Returns the removed pairs. The caller saves the config.
    /// </summary>
    public static List<SyncPair> Apply(Config config, SyncGroup g, Log log)
    {
        var replaced = Replaced(config, g);
        var keep = g.Members.Select(m => PairFor(g, m).Id).ToHashSet();
        foreach (var p in replaced)
        {
            config.Pairs.Remove(p);
            if (!keep.Contains(p.Id)) ForgetHistory(p);
            log.Info($"pair {p.LocalFullPath} <-> {p.Name} is now part of the group '{g.Name}'");
        }
        var i = config.Groups.FindIndex(x => x.Id == g.Id);
        if (i >= 0)
        {
            var old = config.Groups[i];
            foreach (var gone in old.Members.Select(m => PairFor(old, m)).Where(p => !keep.Contains(p.Id))) ForgetHistory(gone);
            if (!Same(old.Hub, g.Hub)) log.Info($"group '{g.Name}': the hub is now {g.HubFullPath} (was {old.HubFullPath}); every folder pairs up with it afresh");
            config.Groups[i] = g;
        }
        else config.Groups.Add(g);
        Expand(config);
        return replaced;
    }

    /// <summary>
    /// A copy of the group with another folder as its hub: a member that becomes the hub leaves the members, and the
    /// old hub becomes one in its place.
    /// </summary>
    public static SyncGroup WithHub(SyncGroup g, string hub)
    {
        var moved = Copy(g);
        var spelled = g.Folders.FirstOrDefault(x => Same(x, hub)) ?? SyncPair.Portable(Full(hub));
        if (Same(spelled, g.Hub)) return moved;
        var at = g.Members.FindIndex(x => Same(x, spelled));
        moved.Members = g.Members.Where(x => !Same(x, spelled)).ToList();
        moved.Members.Insert(at >= 0 ? at : 0, g.Hub);
        moved.Hub = spelled;
        return moved;
    }

    /// <summary>A copy to change; the config's group stays as it is until <see cref="Apply"/>.</summary>
    public static SyncGroup Copy(SyncGroup g) => new()
    {
        Id = g.Id, Name = g.Name, Hub = g.Hub, Members = g.Members.ToList(), FrequentOk = g.FrequentOk.ToList(),
        SuggestOk = g.SuggestOk.ToList(), PollSeconds = g.PollSeconds, Changed = g.Changed,
    };

    /// <summary>Ends the group here: its pairs go, with their history. Everywhere: the group files say so, and other computers follow.</summary>
    public static void Remove(Config config, SyncGroup g, bool everywhere, Log log)
    {
        foreach (var m in g.Members) ForgetHistory(PairFor(g, m));
        config.Groups.RemoveAll(x => x.Id == g.Id);
        if (everywhere)
        {
            var f = ToFile(g);
            f.Removed = true; f.Members = new(); f.Changed = DateTime.UtcNow;
            foreach (var dir in g.Folders.Select(Full))
                try { WriteFile(dir, f); } catch (Exception ex) { log.Warn($"group '{g.Name}': could not mark it removed in {dir}: {ex.Message}"); }
        }
        else if (!config.DeclinedGroups.Contains(g.Id)) config.DeclinedGroups.Add(g.Id);
        Expand(config);
        log.Info($"group '{g.Name}' removed{(everywhere ? " on all computers" : " on this computer")}; no files were deleted");
    }

    private static void ForgetHistory(SyncPair p)
    {
        try { File.Delete(SyncState.FilePathFor(p)); } catch { }
        MergeBase.Forget(p);
    }

    // ------------------------------------------------------------ the group files

    public static GroupFile ToFile(SyncGroup g) => new()
    {
        Id = g.Id, Name = g.Name, Hub = SyncPair.Portable(g.HubFullPath), Members = g.Members.Select(m => SyncPair.Portable(Full(m))).ToList(),
        FrequentOk = g.FrequentOk.ToList(), SuggestOk = g.SuggestOk.ToList(), PollSeconds = g.PollSeconds, Changed = g.Changed,
    };

    public static SyncGroup FromFile(GroupFile f) => new()
    {
        Id = f.Id, Name = f.Name, Hub = f.Hub, Members = f.Members.ToList(),
        FrequentOk = f.FrequentOk.ToList(), SuggestOk = f.SuggestOk.ToList(), PollSeconds = f.PollSeconds, Changed = f.Changed,
    };

    public static GroupFile? ReadFile(string folder)
    {
        var path = Path.Combine(folder, FileName);
        if (!File.Exists(path)) return null;
        try
        {
            var f = JsonSerializer.Deserialize<GroupFile>(File.ReadAllText(path), JsonOptions);
            if (f is not { Id.Length: > 0 }) return null;
            if (f.Hub.Length == 0) f.Hub = SyncPair.Portable(folder); // written before files named their hub: it was kept in the hub only
            return f;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Writes the file when its bytes would differ, so computers do not keep rewriting it for each other.</summary>
    public static bool WriteFile(string folder, GroupFile f)
    {
        if (!Directory.Exists(folder)) return false;
        var path = Path.Combine(folder, FileName);
        var text = JsonSerializer.Serialize(f, JsonOptions);
        try { if (File.Exists(path) && File.ReadAllText(path) == text) return false; } catch (IOException) { }
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, text);
        File.Move(tmp, path, overwrite: true);
        return true;
    }

    private static string Content(GroupFile f) =>
        JsonSerializer.Serialize(new { f.Id, f.Name, Hub = Full(f.Hub).ToLowerInvariant(), Members = f.Members.Select(m => Full(m).ToLowerInvariant()), f.FrequentOk, f.SuggestOk, f.PollSeconds, f.Removed });

    internal static string ContentOf(SyncGroup g) => Content(ToFile(g));

    /// <summary>Before the config is saved: a group changed here gets a new time, which makes its files win on other computers.</summary>
    public static void Stamp(Config config)
    {
        foreach (var g in config.Groups)
            if (ContentOf(g) != g.Synced) g.Changed = DateTime.UtcNow;
    }

    /// <summary>
    /// After the config was saved: the groups changed here go into their files (missing files are written too). A group
    /// not changed here leaves its files alone: one may hold a newer change from another computer for <see cref="Scan"/>.
    /// </summary>
    public static void WriteFiles(Config config)
    {
        foreach (var g in config.Groups)
        {
            var now = ContentOf(g);
            if (now == g.Synced && g.Folders.All(d => !Directory.Exists(Full(d)) || File.Exists(Path.Combine(Full(d), FileName)))) continue;
            WriteAll(g);
            g.Synced = now;
        }
    }

    private static void WriteAll(SyncGroup g)
    {
        var f = ToFile(g);
        foreach (var dir in g.Folders.Select(Full))
            try { WriteFile(dir, f); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Compares the groups with their files and looks for group files in the synced folders (at the top and one level
    /// down, where a vault's .obsidian lies). A joined group whose files hold a newer definition from another computer
    /// takes it in (a new hub too); a removed one is removed. Returns whether the config changed (the caller saves it
    /// and restarts syncing) and the groups to offer.
    /// </summary>
    public static (bool Changed, List<GroupOffer> Offers) Scan(Config config, Log log)
    {
        var changed = false;
        foreach (var g in config.Groups.ToList())
        {
            var files = g.Folders.Select(d => ReadFile(Full(d))).Where(f => f != null && f.Id == g.Id).Select(f => f!).ToList();
            var here = ContentOf(g);
            // Only a later definition counts: an older copy (from the cloud, from before) is simply written over.
            var newer = files.Where(f => f.Changed > g.Changed && Content(f) != here).OrderByDescending(f => f.Changed).FirstOrDefault();
            if (newer == null)
            {
                // The same change made on two computers: both settle on the later time, rather than rewriting each other's.
                foreach (var f in files.Where(f => Content(f) == here && f.Changed > g.Changed)) g.Changed = f.Changed;
                if (here != g.Synced || files.Count < g.Folders.Count(d => Directory.Exists(Full(d))) || files.Any(f => Content(f) != here || f.Changed != g.Changed))
                    WriteAll(g);
                g.Synced = here;
                continue;
            }
            if (newer.Removed)
            {
                foreach (var m in g.Members) ForgetHistory(PairFor(g, m));
                config.Groups.Remove(g);
                Expand(config);
                log.Info($"group '{g.Name}' was removed on another computer: its folders are no longer kept in step here (no files were deleted)");
                changed = true;
                continue;
            }
            var updated = FromFile(newer);
            // Folders written the same way as here keep their spelling (their pairs' ids depend on it).
            string Spell(string x) => g.Folders.FirstOrDefault(y => Same(y, x)) ?? x;
            updated.Hub = Spell(newer.Hub);
            updated.Members = newer.Members.Select(Spell).ToList();
            updated.Synced = Content(newer);
            Apply(config, updated, log);
            WriteAll(updated); // the folders that did not have the new version yet, e.g. a hub that was a member
            log.Info($"group '{g.Name}' was changed on another computer: {updated.Members.Count} folder(s) kept in step with {updated.HubFullPath}");
            changed = true;
        }

        // Groups to join: one offer per group, from its latest file wherever it was found.
        var found = new Dictionary<string, GroupFile>();
        foreach (var root in config.Pairs.Where(p => p.Group == null && p.LocalPath.Length > 0).Select(p => p.LocalFullPath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(root)) continue;
            IEnumerable<string> candidates;
            try { candidates = Directory.EnumerateDirectories(root).Prepend(root).ToList(); } catch { continue; }
            foreach (var dir in candidates)
            {
                if (!File.Exists(Path.Combine(dir, FileName)) || ReadFile(dir) is not { } f) continue;
                if (config.Groups.Any(g => g.Id == f.Id) || config.DeclinedGroups.Contains(f.Id)) continue;
                if (!found.TryGetValue(f.Id, out var seen) || f.Changed > seen.Changed) found[f.Id] = f;
            }
        }
        var offers = found.Values.Where(f => !f.Removed).Select(f =>
        {
            var g = FromFile(f);
            return new GroupOffer(f.Id, f.Name, Full(f.Hub), f.Members.Select(Full).ToList(),
                f.Members.Where(m => Directory.Exists(Full(m))).Select(Full).ToList(), Replaced(config, g), f);
        }).ToList();
        return (changed, offers);
    }

    /// <summary>Joins a group found by <see cref="Scan"/> (given by id or hub), from its latest file. The caller saves the config.</summary>
    public static SyncGroup Join(Config config, string idOrHub, Log log)
    {
        config.DeclinedGroups.RemoveAll(x => x == idOrHub);
        var (_, offers) = Scan(config, log);
        var offer = offers.FirstOrDefault(o => o.Id == idOrHub || Same(o.Hub, idOrHub)) ?? throw new InvalidOperationException("That group is not offered here (any more).");
        var g = FromFile(offer.File);
        g.Synced = Content(offer.File);
        config.DeclinedGroups.Remove(g.Id);
        if (Problem(config, g) is { } problem) throw new InvalidOperationException(problem);
        Apply(config, g, log);
        WriteAll(g);
        log.Info($"joined the group '{g.Name}': {g.Members.Count(m => Directory.Exists(Full(m)))} of {g.Members.Count} folder(s) are on this computer, hub {g.HubFullPath}");
        return g;
    }

    /// <summary>For a pair whose local folder holds a group's hub: its path relative to the pair.</summary>
    public static IEnumerable<string> HubsInside(SyncPair pair, IEnumerable<SyncPair> others)
    {
        var root = pair.LocalFullPath.TrimEnd('\\') + "\\";
        return others.Select(o => o.Group).Where(g => g != null).Distinct().Select(g => g!.HubFullPath)
            .Where(h => h.StartsWith(root, StringComparison.OrdinalIgnoreCase)).Select(h => h[root.Length..].Replace('\\', '/'));
    }
}
