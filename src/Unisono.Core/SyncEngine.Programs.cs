namespace Unisono;

// Programs synced by Unisono, Unisono included. A running program's .exe or .dll cannot be overwritten, but Windows lets
// it be renamed: the running file is parked under a name no sync sees (".unisono-old") and the new version takes its
// place, used from the program's next start - as build.ps1 does. Parked files are deleted once nothing runs from them.
// Unisono's own folder is always kept on this device: a program that downloads files on demand cannot start from an
// online-only copy of itself.
public sealed partial class SyncEngine
{
    public const string ParkedSuffix = ".unisono-old";

    private static readonly string? AppDir = Environment.ProcessPath is { } exe
        ? Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(Path.GetFullPath(exe))!) : null;

    /// <summary>The folder Unisono runs from, or anything in it: always pinned ("Always keep on this device"), never online-only.</summary>
    private static bool InAppDir(string path)
    {
        if (AppDir == null) return false;
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return full.Equals(AppDir, StringComparison.OrdinalIgnoreCase) || full.StartsWith(AppDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsProgramFile(string path) =>
        path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);

    /// <summary>Moves a running program file aside so its new version can be written; null when Windows refuses.</summary>
    private string? Park(string path, string rel)
    {
        var parked = path + ParkedSuffix;
        if (File.Exists(parked))
        {
            try { File.Delete(parked); }
            catch { parked = $"{path}.{DateTime.Now:yyyyMMddHHmmss}{ParkedSuffix}"; } // an older one still runs
        }
        try { File.Move(path, parked); }
        catch (Exception ex)
        {
            _log.Debug($"[{_pair.Name}] could not move the running {rel} aside: {ex.Message}");
            return null;
        }
        _state.Parked.Add(parked);
        return parked;
    }

    /// <summary>Deletes parked program files nothing runs from any more.</summary>
    private void SweepParked()
    {
        if (DryRun || _state.Parked.Count == 0) return;
        foreach (var p in _state.Parked.ToList())
        {
            try
            {
                if (File.Exists(p)) File.Delete(p);
                _state.Parked.Remove(p);
            }
            catch { } // still running: next time
        }
    }

    /// <summary>After a running program's new version was written in place of the parked one.</summary>
    private void Replaced(string path, string rel, CycleStats stats)
    {
        var self = Environment.ProcessPath != null && Path.GetFullPath(Environment.ProcessPath).Equals(Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);
        _log.Info($"[{_pair.Name}] {rel} was running; its new version is in place and used from its next start");
        // Unisono's own new version is announced by the tray app, which offers to restart into it.
        if (!self) stats.Notices.Add($"{PathUtil.NameOf(rel)} was updated while it ran; the new version is used from its next start.");
    }
}
