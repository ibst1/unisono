using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;

namespace Unisono.App;

/// <summary>
/// Everything the web UI can ask of the app. The page sends {id, method, params}; <see cref="HandleAsync"/> returns
/// the result (or throws, which becomes an error reply). Runs on the UI thread, so native dialogs can be shown.
/// </summary>
sealed class Bridge : IAsyncDisposable
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "Unisono";

    private readonly Form _owner;
    private readonly Log _log;
    private readonly SyncHost _host;
    private readonly AppSettings _settings;
    private readonly Func<Task> _restartHost;
    private readonly Action _pushState;
    private readonly ConcurrentDictionary<string, GoogleInfo> _google = new();
    private readonly Dictionary<string, DriveClient> _drives = new();
    private readonly Dictionary<string, SynologyClient> _nas = new();
    private CancellationTokenSource? _googleSignIn;

    public Config Config { get; private set; } = Config.Load();

    /// <summary>Re-reads config.json (and the shared pair list): another computer may have changed the list.</summary>
    public void Reload() => Config = Config.Load();

    private sealed record GoogleInfo(string? Email, long Usage, long Limit, string? Error);

    /// <summary>Google Docs API for the OAuth client's project: null = not checked yet / unknown.</summary>
    private (bool Enabled, string? Url)? _docsApi;

    public Bridge(Form owner, Log log, SyncHost host, AppSettings settings, Func<Task> restartHost, Action pushState)
    {
        _owner = owner;
        _log = log;
        _host = host;
        _settings = settings;
        _restartHost = restartHost;
        _pushState = pushState;
    }

    public async Task<object?> HandleAsync(string method, JsonElement p)
    {
        switch (method)
        {
            case "state": return State();
            case "clientError": _log.Error($"window: {Str(p, "message")} [{Str(p, "stack")}]"); return null;
            case "tab": _settings.Tab = Str(p, "tab") ?? "accounts"; _settings.Save(); return null;

            // ---------------------------------------------------- google
            case "google.setClient": return ChooseClientSecret();
            case "google.add": return await AddGoogleAsync();
            case "google.cancel": _googleSignIn?.Cancel(); return null;
            case "google.remove":
            {
                var id = Str(p, "id")!;
                GoogleAccounts.Remove(id);
                _drives.Remove(id);
                _google.TryRemove(id, out _);
                _log.Info("removed Google account " + id);
                await _restartHost();
                return null;
            }
            case "google.refresh": _ = RefreshGoogleInfoAsync(force: true); return null;

            // ------------------------------------------------------- NAS
            case "nas.login": return await NasLoginAsync(p);
            case "nas.remove":
            {
                SynologyCredentials.Delete(Str(p, "server")!, Str(p, "user")!);
                _log.Info($"forgot NAS login {Str(p, "user")} @ {Str(p, "server")}");
                await _restartHost();
                return null;
            }

            // ----------------------------------------------------- pairs
            case "browse": return await BrowseAsync(p);
            case "browse.mkdir": return await MakeFolderAsync(p);
            case "pickLocal":
            {
                using var d = new FolderBrowserDialog { Description = Str(p, "title") ?? "Local folder to keep in sync", UseDescriptionForTitle = true, ShowNewFolderButton = true };
                if (Str(p, "current") is { Length: > 0 } cur && Directory.Exists(Environment.ExpandEnvironmentVariables(cur))) d.SelectedPath = Environment.ExpandEnvironmentVariables(cur);
                return d.ShowDialog(_owner) == DialogResult.OK ? d.SelectedPath : null;
            }
            case "pair.save": return await SavePairAsync(p);
            case "pair.check": _host.RequestCheck(FindPair(Str(p, "id")).Id); _pushState(); return null;
            case "pair.checkFix":
            {
                var st = _host.Pairs.FirstOrDefault(s => s.Pair.Id == Str(p, "id")) ?? throw new InvalidOperationException("That pair no longer exists.");
                if (st.LastCheck is { Problems.Count: > 0 } r) _host.ForgetHistory(st.Pair.Id, r.Problems.Select(x => x.Rel).Distinct());
                if (st.LastCheck != null) st.LastCheck.Problems.Clear();
                _pushState();
                return null;
            }
            case "pair.checkDismiss":
            {
                if (_host.Pairs.FirstOrDefault(s => s.Pair.Id == Str(p, "id"))?.LastCheck is { } r) r.Problems.Clear();
                _pushState();
                return null;
            }
            case "pair.frequent": return FrequentChoice(Str(p, "id"), Str(p, "rel") ?? "", Str(p, "action"));
            case "pair.ignoreFile": return ReadIgnoreFile(Str(p, "localPath"), Str(p, "id") is { } iid ? Config.Pairs.FirstOrDefault(x => x.Id == iid) : null);
            case "ondemand.check":
            {
                var raw = Str(p, "path") ?? "";
                var full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(raw));
                var self = Str(p, "id") is { } sid ? Config.Pairs.FirstOrDefault(x => x.Id == sid) : null;
                return new { why = CloudFiles.WhyNot(full, self is { OnDemand: true } ? self : null) };
            }
            case "versions.list":
            {
                var pair = FindPair(Str(p, "id"));
                return VersionStore.List(pair).Select(f => new
                {
                    rel = f.Rel,
                    exists = File.Exists(Path.Combine(pair.LocalFullPath, f.Rel.Replace('/', '\\'))),
                    versions = f.Versions.Select(v => new { path = v.CopyPath, taken = VersionLabel(v), size = v.Size }),
                });
            }
            case "versions.restore":
            {
                var pair = FindPair(Str(p, "id"));
                VersionStore.Restore(pair, Str(p, "rel")!, Str(p, "path")!, Config.Options);
                _log.Info($"[{pair.Name}] restored an earlier version: {Str(p, "rel")}");
                _host.SyncNow();
                return null;
            }
            case "vault.restoreFiles": return await RestoreVaultFilesAsync(FindPair(Str(p, "id")));
            case "vault.recover":
            {
                var pair = FindPair(Str(p, "id"));
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                await SetUpVaultAsync(pair, Str(p, "passphrase"), false, Str(p, "recoveryKey"), cts.Token);
                Config.Save();
                await _restartHost();
                return null;
            }
            case "vault.recoveryKey":
            {
                var pair = FindPair(Str(p, "id"));
                var keys = CryptomatorVault.LoadKey(pair.VaultId) ?? throw new InvalidOperationException("This computer does not have the vault's key.");
                _log.Info($"[{pair.Name}] the recovery key was shown");
                return CryptomatorVault.RecoveryKey(keys);
            }
            case "backup.versions":
            {
                var st = _host.Pairs.FirstOrDefault(s => s.Pair.Id == Str(p, "id"));
                return (st?.Versions ?? new()).Select(v => new { rel = v.Rel, versionRel = v.VersionRel, taken = v.Taken.ToString("yyyy-MM-dd HH:mm:ss"), size = v.Size, isFolder = v.IsFolder }).ToList();
            }
            case "backup.restore":
            {
                var pair = FindPair(Str(p, "id"));
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(30));
                var rel = await _host.RestoreAsync(pair.Id, Str(p, "versionRel")!).WaitAsync(cts.Token);
                return new { rel };
            }
            case "activity.days": return ActivityJournal.Days().Select(d => d.ToString("yyyy-MM-dd")).ToList();
            case "activity.list":
            {
                var day = DateTime.TryParse(Str(p, "day"), out var dd) ? dd.Date : DateTime.Today;
                var pairs = Config.Pairs.ToDictionary(x => x.Id);
                return await Task.Run(() => ActivityJournal.Read(day).Take(5000).Select(e =>
                {
                    // Whether the item is still there, for "Show".
                    var exists = pairs.TryGetValue(e.PairId, out var pair) && pair.LocalPath.Length > 0
                                 && Path.Combine(pair.LocalFullPath, e.Rel.TrimEnd('/').Replace('/', '\\')) is var full && (File.Exists(full) || Directory.Exists(full));
                    return new { time = e.Time.ToString("HH:mm:ss"), pairId = e.PairId, pair = e.Pair, action = e.Action, rel = e.Rel, size = e.Size, detail = e.Detail,
                        exists, pairKnown = pair != null };
                }).ToList());
            }
            case "conflicts.list": return await Task.Run(() => Conflicts.Find(Config.Pairs));
            case "conflicts.resolve":
            {
                var pair = FindPair(Str(p, "id"));
                _log.Info($"[{pair.Name}] " + Conflicts.Resolve(pair, Str(p, "rel")!, Str(p, "action")!, Config.Options));
                return null;
            }
            case "conflicts.open":
            {
                // A file of a pair, opened in its program (the conflict copy or the original).
                var pair = FindPair(Str(p, "id"));
                var root = pair.LocalFullPath.TrimEnd('\\');
                var path = Path.GetFullPath(Path.Combine(root, (Str(p, "rel") ?? "").Replace('/', '\\')));
                if (!path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) throw new InvalidOperationException("That file is not there.");
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                return null;
            }
            case "versions.open":
            {
                var path = Str(p, "path")!;
                if (!Path.GetFullPath(path).StartsWith(Path.GetFullPath(VersionStore.Root), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Not a kept version.");
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                return null;
            }
            case "pair.preview":
            {
                // A dry run: every decision of a real cycle, none of its effects (the state file is not written either).
                var pair = FindPair(Str(p, "id"));
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
                await using var stores = new StoreFactory(_log, interactive: false);
                var engine = new SyncEngine(await stores.CreateAsync(pair, cts.Token), pair, _log, Config.Pairs, Config.Options) { DryRun = true };
                await engine.RunCycleAsync(cts.Token);
                return new { actions = engine.Plan.Select(a => new { action = a.Action, path = a.Path, detail = a.Detail }) };
            }
            case "pair.remove":
            {
                var pair = FindPair(Str(p, "id"));
                if (pair.OnDemand)
                {
                    // Placeholders need their provider: turn them back into ordinary files (online-only ones are dropped;
                    // they stay online) while nothing is syncing.
                    await _host.StopAsync();
                    CloudFiles.TurnOff(pair, _log);
                }
                Config.Pairs.Remove(pair);
                Config.Save();
                // Without its history a re-added pair starts by matching content, which is the safe way round.
                try { File.Delete(SyncState.FilePathFor(pair)); } catch { }
                _log.Info($"removed pair {pair.Name}");
                await _restartHost();
                return null;
            }

            // ---------------------------------------------- sync, settings
            case "sync.now": _host.SyncNow(); return null;
            case "sync.pause": _host.Paused = p.GetProperty("paused").GetBoolean(); _pushState(); return null;
            case "settings.set":
            {
                if (p.TryGetProperty("autostart", out var a)) SetAutostart(a.GetBoolean());
                if (p.TryGetProperty("verbose", out var v)) { _settings.VerboseLog = _log.Verbose = v.GetBoolean(); _settings.Save(); }
                var o = Config.Options;
                var changed = false;
                if (p.TryGetProperty("keepVersions", out var kv) && kv.TryGetInt32(out var kvi)) { o.KeepVersions = Math.Clamp(kvi, 0, 1000); changed = true; }
                if (p.TryGetProperty("keepVersionsDays", out var kd) && kd.TryGetInt32(out var kdi)) { o.KeepVersionsDays = Math.Clamp(kdi, 0, 36500); changed = true; }
                if (p.TryGetProperty("checkContentDays", out var cd) && cd.TryGetInt32(out var cdi)) { o.CheckContentDays = Math.Clamp(cdi, 0, 365); changed = true; }
                if (p.TryGetProperty("versionsFolder", out var vf))
                {
                    var raw = vf.ValueKind == JsonValueKind.String ? vf.GetString()?.Trim() : null;
                    var full = string.IsNullOrEmpty(raw) ? null : Path.GetFullPath(Environment.ExpandEnvironmentVariables(raw));
                    if (full != null && Config.Pairs.FirstOrDefault(x => WithSep(full).StartsWith(WithSep(x.LocalFullPath), StringComparison.OrdinalIgnoreCase)) is { } inside)
                        throw new InvalidOperationException($"That folder lies inside {inside.LocalFullPath}, which Unisono syncs: the kept versions would be synced too. Pick a folder outside your sync pairs.");
                    var oldRoot = VersionStore.Root;
                    o.VersionsFolder = full == null ? null : SyncPair.Portable(full);
                    VersionStore.Folder = o.VersionsFolder;
                    await _host.StopAsync(); // nothing may write versions while they move
                    VersionStore.MoveAll(oldRoot, VersionStore.Root);
                    _log.Info($"previous versions now kept in {VersionStore.Root}");
                    changed = true;
                }
                if (p.TryGetProperty("pauseOnMetered", out var pm)) { o.PauseOnMetered = pm.GetBoolean(); changed = true; }
                if (p.TryGetProperty("pauseWhilePresenting", out var pp)) { o.PauseWhilePresenting = pp.GetBoolean(); changed = true; }
                if (Str(p, "ignorePatterns") is { } ig) { o.IgnorePatterns = Lines(ig); changed = true; }
                if (p.TryGetProperty("explorerMenu", out var em)) { o.ExplorerMenu = em.GetBoolean(); changed = true; }
                if (changed)
                {
                    Config.Save();
                    await _restartHost(); // engines take the options when they are created
                }
                return State();
            }
            case "open":
            {
                var what = Str(p, "what");
                if (what == "log") Ui.OpenFolder(_log.Directory);
                else if (what == "data") Ui.OpenFolder(AppPaths.DataDir);
                else if (what == "pair") Ui.OpenFolder(FindPair(Str(p, "id")).LocalFullPath);
                else if (what == "remote" && FindPair(Str(p, "id")) is { Kind: RemoteKind.Folder } fp) Ui.OpenFolder(fp.RemoteFullPath);
                else if (what == "versions")
                {
                    var dir = Str(p, "id") is { } vid ? VersionStore.DirFor(FindPair(vid)) : VersionStore.Root;
                    Directory.CreateDirectory(dir);
                    Ui.OpenFolder(dir);
                }
                else if (what == "item") RevealItem(Str(p, "pair"), Str(p, "rel"));
                else if (what == "url" && Str(p, "url") is { } url && url.StartsWith("https://", StringComparison.Ordinal))
                    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                return null;
            }
            default: throw new InvalidOperationException("unknown request " + method);
        }
    }

    // ------------------------------------------------------------------ state

    public object State() => new
    {
        version = Ui.Version,
        machine = Environment.MachineName,
        dataDir = AppPaths.DataDir,
        oauthClient = OAuthInfo(),
        docsApi = _docsApi == null ? null : new { enabled = _docsApi.Value.Enabled, url = _docsApi.Value.Url },
        google = GoogleAccounts.Load().Select(a => new
        {
            id = a.Id,
            email = a.Email ?? (_google.TryGetValue(a.Id, out var gi) ? gi.Email : null),
            signedIn = DriveClient.HasStoredToken(a.Id),
            usage = _google.TryGetValue(a.Id, out var i1) ? i1.Usage : 0,
            limit = _google.TryGetValue(a.Id, out var i2) ? i2.Limit : 0,
            error = _google.TryGetValue(a.Id, out var i3) ? i3.Error : null,
            isDefault = a.Id == GoogleAccounts.Resolve(null)?.Id,
        }),
        nas = SynologyCredentials.ListAll().Select(c => new
        {
            server = c.Server, user = c.User, resolved = c.ResolvedBase, pinned = c.PinnedCertificates.Count,
        }),
        pairs = Config.Pairs.Select(PairDto),
        host = new { state = _host.State.ToString(), paused = _host.Paused, pausedBecause = _host.PausedBecause },
        settings = new { autostart = IsAutostart(), verbose = _settings.VerboseLog, tab = _settings.Tab },
        options = new
        {
            keepVersions = Config.Options.KeepVersions, keepVersionsDays = Config.Options.KeepVersionsDays, checkContentDays = Config.Options.CheckContentDays,
            pauseOnMetered = Config.Options.PauseOnMetered, pauseWhilePresenting = Config.Options.PauseWhilePresenting,
            ignorePatterns = string.Join("\n", Config.Options.IgnorePatterns), metered = AutoPause.IsMetered(),
            versionsDir = VersionStore.Root, versionsFolder = Config.Options.VersionsFolder ?? "", versionsDefault = string.IsNullOrWhiteSpace(Config.Options.VersionsFolder),
            explorerMenu = Config.Options.ExplorerMenu,
        },
    };

    private object PairDto(SyncPair p)
    {
        var st = _host.Pairs.FirstOrDefault(s => s.Pair.Id == p.Id);
        return new
        {
            id = p.Id, kind = p.Kind.ToString(), name = p.Name, account = p.Account, server = p.Server, user = p.User,
            onDemand = p.OnDemand, remotePath = p.RemotePath, remoteFullPath = p.RemoteFullPath, localPath = p.LocalFullPath, docs = p.GoogleDocs.ToString(),
            
            cloudOnlyMinMB = p.CloudOnlyMinMB, cloudOnlyFolders = p.CloudOnlyFolders, pollSeconds = p.PollSeconds,
            includeFolders = p.IncludeFolders, excludeFolders = p.ExcludeFolders,
            direction = p.Direction.ToString(), ignorePatterns = string.Join("\n", p.IgnorePatterns),
            nested = SyncScope.NestedPaths(p, Config.Pairs).ToList(),
            status = st?.Status ?? "waiting", waitingFor = st?.Status == "syncing" ? null : st?.WaitingFor,
            lastSync = st?.LastSync is { } ls ? ls.ToString(ls.Date == DateTime.Today ? "HH:mm:ss" : "d MMM HH:mm") : null,
            lastResult = st?.LastError ?? Summary(st?.LastStats), failed = st?.LastError != null,
            frequent = st == null ? new List<string>() : Locked(st.Frequent), frequentOk = p.FrequentOk, health = st?.Health,
            encrypted = p.Encrypted, keyHere = !p.Encrypted || CryptomatorVault.LoadKey(p.VaultId) != null,
            needsPassphrase = st?.NeedsPassphrase ?? false, vaultGone = st?.VaultGone ?? false, converting = VaultConverter.Pending(p),
            decrypting = VaultConverter.Pending(p) && VaultConverter.PendingDecrypt(p),
            checking = st?.Checking ?? false, running = st?.Running ?? false,
            lastChecked = st?.LastChecked is { } lc ? lc.ToString(lc.Date == DateTime.Today ? "HH:mm" : "d MMM") : null,
            checkProblems = st?.LastCheck?.Problems.Select(x => new { rel = x.Rel, side = x.Side, detail = x.Detail }).ToList(),
        };
    }

    private static object? OAuthInfo()
    {
        if (!DriveClient.HasClientSecret()) return null;
        try
        {
            var i = OAuthClientInfo.Read(AppPaths.ClientSecretPath);
            return new { project = i.ProjectId, clientId = i.ClientId, desktop = i.Kind == "installed" };
        }
        catch (Exception ex) { return new { project = (string?)null, clientId = "unreadable: " + ex.Message, desktop = false }; }
    }

    private static string Summary(CycleStats? s)
    {
        if (s == null) return "";
        var parts = new List<string>();
        if (s.Downloaded > 0) parts.Add($"{s.Downloaded} down");
        if (s.Uploaded > 0) parts.Add($"{s.Uploaded} up");
        if (s.Exported > 0) parts.Add($"{s.Exported} exported");
        if (s.Linked > 0) parts.Add($"{s.Linked} linked");
        if (s.Trashed > 0) parts.Add($"{s.Trashed} deleted remotely");
        if (s.Recycled > 0) parts.Add($"{s.Recycled} to Recycle Bin");
        if (s.Conflicts > 0) parts.Add($"{s.Conflicts} conflict(s)");
        return parts.Count == 0 ? "no changes" : string.Join(", ", parts);
    }

    // ----------------------------------------------------------------- google

    private object? ChooseClientSecret()
    {
        using var d = new OpenFileDialog
        {
            Title = "OAuth client JSON downloaded from Google Cloud Console (Desktop app)",
            Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
        };
        if (d.ShowDialog(_owner) != DialogResult.OK) return null;
        var info = OAuthClientInfo.Read(d.FileName); // throws on a file that is not a client secret
        AppPaths.EnsureDataDir();
        File.Copy(d.FileName, AppPaths.ClientSecretPath, overwrite: true);
        _drives.Clear();
        _log.Info($"OAuth client set: project {info.ProjectId ?? "?"}, client {info.ClientId}");
        return State();
    }

    private async Task<object> AddGoogleAsync()
    {
        if (!DriveClient.HasClientSecret()) throw new InvalidOperationException("Choose the OAuth client JSON first (see README, one-time setup).");
        _googleSignIn?.Cancel();
        using var cts = _googleSignIn = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var id = GoogleAccounts.NewId();
        try
        {
            var drive = await DriveClient.ConnectAsync(id, cts.Token, quiet: true, chooseAccount: true);
            var (email, usage, limit, _) = await drive.WhoAmIAsync(cts.Token);
            var acct = GoogleAccounts.Upsert(id, email);
            _drives[acct.Id] = drive;
            _google[acct.Id] = new GoogleInfo(email, usage, limit, null);
            _log.Info($"signed in to Google as {email}");
            await _restartHost();
            return new { email, duplicate = acct.Id != id };
        }
        catch
        {
            DriveClient.SignOut(id); // never leave a half-finished token behind
            throw;
        }
        finally
        {
            _googleSignIn = null;
        }
    }

    /// <summary>Fills in e-mail and quota for each account in the background, then refreshes the page.</summary>
    public async Task RefreshGoogleInfoAsync(bool force = false)
    {
        foreach (var a in GoogleAccounts.Load())
        {
            if (!force && _google.TryGetValue(a.Id, out var known) && known.Error == null) continue; // errors are retried
            if (!DriveClient.HasStoredToken(a.Id)) continue;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var drive = await DriveFor(a.Id, cts.Token);
                var (email, usage, limit, _) = await drive.WhoAmIAsync(cts.Token);
                if (a.Email == null) GoogleAccounts.Upsert(a.Id, email);
                _google[a.Id] = new GoogleInfo(email, usage, limit, null);
                if (force || _docsApi is not { Enabled: true }) _docsApi = await drive.DocsApiStatusAsync(cts.Token) ?? _docsApi;
            }
            catch (Exception ex)
            {
                _google[a.Id] = new GoogleInfo(a.Email, 0, 0, Ui.Explain(ex));
            }
        }
        _pushState();
    }

    private async Task<DriveClient> DriveFor(string? account, CancellationToken ct)
    {
        var acct = GoogleAccounts.Resolve(account) ?? throw new InvalidOperationException("No Google account signed in; add one under Accounts.");
        if (_drives.TryGetValue(acct.Id, out var d)) return d;
        if (!DriveClient.HasStoredToken(acct.Id)) throw new InvalidOperationException($"{acct.Email ?? acct.Id} needs to sign in again (Accounts).");
        return _drives[acct.Id] = await DriveClient.ConnectAsync(acct.Id, ct);
    }

    // -------------------------------------------------------------------- NAS

    /// <summary>
    /// One login attempt. The page calls again with an OTP code or with the fingerprints the user agreed to trust, so no
    /// prompt ever blocks here. Returns {status: ok | needOtp | untrusted}.
    /// </summary>
    private async Task<object> NasLoginAsync(JsonElement p)
    {
        var raw = Str(p, "server") ?? "";
        var user = (Str(p, "user") ?? "").Trim();
        var password = Str(p, "password") ?? "";
        if (raw.Trim().Length == 0 || user.Length == 0) throw new InvalidOperationException("Fill in server and user.");
        var server = SynologyClient.NormalizeServer(raw);
        if (!server.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Use an https:// address; the password must not travel unencrypted.");

        var existing = SynologyCredentials.Load(server, user);
        if (password.Length == 0) password = existing?.Password ?? throw new InvalidOperationException("Fill in the password.");
        var creds = new SynologyCredentials
        {
            Server = server, User = user, Password = password,
            DeviceId = existing?.DeviceId, ResolvedBase = existing?.ResolvedBase,
            PinnedCertificates = existing?.PinnedCertificates.ToList() ?? new(),
            AllowInvalidCertificate = existing?.AllowInvalidCertificate ?? false,
        };
        if (p.TryGetProperty("trust", out var trust) && trust.ValueKind == JsonValueKind.Array)
            foreach (var t in trust.EnumerateArray())
                if (t.GetString() is { } sha && !creds.PinnedCertificates.Contains(sha, StringComparer.OrdinalIgnoreCase))
                    creds.PinnedCertificates.Add(sha);
        var otp = Str(p, "otp");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var needOtp = false;
        try
        {
            using var nas = await SynologyClient.ConnectAsync(creds, creds.AllowInvalidCertificate,
                _ => { if (string.IsNullOrWhiteSpace(otp)) { needOtp = true; return Task.FromResult<string?>(null); } return Task.FromResult<string?>(otp); },
                cts.Token, _log.Info);
            var shares = await nas.ListAsync("/", cts.Token);
            await nas.LogoutAsync(cts.Token);
            creds.Save();
            _log.Info($"NAS login stored for {creds.User} @ {creds.Server} (answered at {nas.BaseUrl}); shared folders: {string.Join(", ", shares.Select(s => s.Path))}");
            await _restartHost();
            return new { status = "ok", server = creds.Server, shares = shares.Select(s => s.Path) };
        }
        catch (UntrustedCertificateException ex)
        {
            var certs = ex.Distinct.Where(c => !creds.PinnedCertificates.Contains(c.Sha256, StringComparer.OrdinalIgnoreCase)).ToList();
            if (certs.Count == 0) throw;
            return new
            {
                status = "untrusted",
                certs = certs.Select(c => new { address = c.Address, subject = c.Subject, issuer = c.Issuer, problem = c.Problem, sha256 = c.Sha256, fingerprint = c.FingerprintForDisplay }),
            };
        }
        catch (SynologyApiException ex) when (needOtp && ex.Code is 403 or 404 or 406)
        {
            return new { status = "needOtp" };
        }
    }

    private async Task<SynologyClient> NasFor(string server, string user, CancellationToken ct)
    {
        var key = server + "|" + user;
        if (_nas.TryGetValue(key, out var c)) return c;
        var creds = SynologyCredentials.Load(server, user) ?? throw new InvalidOperationException($"No stored login for {user} @ {server}.");
        c = await SynologyClient.ConnectAsync(creds, creds.AllowInvalidCertificate, null, ct, _log.Debug);
        creds.Save();
        return _nas[key] = c;
    }

    // ----------------------------------------------------------------- browse

    /// <summary>Child folders of a remote folder, for the folder picker. key null = the top level.</summary>
    private async Task<object> BrowseAsync(JsonElement p)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var key = Str(p, "key");
        if (Str(p, "kind") == "Folder")
        {
            // key = the folder's full path; path = how the page spells it (the pair's remote path + "/" + names).
            var dir = new DirectoryInfo(Environment.ExpandEnvironmentVariables(key ?? Str(p, "path") ?? ""));
            var shownPath = Str(p, "path") ?? "";
            return dir.EnumerateDirectories()
                .Where(d => (d.Attributes & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint)) == 0)
                .OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(d => new { name = d.Name, key = d.FullName, path = shownPath.TrimEnd('/', '\\') + "/" + d.Name })
                .ToList();
        }
        if (Str(p, "kind") == "Synology")
        {
            var nas = await NasFor(Str(p, "server")!, Str(p, "user")!, cts.Token);
            var items = await nas.ListAsync(key ?? "/", cts.Token);
            return items.Where(e => e.IsDir && !e.Name.StartsWith('#') && e.Name != "@eaDir")
                .OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(e => new { name = e.Name, key = e.Path, path = e.Path });
        }
        var drive = await DriveFor(Str(p, "account"), cts.Token);
        var parentPath = Str(p, "path") ?? "";
        // The selective-sync picker starts at the pair's remote folder, known only by path.
        key ??= parentPath.Length == 0 ? "root" : (await drive.ResolveFolderAsync(parentPath, cts.Token)).Id;
        var children = await drive.ListChildrenAsync(key, cts.Token);
        return children.Where(DriveClient.IsFolder)
            .OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(f => new { name = f.Name, key = f.Id, path = PathUtil.Join(parentPath, f.Name) });
    }

    /// <summary>New folder from the folder picker, inside the folder given like for <see cref="BrowseAsync"/>. Returns the same shape as a browse item.</summary>
    private async Task<object> MakeFolderAsync(JsonElement p)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        var name = (Str(p, "name") ?? "").Trim();
        if (name.Length == 0 || name is "." or ".." || name.IndexOfAny(@"\/:*?""<>|".ToCharArray()) >= 0)
            throw new InvalidOperationException("That is not a usable folder name.");
        var key = Str(p, "key");
        if (Str(p, "kind") == "Synology")
        {
            if (key == null) throw new InvalidOperationException("Select the shared folder to create it in first.");
            var nas = await NasFor(Str(p, "server")!, Str(p, "user")!, cts.Token);
            var made = await nas.CreateFolderAsync(key, name, cts.Token);
            return new { name = made.Name, key = made.Path, path = made.Path };
        }
        var drive = await DriveFor(Str(p, "account"), cts.Token);
        var parentPath = Str(p, "path") ?? "";
        key ??= parentPath.Length == 0 ? "root" : (await drive.ResolveFolderAsync(parentPath, cts.Token)).Id;
        // Drive allows two folders with one name; reuse the existing one instead of making a twin.
        var existing = (await drive.ListChildrenAsync(key, cts.Token))
            .FirstOrDefault(f => DriveClient.IsFolder(f) && f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        var folder = existing ?? await drive.CreateFolderAsync(name, key, cts.Token);
        _log.Info($"Folder picker: {(existing != null ? "already there" : "created")} {PathUtil.Join(parentPath, folder.Name)} on {Str(p, "account") ?? "Google Drive"}");
        return new { name = folder.Name, key = folder.Id, path = PathUtil.Join(parentPath, folder.Name) };
    }

    // ------------------------------------------------------------------ pairs

    private SyncPair FindPair(string? id) =>
        Config.Pairs.FirstOrDefault(x => x.Id == id) ?? throw new InvalidOperationException("That pair no longer exists.");

    /// <summary>
    /// Validates and stores a pair. Anything that needs the user's say (two non-empty folders on a first sync, a
    /// target inside OneDrive or Work Folders) comes back as {status:"confirm", messages}, and the page sends the same
    /// request again with confirmed=true.
    /// </summary>
    private async Task<object> SavePairAsync(JsonElement p)
    {
        var d = p.GetProperty("pair");
        var original = Str(p, "originalId") is { } oid ? FindPair(oid) : null;
        var confirmed = p.TryGetProperty("confirmed", out var cf) && cf.GetBoolean();
        var create = p.TryGetProperty("create", out var cr) && cr.GetBoolean();

        var pair = original == null ? new SyncPair() : JsonSerializer.Deserialize<SyncPair>(JsonSerializer.Serialize(original))!;
        pair.Kind = Enum.TryParse<RemoteKind>(Str(d, "kind"), out var kind) ? kind : RemoteKind.Drive;
        var encrypt = d.TryGetProperty("encrypted", out var en) && en.ValueKind == JsonValueKind.True;
        var passphrase = Str(d, "passphrase");
        var recoveryKey = Str(d, "recoveryKey");
        // Turning encryption off: the vault is decrypted in place (VaultConverter), or, if it is gone already, the pair
        // simply syncs the plain folder.
        var turningOff = original is { Encrypted: true } && !encrypt;
        // Turning encryption on for an existing pair works while its remote folder is still empty (checked with the vault).
        var turningOn = original is { Encrypted: false } && encrypt;
        if (encrypt && (original == null || turningOn) && string.IsNullOrEmpty(passphrase))
            throw new InvalidOperationException("Choose a passphrase for the encryption.");
        pair.Encrypted = encrypt;
        if (pair.Kind == RemoteKind.Folder)
        {
            // Written like the local folder: unchanged stays as it was (older pairs may say %USERPROFILE%), new as a full path.
            var other = (Str(d, "remotePath") ?? "").Trim();
            var otherFull = other.Length == 0 ? "" : Path.GetFullPath(Environment.ExpandEnvironmentVariables(other)).TrimEnd('\\');
            pair.RemotePath = original is { Kind: RemoteKind.Folder } && original.RemoteFullPath.Equals(otherFull, StringComparison.OrdinalIgnoreCase)
                ? original.RemotePath : otherFull;
        }
        else
        {
            var remote = (Str(d, "remotePath") ?? "").Trim().Replace('\\', '/');
            pair.RemotePath = pair.Kind == RemoteKind.Synology ? "/" + remote.Trim('/') : remote.Trim('/');
        }
        var local = (Str(d, "localPath") ?? "").Trim();
        var localFull = local.Length == 0 ? "" : Path.GetFullPath(Environment.ExpandEnvironmentVariables(local));
        // Unchanged folder: keep how it was written (the pair's identity and history depend on it; older pairs may say
        // %USERPROFILE%). New or changed: the full path, as the window shows it.
        pair.LocalPath = original != null && original.LocalFullPath.Equals(localFull, StringComparison.OrdinalIgnoreCase)
            ? original.LocalPath : localFull;
        pair.GoogleDocs = Enum.TryParse<GoogleDocsMode>(Str(d, "docs"), out var docs) ? docs : GoogleDocsMode.Export;
        pair.Direction = Enum.TryParse<SyncDirection>(Str(d, "direction"), out var dir) ? dir : SyncDirection.TwoWay;
        pair.OnDemand = d.TryGetProperty("onDemand", out var od) && od.ValueKind == JsonValueKind.True;
        var ignoreText = Str(d, "ignorePatterns"); // written to the folder's .unisonoignore below
        pair.IncludeFolders = StrList(d, "includeFolders");
        pair.ExcludeFolders = StrList(d, "excludeFolders");
        if (d.TryGetProperty("frequentOk", out _)) pair.FrequentOk = StrList(d, "frequentOk");
        pair.PollSeconds = Math.Clamp(d.TryGetProperty("pollSeconds", out var ps) && ps.TryGetInt32(out var pv) ? pv : 60, 10, 3600);
        if (pair.Kind == RemoteKind.Folder)
        {
            pair.Server = pair.User = pair.Account = null;
            pair.AllowInvalidCertificate = false;
            pair.CloudOnlyMinMB = 0;
            pair.CloudOnlyFolders = new();
        }
        else if (pair.Kind == RemoteKind.Synology)
        {
            pair.Server = Str(d, "server");
            pair.User = Str(d, "user");
            pair.Account = null;
            pair.CloudOnlyMinMB = 0;
            pair.CloudOnlyFolders = new();
        }
        else
        {
            pair.Server = pair.User = null;
            pair.AllowInvalidCertificate = false;
            // New pairs name their account explicitly. A pair from before multi-account support (Account empty = the
            // default account) keeps it empty while it stays on that account, so its identity and history are kept.
            var acct = Str(d, "account");
            if (string.IsNullOrEmpty(acct)) pair.Account = null;
            else if (original is { Account: null } && string.Equals(GoogleAccounts.Resolve(null)?.Email, acct, StringComparison.OrdinalIgnoreCase)) pair.Account = null;
            else pair.Account = acct;
            pair.CloudOnlyMinMB = Math.Max(0, d.TryGetProperty("cloudOnlyMinMB", out var mb) && mb.TryGetInt32(out var mbv) ? mbv : 0);
            pair.CloudOnlyFolders = (Str(d, "cloudOnlyFolders") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => s.Replace('\\', '/').Trim('/')).Where(s => s.Length > 0).ToList();
        }

        if (CheckLocal(pair, original) is { } problem) throw new InvalidOperationException(problem);

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;
        bool exists, remoteHasItems;
        var rootNames = new List<string>(); // what the remote folder holds at its top, to recognise a vault
        Func<Task> createRemote;
        if (pair.Kind == RemoteKind.Folder)
        {
            var full = pair.RemoteFullPath;
            if (File.Exists(full)) throw new InvalidOperationException($"{full} is a file, not a folder.");
            exists = Directory.Exists(full);
            if (exists) rootNames.AddRange(Directory.EnumerateFileSystemEntries(full).Select(Path.GetFileName)!);
            remoteHasItems = rootNames.Count > 0;
            createRemote = () => { Directory.CreateDirectory(full); return Task.CompletedTask; };
        }
        else if (pair.Kind == RemoteKind.Synology)
        {
            var nas = await NasFor(pair.Server!, pair.User!, ct);
            var info = await nas.GetInfoAsync(pair.RemotePath, ct);
            if (info is { IsDir: false }) throw new InvalidOperationException($"{pair.RemotePath} is a file on the NAS, not a folder.");
            exists = info != null;
            if (exists) rootNames.AddRange((await nas.ListAsync(pair.RemotePath, ct)).Where(e => !e.Name.StartsWith('#') && e.Name != "@eaDir").Select(e => e.Name));
            remoteHasItems = rootNames.Count > 0;
            createRemote = () => new SynologyStore(nas, pair).EnsureRootAsync(ct);
        }
        else
        {
            var drive = await DriveFor(pair.Account, ct);
            try
            {
                var folder = await drive.ResolveFolderAsync(pair.RemotePath, ct);
                exists = true;
                rootNames.AddRange((await drive.ListChildrenAsync(folder.Id, ct)).Select(f => f.Name));
                remoteHasItems = rootNames.Count > 0;
            }
            catch (DirectoryNotFoundException) { exists = false; remoteHasItems = false; }
            createRemote = () => drive.EnsureFolderAsync(pair.RemotePath, ct);
        }
        if (!exists && !create)
            throw new InvalidOperationException($"'{pair.RemotePath}' does not exist remotely. Tick 'Create it if missing' or pick an existing folder.");
        var remoteIsVault = rootNames.Contains(CryptomatorVault.VaultFile, StringComparer.OrdinalIgnoreCase);
        // An existing pair with plain files on the other side becomes encrypted by conversion (VaultConverter).
        var converting = turningOn && remoteHasItems && !remoteIsVault;
        if (converting && rootNames.Any(n => n.Equals(CryptomatorVault.DataFolder, StringComparison.OrdinalIgnoreCase) || n.Equals(CryptomatorVault.MasterkeyFile, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"The remote folder holds an item named '{CryptomatorVault.DataFolder}' or '{CryptomatorVault.MasterkeyFile}', which a Cryptomator vault needs for itself. Rename it first.");
        if (!encrypt && remoteIsVault && !turningOff)
            throw new InvalidOperationException("The remote folder is an encrypted Cryptomator vault: tick 'Encrypt on the other side' and enter its passphrase.");
        var decrypting = turningOff && remoteIsVault;
        if (decrypting && CryptomatorVault.LoadKey(original!.VaultId) == null)
            throw new InvalidOperationException("This computer does not have the vault's key, so it cannot decrypt it. Enter the passphrase first (Edit, with Encrypt ticked), then untick.");

        if (!confirmed)
        {
            var messages = new List<string>();
            var fresh = original == null || original.Id != pair.Id; // a new state file: the first sync starts without history
            var localHasItems = Directory.Exists(pair.LocalFullPath) && Directory.EnumerateFileSystemEntries(pair.LocalFullPath).Any();
            if (fresh && localHasItems && remoteHasItems)
                messages.Add("Both folders already contain files. On the first sync, files with identical content on both sides are simply " +
                             "linked up; files with the same name but different content are all kept (the local one is renamed " +
                             "'name (conflict <computer> <time>)' and uploaded next to the remote one). Nothing is deleted.");
            if (pair.Kind == RemoteKind.Drive && pair.RemotePath.Length == 0)
                messages.Add("This syncs your whole My Drive. Everything in it is downloaded to this computer unless cloud-only rules keep large files or folders online.");
            if (original is { OnDemand: true } && (!pair.OnDemand || original.Id != pair.Id))
                messages.Add("Files on demand ends for this folder: what is on this computer stays as ordinary files, and everything that is online-only now will be downloaded.");
            if (!pair.OnDemand && ManagedSyncRoot(pair.LocalFullPath) is { } root)
                messages.Add($"The local folder is inside {root}, which Windows itself syncs to another server (OneDrive or your organisation's Work Folders). " +
                             "Everything Unisono downloads there ends up in that place too.");
            if (decrypting)
                messages.Add("Unisono turns the vault back into plain files: every file is decrypted and uploaded as an ordinary file next to the vault " +
                             "(online-only files are downloaded for this and do not stay here), in turns and in the background. Then the vault's own " +
                             "files go to the trash on the other side. From then on Google Drive (or whoever runs the NAS) can read the files. " +
                             "Encrypted copies stay in the trash until it is emptied. Other computers that sync this folder stop syncing it until you " +
                             "untick Encrypt there too.");
            if (converting)
                messages.Add("The remote folder already holds unencrypted files. Unisono turns it into a Cryptomator vault: every file is encrypted " +
                             "and uploaded into the vault (online-only files are downloaded for this and do not stay here), in turns and in the " +
                             "background. Google documents become .docx / .xlsx / .pptx files. When everything is in the vault, the unencrypted " +
                             "originals are moved to the trash on the other side. Unencrypted copies can stay in the trash (Google Drive's trash, " +
                             "the NAS's recycle bin) until it is emptied: empty it there to be rid of them. Other computers that sync this " +
                             "folder stop syncing it until you enter the passphrase there too.");
            if (messages.Count > 0) return new { status = "confirm", messages };
        }

        if (!exists) await createRemote();
        if (encrypt && (original == null || original.Id != pair.Id || !string.IsNullOrEmpty(passphrase))) await SetUpVaultAsync(pair, passphrase, converting, recoveryKey, ct);
        if (decrypting)
        {
            await _host.StopAsync(); // restarted below; the decryption then runs in place of the pair's cycles
            VaultConverter.Request(pair, decrypt: true);
            _log.Info($"[{pair.Name}] turning the Cryptomator vault back into plain files");
        }
        else if (turningOff)
        {
            // No vault on the other side any more: sync the plain folder from a fresh start.
            await _host.StopAsync();
            try { File.Delete(SyncState.FilePathFor(original!)); } catch { }
        }
        if (converting)
        {
            await _host.StopAsync(); // restarted below; the conversion then runs in place of the pair's cycles
            VaultConverter.Request(pair);
            _log.Info($"[{pair.Name}] converting the remote folder into a Cryptomator vault");
        }
        else if (turningOn)
        {
            // Start afresh: the pair's history describes the unencrypted folder, not the vault.
            await _host.StopAsync(); // restarted below
            try { File.Delete(SyncState.FilePathFor(original!)); } catch { }
        }
        if (original is { OnDemand: true } && (!pair.OnDemand || original.Id != pair.Id))
        {
            await _host.StopAsync(); // restarted below; the state must not change under a running cycle
            CloudFiles.TurnOff(original, _log);
        }
        Directory.CreateDirectory(pair.LocalFullPath);
        if (ignoreText != null)
        {
            WriteIgnoreFile(pair.LocalFullPath, ignoreText);
            pair.IgnorePatterns = new(); // patterns kept in the config by earlier versions now live in the file
        }
        if (original != null)
        {
            if (original.Id != pair.Id) { try { File.Delete(SyncState.FilePathFor(original)); } catch { } }
            Config.Pairs[Config.Pairs.IndexOf(original)] = pair;
        }
        else Config.Pairs.Add(pair);
        Config.Save();
        _log.Info($"{(original == null ? "added" : "changed")} pair {pair.Name} <-> {pair.LocalFullPath}");
        await _restartHost();
        return new { status = "ok" };
    }

    private static List<string> Locked(List<string> list) { lock (list) return list.ToList(); }

    /// <summary>The user's answer to "this file changes very often": add it to the pair's .unisonoignore, or keep syncing it.</summary>
    private object? FrequentChoice(string? id, string rel, string? action)
    {
        var pair = FindPair(id);
        if (action == "ignore")
        {
            var pattern = IgnoreFile.AddItem(pair.LocalFullPath, rel, isDir: false);
            _log.Info($"[{pair.Name}] now ignored (changes very often): {pattern}");
        }
        else
        {
            if (!pair.FrequentOk.Contains(rel, StringComparer.OrdinalIgnoreCase)) pair.FrequentOk.Add(rel);
            Config.Save();
            _log.Info($"[{pair.Name}] kept syncing although it changes very often: {rel}");
        }
        if (_host.Pairs.FirstOrDefault(s => s.Pair.Id == pair.Id) is { } st)
            lock (st.Frequent) st.Frequent.RemoveAll(f => f.Equals(rel, StringComparison.OrdinalIgnoreCase));
        _pushState();
        return null;
    }

    // The pair dialog's Ignore field is the .unisonoignore file at the top of the local folder, so the patterns sync
    // with the folder and apply on every computer that syncs it.

    private string ReadIgnoreFile(string? localPath, SyncPair? pair)
    {
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(localPath))
        {
            var root = Path.GetFullPath(Environment.ExpandEnvironmentVariables(localPath.Trim()));
            try { if (IgnoreFile.Read(root) is { Length: > 0 } text) lines.AddRange(text.Split('\n')); }
            catch (Exception ex) { _log.Warn($"could not read {IgnoreFile.PathFor(root)}: {ex.Message}"); }
        }
        // Earlier versions kept the pair's patterns in the config; they are shown here and move into the file on save.
        if (pair != null) lines.AddRange(pair.IgnorePatterns.Where(x => !lines.Contains(x)));
        return string.Join("\n", lines);
    }

    private void WriteIgnoreFile(string localFullPath, string text)
    {
        if (IgnoreFile.Write(localFullPath, text) is { } done) _log.Info(done);
    }

    /// <summary>
    /// An encrypted pair's Cryptomator vault on the other side: joins an existing one (made by Unisono or by Cryptomator;
    /// the passphrase must fit) or, in an empty folder, creates one. Either way the master keys are stored for this Windows
    /// user and <see cref="SyncPair.VaultId"/> set.
    /// </summary>
    private async Task SetUpVaultAsync(SyncPair pair, string? passphrase, bool converting, string? recoveryKey, CancellationToken ct)
    {
        var plain = JsonSerializer.Deserialize<SyncPair>(JsonSerializer.Serialize(pair))!;
        plain.Encrypted = false;
        await using var stores = new StoreFactory(_log, interactive: false);
        var inner = await stores.CreateAsync(plain, ct);
        var tree = await inner.GetTreeAsync(_log, ct);
        var dir = Path.Combine(AppPaths.DataDir, "crypt-tmp");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, Guid.NewGuid().ToString("N"));
        try
        {
            async Task<string> ReadAsync(RemoteNode n)
            {
                await inner.DownloadAsync(n, file, ct);
                return await File.ReadAllTextAsync(file, ct);
            }
            if (!string.IsNullOrWhiteSpace(recoveryKey))
            {
                // With the recovery key: the vault's keys under a new passphrase (a new masterkey.cryptomator), and a new
                // vault.cryptomator if that is missing too.
                var keys = CryptomatorVault.FromRecoveryKey(recoveryKey) ?? throw new InvalidOperationException("That recovery key is mistyped (its checksum does not fit).");
                if (string.IsNullOrEmpty(passphrase)) throw new InvalidOperationException("Choose a new passphrase for the vault.");
                var vault = new CryptomatorVault(keys);
                string config, id;
                if (tree.TryGetValue(CryptomatorVault.VaultFile, out var existingConfig))
                {
                    config = await ReadAsync(existingConfig);
                    if (!vault.Owns(config)) throw new InvalidOperationException("That recovery key does not belong to the vault in this folder.");
                    id = CryptomatorVault.IdOf(config);
                }
                else
                {
                    if (tree.Keys.Any(k => !VaultConverter.IsVaultItem(k))) throw new InvalidOperationException("This folder holds files that are not a vault; the recovery key is for a vault folder.");
                    (config, id) = CryptomatorVault.NewConfig(keys);
                    await File.WriteAllTextAsync(file, config, ct);
                    await inner.UploadNewAsync(file, CryptomatorVault.VaultFile, null, ct);
                }
                var master = await Task.Run(() => CryptomatorVault.Masterkey(keys, passphrase), ct);
                await File.WriteAllTextAsync(file, master, ct);
                if (tree.TryGetValue(CryptomatorVault.MasterkeyFile, out var oldMaster)) await inner.UploadUpdateAsync(file, oldMaster, ct);
                else await inner.UploadNewAsync(file, CryptomatorVault.MasterkeyFile, null, ct);
                CryptomatorVault.SaveKey(id, keys);
                CryptomatorVault.SaveVaultFiles(id, master, config);
                pair.VaultId = id;
                _log.Info($"[{pair.Name}] vault {id} opened with the recovery key; it has a new passphrase now");
                return;
            }
            if (tree.TryGetValue(CryptomatorVault.VaultFile, out var configNode))
            {
                var config = await ReadAsync(configNode);
                var id = CryptomatorVault.IdOf(config);
                byte[] keys;
                if (string.IsNullOrEmpty(passphrase))
                    keys = CryptomatorVault.LoadKey(id) ?? throw new InvalidOperationException("This remote folder is an encrypted vault: enter its passphrase.");
                else
                {
                    if (!tree.TryGetValue(CryptomatorVault.MasterkeyFile, out var masterNode))
                        throw new InvalidOperationException($"{CryptomatorVault.MasterkeyFile} is missing in this vault.");
                    var master = await ReadAsync(masterNode);
                    keys = await Task.Run(() => CryptomatorVault.Unlock(master, passphrase), ct)
                           ?? throw new InvalidOperationException("That is not the passphrase of this vault.");
                    CryptomatorVault.SaveVaultFiles(id, master, config); // this computer's copies, to put back what goes missing
                }
                CryptomatorVault.CheckConfig(config, keys);
                CryptomatorVault.SaveKey(id, keys);
                pair.VaultId = id;
                _log.Info($"[{pair.Name}] joined the Cryptomator vault {id}");
                return;
            }
            if (tree.Count > 0 && !converting) throw new InvalidOperationException("The remote folder already holds files that are not a Cryptomator vault, so it cannot become one. " +
                "For an encrypted pair choose an empty or new remote folder (or remove this pair and add it again with one); nothing is deleted by removing a pair.");
            if (string.IsNullOrEmpty(passphrase)) throw new InvalidOperationException("Choose a passphrase for the vault.");
            var (newKeys, masterkey, vaultConfig, newId) = await Task.Run(() => CryptomatorVault.Create(passphrase), ct);
            await File.WriteAllTextAsync(file, masterkey, ct);
            await inner.UploadNewAsync(file, CryptomatorVault.MasterkeyFile, null, ct);
            await File.WriteAllTextAsync(file, vaultConfig, ct);
            await inner.UploadNewAsync(file, CryptomatorVault.VaultFile, null, ct);
            // The root directory's folder, so the vault opens in Cryptomator before anything is synced.
            RemoteNode? parent = null;
            var rootPath = new CryptomatorVault(newKeys).DirPath("");
            var built = "";
            foreach (var part in rootPath.Split('/'))
            {
                built = PathUtil.Join(built, part);
                parent = await inner.CreateFolderAsync(built, parent, ct);
            }
            CryptomatorVault.SaveKey(newId, newKeys);
            CryptomatorVault.SaveVaultFiles(newId, masterkey, vaultConfig);
            pair.VaultId = newId;
            _log.Info($"[{pair.Name}] created the Cryptomator vault {newId}");
        }
        finally
        {
            try { File.Delete(file); } catch { }
        }
    }

    /// <summary>"Put back vault files": this computer's copies of vault.cryptomator and masterkey.cryptomator go to the other side.</summary>
    private async Task<object> RestoreVaultFilesAsync(SyncPair pair)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var ct = cts.Token;
        var keys = CryptomatorVault.LoadKey(pair.VaultId) ?? throw new InvalidOperationException("This computer does not have the vault's key.");
        var vault = new CryptomatorVault(keys);
        var (master, config) = CryptomatorVault.LoadVaultFiles(pair.VaultId!);
        if (master == null) throw new InvalidOperationException($"This computer has no copy of {CryptomatorVault.MasterkeyFile}. Use 'Recover with recovery key' instead (it makes a new one, with a new passphrase).");
        if (config == null || !vault.Owns(config)) config = CryptomatorVault.NewConfig(keys).Config;
        await using var stores = new StoreFactory(_log, interactive: false);
        var inner = await stores.CreatePlainAsync(pair, ct);
        var tree = await inner.GetTreeAsync(_log, ct);
        var file = Path.Combine(AppPaths.DataDir, "crypt-tmp", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        try
        {
            foreach (var (name, text) in new[] { (CryptomatorVault.MasterkeyFile, master), (CryptomatorVault.VaultFile, config) })
            {
                await File.WriteAllTextAsync(file, text, new System.Text.UTF8Encoding(false), ct);
                if (tree.TryGetValue(name, out var node)) await inner.UploadUpdateAsync(file, node, ct);
                else await inner.UploadNewAsync(file, name, null, ct);
            }
        }
        finally { try { File.Delete(file); } catch { } }
        _log.Info($"[{pair.Name}] put the vault files back on the other side from this computer's copies");
        ActivityJournal.Add(pair, "restored", CryptomatorVault.MasterkeyFile + ", " + CryptomatorVault.VaultFile, -1, "put back from this computer's copies");
        return new { ok = true };
    }

    private string? CheckLocal(SyncPair p, SyncPair? original)
    {
        if (p.LocalPath.Length == 0) return "Choose a local folder.";
        if (Path.GetPathRoot(p.LocalFullPath)?.TrimEnd('\\').Length == p.LocalFullPath.TrimEnd('\\').Length) return "A whole drive cannot be a sync folder.";
        var mine = WithSep(p.LocalFullPath);
        if (mine.StartsWith(WithSep(AppPaths.DataDir), StringComparison.OrdinalIgnoreCase) || mine.StartsWith(WithSep(AppPaths.AuthDir), StringComparison.OrdinalIgnoreCase))
            return "The local folder cannot be inside Unisono's own data folder.";
        foreach (var other in Config.Pairs.Where(o => !ReferenceEquals(o, original)))
        {
            // Nesting is fine (the outer pair leaves the inner folder alone); the very same folder is not.
            if (WithSep(other.LocalFullPath).Equals(mine, StringComparison.OrdinalIgnoreCase))
                return $"{other.LocalFullPath} is already the local folder of the pair with {other.Name}. A local folder can belong to one pair only. " +
                    "To keep several folders alike, add one pair per folder, each with the same remote or other folder.";
        }
        if (p.OnDemand)
        {
            if (CloudFiles.WhyNot(p.LocalFullPath, original is { OnDemand: true } && original.Id == p.Id ? original : null) is { } why) return why;
            // Windows cannot nest two files-on-demand folders (sync roots). An ordinary pair inside one is fine: this
            // pair leaves that folder alone, and its files stay ordinary files.
            foreach (var other in Config.Pairs.Where(o => !ReferenceEquals(o, original) && o.OnDemand))
            {
                var theirs = WithSep(other.LocalFullPath);
                if (theirs.StartsWith(mine, StringComparison.OrdinalIgnoreCase) || mine.StartsWith(theirs, StringComparison.OrdinalIgnoreCase))
                    return $"Two files-on-demand folders cannot lie inside one another ({other.LocalFullPath} is one too); Windows does not allow that. " +
                           "Use 'Everything downloaded' for one of them.";
            }
        }
        if (p.Kind == RemoteKind.Folder)
        {
            if (p.RemotePath.Length == 0) return "Choose the other folder.";
            var other = WithSep(p.RemoteFullPath);
            if (other.Equals(mine, StringComparison.OrdinalIgnoreCase)) return "The two folders are the same folder.";
            if (other.StartsWith(mine, StringComparison.OrdinalIgnoreCase) || mine.StartsWith(other, StringComparison.OrdinalIgnoreCase))
                return "One folder lies inside the other; they would keep copying into themselves.";
            if (other.StartsWith(WithSep(AppPaths.DataDir), StringComparison.OrdinalIgnoreCase) || other.StartsWith(WithSep(AppPaths.AuthDir), StringComparison.OrdinalIgnoreCase))
                return "The other folder cannot be inside Unisono's own data folder.";
        }
        if (p.Kind == RemoteKind.Synology)
        {
            if (string.IsNullOrEmpty(p.Server) || string.IsNullOrEmpty(p.User)) return "Choose a NAS login.";
            if (p.RemotePath.Trim('/').Length == 0) return "Pick a shared folder or a folder inside one; the list of shared folders itself cannot be synced.";
        }
        return null;
    }

    private static string WithSep(string path) => path.TrimEnd('\\', '/') + "\\";

    private static string? ManagedSyncRoot(string path)
    {
        foreach (var v in new[] { "OneDrive", "OneDriveCommercial", "OneDriveConsumer" })
            if (Environment.GetEnvironmentVariable(v) is { Length: > 0 } root && WithSep(path).StartsWith(WithSep(root), StringComparison.OrdinalIgnoreCase))
                return root;
        var workFolders = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Work Folders");
        return WithSep(path).StartsWith(WithSep(workFolders), StringComparison.OrdinalIgnoreCase) ? workFolders : null;
    }

    /// <summary>Log lines name a pair and a relative path; clicking one selects that item in Explorer.</summary>
    private void RevealItem(string? pairName, string? rel)
    {
        var pair = Config.Pairs.FirstOrDefault(x => x.Name == pairName);
        if (pair == null || string.IsNullOrEmpty(rel)) return;
        var full = Path.Combine(pair.LocalFullPath, rel.Replace('/', '\\'));
        if (File.Exists(full) || Directory.Exists(full)) Process.Start("explorer.exe", $"/select,\"{full}\"");
        else
        {
            // Gone (deleted, renamed to a conflict copy): open the nearest folder that still exists.
            var dir = Path.GetDirectoryName(full);
            while (dir != null && !Directory.Exists(dir)) dir = Path.GetDirectoryName(dir);
            if (dir != null) Ui.OpenFolder(dir);
        }
    }

    // -------------------------------------------------------------- autostart

    private static bool IsAutostart()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(RunValue) is string s && s.Contains(Environment.ProcessPath ?? "\u0000", StringComparison.OrdinalIgnoreCase);
    }

    private void SetAutostart(bool on)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) key.SetValue(RunValue, $"\"{Environment.ProcessPath}\" --tray");
        else key.DeleteValue(RunValue, throwOnMissingValue: false);
        _log.Info(on ? "will start with Windows (in the tray)" : "will not start with Windows");
    }

    /// <summary>A copy is named after the file's own modified time ("2026-09-24 064301.txt"); that is the version's time.</summary>
    private static string VersionLabel(VersionStore.Version v) =>
        DateTime.TryParseExact(Path.GetFileNameWithoutExtension(v.CopyPath), "yyyy-MM-dd HHmmss", null, System.Globalization.DateTimeStyles.None, out var t)
            ? t.ToString("yyyy-MM-dd HH:mm:ss") : v.Taken.ToString("yyyy-MM-dd HH:mm");

    private static List<string> Lines(string? text) =>
        (text ?? "").Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length > 0).ToList();

    private static List<string> StrList(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Select(x => (x.GetString() ?? "").Replace('\\', '/').Trim('/')).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            : new List<string>();

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public async ValueTask DisposeAsync()
    {
        foreach (var c in _nas.Values)
        {
            try { await c.LogoutAsync(CancellationToken.None); } catch { }
            c.Dispose();
        }
        _nas.Clear();
    }
}
