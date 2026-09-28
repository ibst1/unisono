using System.Text.Json;

namespace Unisono;

/// <summary>
/// Turns an existing unencrypted pair into an encrypted one in place: the Cryptomator vault is created next to the
/// plain files in the same remote folder (by the window, <see cref="MarkerPath"/> then says a conversion is due), and
/// this copies every remote file into it, encrypted: from this computer where the local copy is the synced version,
/// otherwise downloaded (online-only files, files outside the pair's folder selection) or exported (Google documents
/// become .docx/.xlsx/.pptx). It runs in turns in place of the pair's cycles, and resumes after a restart: what the vault
/// already holds is not copied again. When everything is in the vault and nothing plain changed meanwhile, the pair gets
/// a new sync state (so nothing is compared again) and the plain originals go to the remote's trash.
/// </summary>
public sealed class VaultConverter
{
    private readonly SyncPair _pair;
    private readonly IRemoteStore _plain;
    private readonly IRemoteStore _vault;
    private readonly Log _log;
    private readonly SyncState _oldState;
    // Plain item -> what was copied: its modified time then, and the new state entry.
    private readonly Dictionary<string, (DateTime PlainModified, StateEntry Entry)> _copied = new(StringComparer.OrdinalIgnoreCase);

    public static string MarkerPath(SyncPair pair) => Path.Combine(AppPaths.DataDir, "convert", pair.Id + ".json");
    public static bool Pending(SyncPair pair) => File.Exists(MarkerPath(pair));

    public static void Request(SyncPair pair)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath(pair))!);
        File.WriteAllText(MarkerPath(pair), JsonSerializer.Serialize(new { requested = DateTime.Now, pair = pair.Name }));
    }

    /// <summary>Items at the top of a remote folder that belong to a Cryptomator vault.</summary>
    public static bool IsVaultItem(string rel) =>
        rel.Equals(CryptomatorVault.VaultFile, StringComparison.OrdinalIgnoreCase) || rel.Equals(CryptomatorVault.MasterkeyFile, StringComparison.OrdinalIgnoreCase)
        || rel.Equals(CryptomatorVault.DataFolder, StringComparison.OrdinalIgnoreCase) || rel.StartsWith(CryptomatorVault.DataFolder + "/", StringComparison.OrdinalIgnoreCase);

    public VaultConverter(SyncPair pair, IRemoteStore plain, IRemoteStore vault, Log log)
    {
        _pair = pair;
        _plain = plain;
        _vault = vault;
        _log = log;
        _oldState = SyncState.Load(pair);
        _plain.LoadState(_oldState); // the plain index as last synced: the change feed brings it up to date
    }

    private string Local(string rel) => Path.Combine(_pair.LocalFullPath, rel.Replace('/', '\\'));

    /// <summary>One turn of at most <paramref name="budget"/> (null: until done). True when the conversion is complete.</summary>
    public async Task<bool> RunAsync(TimeSpan? budget, Action<string> progress, CancellationToken ct)
    {
        var deadline = budget is { } b ? DateTime.UtcNow + b : DateTime.MaxValue;
        var plainTree = (await _plain.GetTreeAsync(_log, ct)).Where(x => !IsVaultItem(x.Key)).ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
        var vaultTree = await _vault.GetTreeAsync(_log, ct);

        // The files to copy, under the names they get in the vault (Google documents as their Office export).
        var work = new List<(RemoteNode Node, string Target, string? ExportMime)>();
        foreach (var node in plainTree.Values.Where(n => !n.IsFolder).OrderBy(n => n.RelPath, StringComparer.OrdinalIgnoreCase))
        {
            if (node.IsNativeDocument)
            {
                if (SyncEngine.ExportMime(node.MimeType) is not { } mime) { _log.Warn($"[{_pair.Name}] '{node.RelPath}' ({node.MimeType}) cannot be exported and is not taken into the vault"); continue; }
                work.Add((node, SyncEngine.DerivedName(node, EntryKind.Export)!, mime));
            }
            else work.Add((node, node.RelPath, null));
        }
        var done = work.Count(w => _copied.TryGetValue(w.Node.RelPath, out var c) && c.PlainModified == w.Node.ModifiedUtc);
        progress($"encrypting {done}/{work.Count}");

        foreach (var folder in plainTree.Values.Where(n => n.IsFolder).OrderBy(n => PathUtil.Depth(n.RelPath)))
        {
            if (vaultTree.ContainsKey(folder.RelPath)) continue;
            vaultTree.TryGetValue(PathUtil.ParentOf(folder.RelPath), out var parent);
            vaultTree[folder.RelPath] = await _vault.CreateFolderAsync(folder.RelPath, parent, ct);
        }

        foreach (var (node, target, exportMime) in work)
        {
            ct.ThrowIfCancellationRequested();
            if (_copied.TryGetValue(node.RelPath, out var c) && c.PlainModified == node.ModifiedUtc) continue;
            if (DateTime.UtcNow > deadline) return false;
            var entry = await CopyAsync(node, target, exportMime, vaultTree, ct);
            if (entry != null) _copied[node.RelPath] = (node.ModifiedUtc, entry);
            progress($"encrypting {++done}/{work.Count}");
        }

        // Everything copied. Anything plain that changed meanwhile (another computer) is copied again first.
        var again = (await _plain.GetTreeAsync(_log, ct)).Where(x => !IsVaultItem(x.Key) && !x.Value.IsFolder)
            .Count(x => !_copied.TryGetValue(x.Key, out var c) || c.PlainModified != x.Value.ModifiedUtc);
        if (again > 0) { _log.Info($"[{_pair.Name}] {again} file(s) changed on the other side while encrypting; copying them again"); return false; }

        await FinishAsync(plainTree, ct);
        return true;
    }

    /// <summary>Copies one plain file into the vault; returns its new state entry.</summary>
    private async Task<StateEntry?> CopyAsync(RemoteNode node, string target, string? exportMime, Dictionary<string, RemoteNode> vaultTree, CancellationToken ct)
    {
        var local = Local(target);
        var info = new FileInfo(local);
        _oldState.Entries.TryGetValue(node.RelPath, out var old);
        // The local copy stands for the synced version when it is complete and unchanged since the last sync.
        var useLocal = info.Exists && !CloudFiles.IsDehydrated(info.Attributes) && old != null
                       && info.Length == old.LocalSize && info.LastWriteTimeUtc.Ticks == old.LocalMtimeTicks;
        var temp = useLocal ? null : Path.Combine(AppPaths.DataDir, "crypt-tmp", Guid.NewGuid().ToString("N"));
        try
        {
            string source;
            if (useLocal) source = local;
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
                using (var t = SyncActivity.Begin(SyncActivity.Direction.Down, _pair.Name, node.RelPath, node.Size))
                {
                    if (exportMime != null) await _plain.ExportAsync(node, exportMime, temp!, ct);
                    else await _plain.DownloadAsync(node, temp!, ct);
                    t.Complete();
                }
                source = temp!;
            }
            var md5 = PathUtil.Md5Of(source);
            var sourceInfo = new FileInfo(source);
            vaultTree.TryGetValue(PathUtil.ParentOf(target), out var parent);
            RemoteNode uploaded;
            using (var t = SyncActivity.Begin(SyncActivity.Direction.Up, _pair.Name, target, sourceInfo.Length))
            {
                uploaded = vaultTree.TryGetValue(target, out var existing) && !existing.IsFolder
                    ? await _vault.UploadUpdateAsync(source, existing, ct)
                    : await _vault.UploadNewAsync(source, target, parent, ct);
                t.Complete();
            }
            vaultTree[target] = uploaded;
            // The state entry as the engine would write it after syncing: the local side as it is now, if there is one.
            var now = new FileInfo(local);
            return new StateEntry
            {
                Path = target, Kind = EntryKind.File, DriveId = uploaded.Id, Size = uploaded.Size, DriveModifiedUtc = uploaded.ModifiedUtc,
                Md5 = useLocal || (now.Exists && !CloudFiles.IsDehydrated(now.Attributes)) ? md5 : null,
                LocalSize = useLocal ? info.Length : now.Exists ? now.Length : -1,
                LocalMtimeTicks = useLocal ? info.LastWriteTimeUtc.Ticks : now.Exists ? now.LastWriteTimeUtc.Ticks : 0,
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.Warn($"[{_pair.Name}] could not encrypt '{node.RelPath}' yet: {ex.Message}");
            return null;
        }
        finally
        {
            if (temp != null) try { File.Delete(temp); } catch { }
        }
    }

    private async Task FinishAsync(Dictionary<string, RemoteNode> plainTree, CancellationToken ct)
    {
        var vaultTree = await _vault.GetTreeAsync(_log, ct);
        var state = new SyncState();
        foreach (var (rel, node) in vaultTree)
        {
            if (node.IsFolder) { if (Directory.Exists(Local(rel))) state.Entries[rel] = new StateEntry { Path = rel, Kind = EntryKind.Folder, DriveId = node.Id }; continue; }
            var entry = _copied.Values.Select(c => c.Entry).FirstOrDefault(e => e.Path.Equals(rel, StringComparison.OrdinalIgnoreCase));
            // Only files that exist here get history; the rest arrive (or become placeholders) with the next cycle.
            if (entry != null && entry.LocalSize >= 0) state.Entries[rel] = entry;
        }
        // Link files (".url" stand-ins for cloud-only files) were generated here: the real files are in the vault now.
        foreach (var link in _oldState.Entries.Values.Where(e => e.Kind == EntryKind.Link && e.LocalName != null))
            try { if (File.Exists(Local(link.LocalName!))) RecycleBin.Delete(Local(link.LocalName!)); } catch { }
        _vault.SaveState(state);
        state.Save(_pair);

        // The plain originals go to the remote's trash (recoverable there), top-level items first.
        var removed = 0;
        foreach (var node in plainTree.Values.Where(n => !n.RelPath.Contains('/')))
        {
            try { await _plain.DeleteAsync(node, ct); removed++; }
            catch (Exception ex) { _log.Warn($"[{_pair.Name}] could not move the unencrypted '{node.RelPath}' to the trash: {ex.Message}"); }
        }
        try { File.Delete(MarkerPath(_pair)); } catch { }
        _log.Info($"[{_pair.Name}] encrypted: {_copied.Count} file(s) in the vault; {removed} unencrypted top-level item(s) moved to the trash on the other side");
        ActivityJournal.Add(_pair, "encrypted", "", -1, $"{_copied.Count} file(s) moved into the Cryptomator vault");
    }
}
