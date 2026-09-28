namespace Unisono;

/// <summary>One Drive sign-in and one NAS session per server, shared by all pairs that use them.</summary>
public sealed class StoreFactory : IAsyncDisposable
{
    private readonly Log _log;
    private readonly bool _interactive;
    private readonly Dictionary<string, DriveClient> _drive = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SynologyClient> _nas = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="interactive">May open the browser for a Google sign-in. The background host must not.</param>
    public StoreFactory(Log log, bool interactive)
    {
        _log = log;
        _interactive = interactive;
    }

    /// <summary>The Drive connection for a pair's account (e-mail or id; empty = the default account).</summary>
    public async Task<DriveClient> DriveAsync(string? account, CancellationToken ct)
    {
        var acct = GoogleAccounts.Resolve(account)
                   ?? throw new InvalidOperationException(string.IsNullOrEmpty(account)
                       ? "no Google account signed in; add one under Accounts"
                       : $"the Google account {account} is not signed in on this computer; add it under Accounts");
        if (_drive.TryGetValue(acct.Id, out var client)) return client;
        if (!_interactive && !DriveClient.HasStoredToken(acct.Id))
            throw new InvalidOperationException($"the Google account {acct.Email ?? acct.Id} needs to sign in again; use Accounts in the Unisono window");
        return _drive[acct.Id] = await DriveClient.ConnectAsync(acct.Id, ct);
    }

    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Safe to call concurrently: the sync loop and on-open downloads share the connections.</summary>
    public async Task<IRemoteStore> CreateAsync(SyncPair pair, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { return await CreateCoreAsync(pair, ct); }
        finally { _gate.Release(); }
    }

    /// <summary>The pair's remote folder without encryption: what an encrypted pair's conversion copies from.</summary>
    public async Task<IRemoteStore> CreatePlainAsync(SyncPair pair, CancellationToken ct)
    {
        var plain = System.Text.Json.JsonSerializer.Deserialize<SyncPair>(System.Text.Json.JsonSerializer.Serialize(pair))!;
        plain.Encrypted = false;
        return await CreateAsync(plain, ct);
    }

    /// <summary>The pair's remote folder as its Cryptomator vault: what a decryption copies from.</summary>
    public async Task<IRemoteStore> CreateVaultAsync(SyncPair pair, CancellationToken ct)
    {
        var vault = System.Text.Json.JsonSerializer.Deserialize<SyncPair>(System.Text.Json.JsonSerializer.Serialize(pair))!;
        vault.Encrypted = true;
        return await CreateAsync(vault, ct);
    }

    private async Task<IRemoteStore> CreateCoreAsync(SyncPair pair, CancellationToken ct)
    {
        IRemoteStore store = pair.Kind switch
        {
            RemoteKind.Folder => new FolderStore(pair),
            RemoteKind.Drive => new DriveStore(await DriveAsync(pair.Account, ct), pair),
            _ => new SynologyStore(await NasAsync(pair, ct), pair),
        };
        if (!pair.Encrypted) return store;
        var key = CryptomatorVault.LoadKey(pair.VaultId)
                  ?? throw new InvalidOperationException("the passphrase of this encrypted pair is not stored on this computer: open the pair (Edit) and enter it");
        return new CryptomatorStore(store, new CryptomatorVault(key), pair, _log);
    }

    public async Task<SynologyClient> NasAsync(SyncPair pair, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(pair.Server) || string.IsNullOrEmpty(pair.User))
            throw new InvalidOperationException($"pair {pair.Name}: Server and User are required for a Synology pair");
        var key = pair.Server + "|" + pair.User;
        if (_nas.TryGetValue(key, out var client)) return client;

        var creds = SynologyCredentials.Load(pair.Server, pair.User)
                    ?? throw new InvalidOperationException($"no stored login for {pair.Server}; add it under Synology NAS in the window (or 'UnisonoCli nas login')");
        if (!creds.User.Equals(pair.User, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"the stored login for {pair.Server} is for '{creds.User}', the pair wants '{pair.User}'");
        try
        {
            client = await SynologyClient.ConnectAsync(creds, creds.AllowInvalidCertificate || pair.AllowInvalidCertificate, otpPrompt: null, ct, _log.Debug);
        }
        catch (SynologyApiException ex) when (ex.Code is 403 or 404 or 406)
        {
            throw new InvalidOperationException($"{pair.Server} asks for a two-factor code; log in to the NAS again from the window so a device token is stored", ex);
        }
        creds.Save(); // resolved address / device token may have changed
        _log.Debug($"NAS session on {client.BaseUrl} as {creds.User}");
        _nas[key] = client;
        return client;
    }

    /// <summary>Drop a NAS session after a failure, so the next attempt resolves the address again (e.g. after moving from home to work).</summary>
    public void Forget(SyncPair pair)
    {
        if (pair.Server != null && _nas.Remove(pair.Server + "|" + pair.User, out var c)) c.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var c in _nas.Values)
        {
            await c.LogoutAsync(CancellationToken.None);
            c.Dispose();
        }
        _nas.Clear();
    }
}
