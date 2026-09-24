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
    public async Task<IRemoteStore> CreateAsync(SyncPair pair, CancellationToken ct)
    {
        if (pair.Kind == RemoteKind.Folder)
            return new FolderStore(pair);
        if (pair.Kind == RemoteKind.Drive)
            return new DriveStore(await DriveAsync(pair.Account, ct), pair);
        return new SynologyStore(await NasAsync(pair, ct), pair);
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
