namespace Unisono;

/// <summary>One Drive sign-in and one NAS session per server, shared by all pairs that use them.</summary>
public sealed class StoreFactory : IAsyncDisposable
{
    private readonly Log _log;
    private readonly bool _interactive;
    private DriveClient? _drive;
    private readonly Dictionary<string, SynologyClient> _nas = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="interactive">May open the browser for a Google sign-in. The background host must not.</param>
    public StoreFactory(Log log, bool interactive)
    {
        _log = log;
        _interactive = interactive;
    }

    public async Task<DriveClient> DriveAsync(CancellationToken ct)
    {
        if (_drive != null) return _drive;
        if (!_interactive && !DriveClient.HasStoredToken())
            throw new InvalidOperationException("not signed in to Google; use 'Sign in' in the Unisono window");
        return _drive = await DriveClient.ConnectAsync(ct, quiet: true);
    }

    public async Task<IRemoteStore> CreateAsync(SyncPair pair, CancellationToken ct)
    {
        if (pair.Kind == RemoteKind.Drive)
            return new DriveStore(await DriveAsync(ct), pair);
        return new SynologyStore(await NasAsync(pair, ct), pair);
    }

    public async Task<SynologyClient> NasAsync(SyncPair pair, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(pair.Server) || string.IsNullOrEmpty(pair.User))
            throw new InvalidOperationException($"pair {pair.Name}: Server and User are required for a Synology pair");
        if (_nas.TryGetValue(pair.Server, out var client)) return client;

        var creds = SynologyCredentials.Load(pair.Server)
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
        _nas[pair.Server] = client;
        return client;
    }

    /// <summary>Drop a NAS session after a failure, so the next attempt resolves the address again (e.g. after moving from home to work).</summary>
    public void Forget(SyncPair pair)
    {
        if (pair.Server != null && _nas.Remove(pair.Server, out var c)) c.Dispose();
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
