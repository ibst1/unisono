using System.Globalization;
using System.Text.RegularExpressions;

namespace Unisono;

/// <summary>
/// Backup pairs (<see cref="SyncDirection.Backup"/>): like upload-only, and nothing on the other side is ever lost.
/// A file replaced by a newer local version, or deleted locally, is first moved into the pair's
/// <see cref="VersionsFolder"/> on the other side as "name (yyyy-MM-dd HHmmss).ext"; a deleted folder goes there whole
/// as "folder (yyyy-MM-dd HHmmss)". Kept versions are pruned by the version settings (count and age) once a day, the
/// newest one of each item always stays. The versions folder is invisible to the rest of the sync.
/// </summary>
public sealed partial class SyncEngine
{
    public const string VersionsFolder = ".unisono-versions";
    private static readonly Regex VersionName = new(@"^(?<stem>.*) \((?<when>\d{4}-\d{2}-\d{2} \d{6})\)(?<ext>\.[^.]*)?$", RegexOptions.CultureInvariant);

    private bool BackupMode => _pair.Direction == SyncDirection.Backup;
    private Dictionary<string, RemoteNode> _fullRemote = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastPruneUtc = DateTime.MinValue;

    /// <summary>The kept versions of a backup pair as of its last cycle; null for other pairs.</summary>
    public List<BackupVersion>? BackupVersions() => BackupMode ? ListVersions(_fullRemote) : null;

    public static bool IsInVersions(string rel) =>
        rel.Equals(VersionsFolder, StringComparison.OrdinalIgnoreCase) || rel.StartsWith(VersionsFolder + "/", StringComparison.OrdinalIgnoreCase);

    /// <summary>A kept version on the other side.</summary>
    public sealed record BackupVersion(string Rel, string VersionRel, DateTime Taken, long Size, bool IsFolder);

    /// <summary>Every kept version in a remote tree, newest first.</summary>
    public static List<BackupVersion> ListVersions(IReadOnlyDictionary<string, RemoteNode> tree)
    {
        var list = new List<BackupVersion>();
        var candidates = tree.Where(t => t.Key.StartsWith(VersionsFolder + "/", StringComparison.OrdinalIgnoreCase) && VersionName.IsMatch(PathUtil.NameOf(t.Key))).ToList();
        var keptFolders = new HashSet<string>(candidates.Where(t => t.Value.IsFolder).Select(t => t.Key), StringComparer.OrdinalIgnoreCase);
        foreach (var (rel, node) in candidates)
        {
            // Inside a kept folder, only the folder itself counts as a version.
            var inKeptFolder = false;
            for (var up = PathUtil.ParentOf(rel); up.Length > VersionsFolder.Length; up = PathUtil.ParentOf(up))
                if (keptFolders.Contains(up)) { inKeptFolder = true; break; }
            if (inKeptFolder) continue;
            var m = VersionName.Match(PathUtil.NameOf(rel));
            if (!DateTime.TryParseExact(m.Groups["when"].Value, "yyyy-MM-dd HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var taken)) continue;
            var dir = PathUtil.ParentOf(rel)[VersionsFolder.Length..].TrimStart('/');
            list.Add(new(PathUtil.Join(dir, m.Groups["stem"].Value + m.Groups["ext"].Value), rel, taken, node.Size, node.IsFolder));
        }
        return list.OrderByDescending(v => v.Taken).ToList();
    }

    /// <summary>Moves a remote file or folder into the versions folder (creating its folders there).</summary>
    private async Task KeepRemoteVersionAsync(RemoteNode node, string rel, CancellationToken ct)
    {
        var name = PathUtil.NameOf(rel);
        var ext = node.IsFolder ? "" : Path.GetExtension(name);
        var stem = name[..^ext.Length];
        var dir = PathUtil.ParentOf(rel);
        var targetRel = $"{VersionsFolder}/{(dir.Length > 0 ? dir + "/" : "")}{stem} ({DateTime.Now:yyyy-MM-dd HHmmss}){ext}";
        var parent = await EnsureRemoteFolderAsync(PathUtil.ParentOf(targetRel), ct);
        var moved = await _store.MoveAsync(node, targetRel, parent, ct);
        _fullRemote[targetRel] = moved;
        _log.Info($"[{_pair.Name}] {(File.Exists(Local(rel)) || Directory.Exists(Local(rel)) ? "replaced" : "deleted here")}: {rel}; the previous version is kept as {targetRel}");
    }

    private async Task<RemoteNode?> EnsureRemoteFolderAsync(string rel, CancellationToken ct)
    {
        if (rel.Length == 0) return null;
        if (_fullRemote.TryGetValue(rel, out var existing)) return existing;
        var parent = await EnsureRemoteFolderAsync(PathUtil.ParentOf(rel), ct);
        var created = await _store.CreateFolderAsync(rel, parent, ct);
        _fullRemote[rel] = created;
        return created;
    }

    /// <summary>Once a day: removes kept versions beyond the version settings (the newest of each item always stays).</summary>
    private async Task PruneVersionsAsync(CancellationToken ct)
    {
        if (!BackupMode || DryRun || DateTime.UtcNow - _lastPruneUtc < TimeSpan.FromDays(1)) return;
        _lastPruneUtc = DateTime.UtcNow;
        var cutoff = _options.KeepVersionsDays > 0 ? DateTime.Now.AddDays(-_options.KeepVersionsDays) : DateTime.MinValue;
        var removed = 0;
        foreach (var group in ListVersions(_fullRemote).GroupBy(v => v.Rel, StringComparer.OrdinalIgnoreCase))
        {
            var i = 0;
            foreach (var v in group.OrderByDescending(v => v.Taken))
            {
                var keep = i == 0 || (i < Math.Max(1, _options.KeepVersions) && v.Taken >= cutoff);
                i++;
                if (keep || !_fullRemote.TryGetValue(v.VersionRel, out var node)) continue;
                try { await _store.DeleteAsync(node, ct); RemoveTree(_fullRemote, v.VersionRel); removed++; }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { _log.Debug($"[{_pair.Name}] could not remove old version {v.VersionRel}: {ex.Message}"); }
            }
        }
        if (removed > 0) _log.Info($"[{_pair.Name}] removed {removed} old version(s) from {VersionsFolder}");
    }

    /// <summary>
    /// Puts a kept version back in place locally (the local file it replaces is kept among this computer's versions);
    /// the next cycle backs it up as the current version. Call between cycles.
    /// </summary>
    public async Task<string> RestoreAsync(string versionRel, CancellationToken ct)
    {
        if (!_fullRemote.TryGetValue(versionRel, out var node) || node.IsFolder) throw new InvalidOperationException("That version is no longer in the backup.");
        var v = ListVersions(new Dictionary<string, RemoteNode> { [versionRel] = node }).FirstOrDefault()
                ?? throw new InvalidOperationException("Not a kept version.");
        var path = Local(v.Rel);
        if (File.Exists(path)) VersionStore.Save(_pair, v.Rel, path, _options);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await Tracked(SyncActivity.Direction.Down, v.Rel, node.Size, () => _store.DownloadAsync(node, path, ct));
        LocalChanged = true;
        _log.Info($"[{_pair.Name}] restored {v.Rel} from the version of {v.Taken:yyyy-MM-dd HH:mm:ss}");
        Journal("restored", v.Rel, node.Size, $"from the backup's version of {v.Taken:yyyy-MM-dd HH:mm}");
        return v.Rel;
    }
}
