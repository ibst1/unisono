using System.Text.Json;

namespace Unisono;

/// <summary>
/// Turns an existing pair's remote folder into a Cryptomator vault, or a vault back into plain files, in place.
///
/// Encrypting: the vault is created next to the plain files (by the window; <see cref="MarkerPath"/> then says a
/// conversion is due) and every remote file is copied into it: from this computer where the local copy is the synced
/// version, otherwise downloaded (online-only files, files outside the pair's folder selection) or exported (Google
/// documents become .docx/.xlsx/.pptx). At the end the plain originals go to the remote's trash.
/// Decrypting: every file in the vault is copied out as a plain file next to it, the same way; at the end the vault's
/// own files (vault.cryptomator, masterkey.cryptomator, d/) go to the trash.
///
/// Either way it runs in turns in place of the pair's cycles and resumes after a restart (what the target already holds
/// is not copied again); nothing is removed before everything is copied and the source stood still; then the pair gets a
/// new sync state, so nothing has to be compared again.
/// </summary>
public sealed class VaultConverter
{
    private readonly SyncPair _pair;
    private readonly IRemoteStore _source, _target;
    private readonly bool _decrypt;
    private readonly Log _log;
    private readonly SyncState _oldState;
    // Source item -> what was copied: its modified time then, and the new state entry.
    private readonly Dictionary<string, (DateTime SourceModified, StateEntry Entry)> _copied = new(StringComparer.OrdinalIgnoreCase);

    public static string MarkerPath(SyncPair pair) => Path.Combine(AppPaths.DataDir, "convert", pair.Id + ".json");
    public static bool Pending(SyncPair pair) => File.Exists(MarkerPath(pair));

    /// <summary>Whether the pending conversion decrypts (false: encrypts).</summary>
    public static bool PendingDecrypt(SyncPair pair)
    {
        try { return JsonDocument.Parse(File.ReadAllText(MarkerPath(pair))).RootElement.TryGetProperty("decrypt", out var d) && d.GetBoolean(); }
        catch { return false; }
    }

    public static void Request(SyncPair pair, bool decrypt = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath(pair))!);
        File.WriteAllText(MarkerPath(pair), JsonSerializer.Serialize(new { requested = DateTime.Now, pair = pair.Name, decrypt }));
    }

    /// <summary>Items at the top of a remote folder that belong to a Cryptomator vault.</summary>
    public static bool IsVaultItem(string rel) =>
        rel.Equals(CryptomatorVault.VaultFile, StringComparison.OrdinalIgnoreCase) || rel.Equals(CryptomatorVault.MasterkeyFile, StringComparison.OrdinalIgnoreCase)
        || rel.Equals(CryptomatorVault.DataFolder, StringComparison.OrdinalIgnoreCase) || rel.StartsWith(CryptomatorVault.DataFolder + "/", StringComparison.OrdinalIgnoreCase);

    /// <param name="plain">The remote folder as plain files.</param>
    /// <param name="vault">The same folder as a Cryptomator vault (<see cref="CryptomatorStore"/>).</param>
    /// <param name="decrypt">True: from the vault to plain files; false: into the vault.</param>
    public VaultConverter(SyncPair pair, IRemoteStore plain, IRemoteStore vault, Log log, bool decrypt = false)
    {
        _pair = pair;
        _decrypt = decrypt;
        (_source, _target) = decrypt ? (vault, plain) : (plain, vault);
        _log = log;
        _oldState = SyncState.Load(pair);
        _source.LoadState(_oldState); // the source's index as last synced: its change feed brings it up to date
    }

    private string Local(string rel) => Path.Combine(_pair.LocalFullPath, rel.Replace('/', '\\'));
    private string Verb => _decrypt ? "decrypting" : "encrypting";

    private async Task<Dictionary<string, RemoteNode>> SourceTreeAsync(CancellationToken ct) => _decrypt
        ? await _source.GetTreeAsync(_log, ct)
        : (await _source.GetTreeAsync(_log, ct)).Where(x => !IsVaultItem(x.Key)).ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);

    private async Task<Dictionary<string, RemoteNode>> TargetTreeAsync(CancellationToken ct) => _decrypt
        ? (await _target.GetTreeAsync(_log, ct)).Where(x => !IsVaultItem(x.Key)).ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase)
        : await _target.GetTreeAsync(_log, ct);

    /// <summary>One turn of at most <paramref name="budget"/> (null: until done). True when the conversion is complete.</summary>
    public async Task<bool> RunAsync(TimeSpan? budget, Action<string> progress, CancellationToken ct)
    {
        var deadline = budget is { } b ? DateTime.UtcNow + b : DateTime.MaxValue;
        var sourceTree = await SourceTreeAsync(ct);
        var targetTree = await TargetTreeAsync(ct);
        if (_decrypt && sourceTree.Keys.FirstOrDefault(k => IsVaultItem(k) && !k.Contains('/')) is { } clash)
            throw new InvalidOperationException($"The vault holds an item named '{clash}' at its top, which the vault's own files use; rename it first (in the local folder).");

        // The files to copy, under the names they get (Google documents as their Office export).
        var work = new List<(RemoteNode Node, string Target, string? ExportMime)>();
        foreach (var node in sourceTree.Values.Where(n => !n.IsFolder).OrderBy(n => n.RelPath, StringComparer.OrdinalIgnoreCase))
        {
            if (node.IsNativeDocument)
            {
                if (SyncEngine.ExportMime(node.MimeType) is not { } mime) { _log.Warn($"[{_pair.Name}] '{node.RelPath}' ({node.MimeType}) cannot be exported and is not taken into the vault"); continue; }
                work.Add((node, SyncEngine.DerivedName(node, EntryKind.Export)!, mime));
            }
            else work.Add((node, node.RelPath, null));
        }
        var done = work.Count(w => _copied.TryGetValue(w.Node.RelPath, out var c) && c.SourceModified == w.Node.ModifiedUtc);
        progress($"{Verb} {done}/{work.Count}");

        foreach (var folder in sourceTree.Values.Where(n => n.IsFolder).OrderBy(n => PathUtil.Depth(n.RelPath)))
        {
            if (targetTree.ContainsKey(folder.RelPath)) continue;
            targetTree.TryGetValue(PathUtil.ParentOf(folder.RelPath), out var parent);
            targetTree[folder.RelPath] = await _target.CreateFolderAsync(folder.RelPath, parent, ct);
        }

        foreach (var (node, target, exportMime) in work)
        {
            ct.ThrowIfCancellationRequested();
            if (_copied.TryGetValue(node.RelPath, out var c) && c.SourceModified == node.ModifiedUtc) continue;
            if (DateTime.UtcNow > deadline) return false;
            var entry = await CopyAsync(node, target, exportMime, targetTree, ct);
            if (entry != null) _copied[node.RelPath] = (node.ModifiedUtc, entry);
            progress($"{Verb} {++done}/{work.Count}");
        }

        // Everything copied. Anything that changed in the source meanwhile (another computer) is copied again first.
        var again = (await SourceTreeAsync(ct)).Where(x => !x.Value.IsFolder)
            .Count(x => !_copied.TryGetValue(x.Key, out var c) || c.SourceModified != x.Value.ModifiedUtc);
        if (again > 0) { _log.Info($"[{_pair.Name}] {again} file(s) changed on the other side while {Verb}; copying them again"); return false; }

        await FinishAsync(sourceTree, ct);
        return true;
    }

    /// <summary>Copies one file from the source to the target; returns its new state entry.</summary>
    private async Task<StateEntry?> CopyAsync(RemoteNode node, string target, string? exportMime, Dictionary<string, RemoteNode> targetTree, CancellationToken ct)
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
                    if (exportMime != null) await _source.ExportAsync(node, exportMime, temp!, ct);
                    else await _source.DownloadAsync(node, temp!, ct);
                    t.Complete();
                }
                source = temp!;
            }
            var md5 = PathUtil.Md5Of(source);
            var sourceInfo = new FileInfo(source);
            targetTree.TryGetValue(PathUtil.ParentOf(target), out var parent);
            RemoteNode uploaded;
            using (var t = SyncActivity.Begin(SyncActivity.Direction.Up, _pair.Name, target, sourceInfo.Length))
            {
                uploaded = targetTree.TryGetValue(target, out var existing) && !existing.IsFolder
                    ? await _target.UploadUpdateAsync(source, existing, ct)
                    : await _target.UploadNewAsync(source, target, parent, ct);
                t.Complete();
            }
            targetTree[target] = uploaded;
            // The state entry as the engine would write it after syncing: the local side as it is now, if there is one.
            var now = new FileInfo(local);
            return new StateEntry
            {
                Path = target, Kind = EntryKind.File, DriveId = uploaded.Id, Size = uploaded.Size, DriveModifiedUtc = uploaded.ModifiedUtc,
                // A plain target reports its own checksum (Drive); a vault's has none, so the content's stands in.
                Md5 = uploaded.Md5 ?? (useLocal || (now.Exists && !CloudFiles.IsDehydrated(now.Attributes)) ? md5 : null),
                LocalSize = useLocal ? info.Length : now.Exists ? now.Length : -1,
                LocalMtimeTicks = useLocal ? info.LastWriteTimeUtc.Ticks : now.Exists ? now.LastWriteTimeUtc.Ticks : 0,
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.Warn($"[{_pair.Name}] could not copy '{node.RelPath}' yet ({Verb}): {ex.Message}");
            return null;
        }
        finally
        {
            if (temp != null) try { File.Delete(temp); } catch { }
        }
    }

    private async Task FinishAsync(Dictionary<string, RemoteNode> sourceTree, CancellationToken ct)
    {
        var targetTree = await TargetTreeAsync(ct);
        var state = new SyncState();
        foreach (var (rel, node) in targetTree)
        {
            if (node.IsFolder) { if (Directory.Exists(Local(rel))) state.Entries[rel] = new StateEntry { Path = rel, Kind = EntryKind.Folder, DriveId = node.Id }; continue; }
            var entry = _copied.Values.Select(c => c.Entry).FirstOrDefault(e => e.Path.Equals(rel, StringComparison.OrdinalIgnoreCase));
            // Only files that exist here get history; the rest arrive (or become placeholders) with the next cycle.
            if (entry != null && entry.LocalSize >= 0) state.Entries[rel] = entry;
        }
        // Link files (".url" stand-ins for cloud-only files) were generated here: the real files are copied now.
        foreach (var link in _oldState.Entries.Values.Where(e => e.Kind == EntryKind.Link && e.LocalName != null))
            try { if (File.Exists(Local(link.LocalName!))) RecycleBin.Delete(Local(link.LocalName!)); } catch { }
        _target.SaveState(state);
        state.Save(_pair);

        // What was converted goes to the remote's trash (recoverable there): the plain originals, or the vault's own files.
        var removed = 0;
        var toRemove = _decrypt
            ? (await _target.GetTreeAsync(_log, ct)).Where(x => IsVaultItem(x.Key) && !x.Key.Contains('/')).Select(x => x.Value).ToList()
            : sourceTree.Values.Where(n => !n.RelPath.Contains('/')).ToList();
        foreach (var node in toRemove)
        {
            try { await (_decrypt ? _target : _source).DeleteAsync(node, ct); removed++; }
            catch (Exception ex) { _log.Warn($"[{_pair.Name}] could not move '{node.RelPath}' to the trash: {ex.Message}"); }
        }
        try { File.Delete(MarkerPath(_pair)); } catch { }
        _log.Info(_decrypt
            ? $"[{_pair.Name}] decrypted: {_copied.Count} file(s) are plain files now; the vault's {removed} item(s) moved to the trash on the other side"
            : $"[{_pair.Name}] encrypted: {_copied.Count} file(s) in the vault; {removed} unencrypted top-level item(s) moved to the trash on the other side");
        ActivityJournal.Add(_pair, _decrypt ? "decrypted" : "encrypted", "", -1,
            _decrypt ? $"{_copied.Count} file(s) moved out of the Cryptomator vault" : $"{_copied.Count} file(s) moved into the Cryptomator vault");
    }
}
