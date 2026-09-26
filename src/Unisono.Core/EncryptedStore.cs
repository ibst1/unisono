using System.Collections.Concurrent;

namespace Unisono;

/// <summary>
/// An encrypted pair's remote side: wraps the real store (Drive, NAS, folder) and translates between the plain names
/// and content the engine works with and the ciphertext the other side holds (see <see cref="CryptoVault"/>). Items
/// whose names are not of this vault (the vault file itself, anything put there by other means) are left out.
/// Remote checksums say nothing about the plain content, so the engine goes by size and date as on a NAS.
/// Google-document features (export, links, write-back) do not exist here: everything is an opaque file.
/// </summary>
public sealed class EncryptedStore : IRemoteStore
{
    private readonly IRemoteStore _inner;
    private readonly CryptoVault _vault;
    private readonly string _pairName;
    private readonly ConcurrentDictionary<string, RemoteNode> _byId = new();
    private readonly ConcurrentDictionary<string, string?> _names = new(StringComparer.Ordinal);
    private int _foreignReported;

    public EncryptedStore(IRemoteStore inner, CryptoVault vault, string pairName)
    {
        _inner = inner;
        _vault = vault;
        _pairName = pairName;
    }

    public string Kind => _inner.Kind + " (encrypted)";
    public void LoadState(SyncState state) => _inner.LoadState(state);
    public void SaveState(SyncState state) => _inner.SaveState(state);
    public int ParallelUploads => _inner.ParallelUploads;
    public bool HasStableIds => _inner.HasStableIds;
    public string RebaseId(string id, string oldFolderId, string newFolderId) => _inner.RebaseId(id, oldFolderId, newFolderId);

    public async Task<Dictionary<string, RemoteNode>> GetTreeAsync(Log log, CancellationToken ct)
    {
        var tree = await _inner.GetTreeAsync(log, ct);
        var plain = new Dictionary<string, RemoteNode>(StringComparer.OrdinalIgnoreCase);
        var foreign = 0;
        foreach (var (rel, node) in tree)
        {
            if (DecryptPath(rel) is { } p) plain[p] = Wrap(node, p);
            else if (!rel.Equals(CryptoVault.VaultFile, StringComparison.OrdinalIgnoreCase)) foreign++;
        }
        if (foreign > 0 && Interlocked.Exchange(ref _foreignReported, 1) == 0)
            log.Warn($"[{_pairName}] {foreign} item(s) on the other side are not encrypted with this pair's passphrase and are left alone");
        return plain;
    }

    private string? DecryptPath(string rel)
    {
        var parts = rel.Split('/');
        for (var i = 0; i < parts.Length; i++)
        {
            var name = _names.GetOrAdd(parts[i], _vault.DecryptName);
            if (name == null) return null;
            parts[i] = name;
        }
        return string.Join("/", parts);
    }

    private RemoteNode Wrap(RemoteNode inner, string plainRel)
    {
        _byId[inner.Id] = inner;
        return new RemoteNode
        {
            Id = inner.Id, Name = PathUtil.NameOf(plainRel), ParentId = inner.ParentId, RelPath = plainRel,
            MimeType = inner.IsFolder ? inner.MimeType : "application/octet-stream",
            Size = inner.IsFolder ? 0 : CryptoVault.PlainSize(inner.Size),
            Md5 = null, // the ciphertext's checksum says nothing about the plain content
            ModifiedUtc = inner.ModifiedUtc,
        };
    }

    private RemoteNode Inner(RemoteNode node) =>
        _byId.TryGetValue(node.Id, out var inner) ? inner : throw new InvalidOperationException($"'{node.RelPath}' is not known on the other side (yet)");

    private RemoteNode? InnerParent(RemoteNode? parent) => parent == null ? null : Inner(parent);

    public async Task<RemoteNode?> RefreshAsync(RemoteNode node, CancellationToken ct) =>
        await _inner.RefreshAsync(Inner(node), ct) is { } fresh ? Wrap(fresh, node.RelPath) : null;

    // Transfers go through a temporary ciphertext file in the data folder (never inside a synced folder).
    private static string TempFile()
    {
        var dir = Path.Combine(AppPaths.DataDir, "crypt-tmp");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, Guid.NewGuid().ToString("N"));
    }

    public async Task DownloadAsync(RemoteNode node, string localPath, CancellationToken ct)
    {
        var enc = TempFile();
        var dec = TempFile();
        try
        {
            await _inner.DownloadAsync(Inner(node), enc, ct);
            await Task.Run(() => _vault.DecryptFile(enc, dec), ct);
            File.SetLastWriteTimeUtc(dec, File.GetLastWriteTimeUtc(enc));
            File.Move(dec, localPath, overwrite: true);
        }
        finally
        {
            TryDelete(enc); TryDelete(dec);
        }
    }

    private async Task<string> EncryptAsync(string localPath, CancellationToken ct)
    {
        var enc = TempFile();
        await Task.Run(() => _vault.EncryptFile(localPath, enc), ct);
        File.SetLastWriteTimeUtc(enc, File.GetLastWriteTimeUtc(localPath)); // the stores carry the modified time over
        return enc;
    }

    public async Task<RemoteNode> UploadNewAsync(string localPath, string relPath, RemoteNode? parent, CancellationToken ct)
    {
        var enc = await EncryptAsync(localPath, ct);
        try { return Wrap(await _inner.UploadNewAsync(enc, _vault.EncryptPath(relPath), InnerParent(parent), ct), relPath); }
        finally { TryDelete(enc); }
    }

    public async Task<RemoteNode> UploadUpdateAsync(string localPath, RemoteNode node, CancellationToken ct)
    {
        var enc = await EncryptAsync(localPath, ct);
        try { return Wrap(await _inner.UploadUpdateAsync(enc, Inner(node), ct), node.RelPath); }
        finally { TryDelete(enc); }
    }

    public async Task<RemoteNode> CreateFolderAsync(string relPath, RemoteNode? parent, CancellationToken ct) =>
        Wrap(await _inner.CreateFolderAsync(_vault.EncryptPath(relPath), InnerParent(parent), ct), relPath);

    public async Task<RemoteNode> MoveAsync(RemoteNode node, string newRelPath, RemoteNode? newParent, CancellationToken ct) =>
        Wrap(await _inner.MoveAsync(Inner(node), _vault.EncryptPath(newRelPath), InnerParent(newParent), ct), newRelPath);

    public Task DeleteAsync(RemoteNode node, CancellationToken ct) => _inner.DeleteAsync(Inner(node), ct);

    public RemoteNode HydrationNode(string rel, string identity, long size)
    {
        var inner = _inner.HydrationNode(_vault.EncryptPath(rel), identity, size);
        _byId[inner.Id] = inner;
        return new RemoteNode { Id = inner.Id, Name = PathUtil.NameOf(rel), RelPath = rel, Size = size, ModifiedUtc = inner.ModifiedUtc };
    }

    public bool CanLink(RemoteNode node) => false;
    public Task<string> LinkUrlAsync(RemoteNode node, CancellationToken ct) => throw new NotSupportedException("an encrypted pair has no links");
    public bool CanImport(RemoteNode node) => false;
    public Task<string?> RevisionAsync(RemoteNode node, CancellationToken ct) => Task.FromResult<string?>(null);
    public Task<string?> WhyNotImportAsync(RemoteNode node, CancellationToken ct) => Task.FromResult<string?>("the pair is encrypted");
    public Task<(RemoteNode? Fresh, string? Revision, string? Why)> PatchAsync(RemoteNode node, string localPath, CancellationToken ct) =>
        Task.FromResult<(RemoteNode?, string?, string?)>((null, null, "the pair is encrypted"));
    public Task<RemoteNode> ImportAsync(RemoteNode node, string localPath, string mime, CancellationToken ct) => throw new NotSupportedException("the pair is encrypted");
    public Task ExportAsync(RemoteNode node, string exportMime, string localPath, CancellationToken ct) => throw new NotSupportedException("the pair is encrypted");

    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
}
