using System.Reflection;
using Unisono;
using File = Google.Apis.Drive.v3.Data.File;

// Commands:
//   unisono auth                                    Google: sign in (opens the browser once) and print the account
//   unisono nas login <server> <user>               Synology: store DSM credentials (DPAPI) after a test login
//   unisono nas logout <server>                     Synology: forget stored credentials
//   unisono init <remote/path> <localDir> [--create] [--nas <server> <user>]
//   unisono sync [-v]                               run one sync cycle for every pair
//   unisono run [-v]                                keep syncing: watch the local folders, poll the remote side
//   unisono ls [drive/path] | pull <drive/path> <localDir> | changes <token>   Drive helpers
//   unisono config | version

var asm = Assembly.GetExecutingAssembly();
var version = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";
var buildTime = asm.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "BuildTime")?.Value;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
var ct = cts.Token;
var log = new Log { Verbose = args.Contains("-v") };
args = args.Where(a => a != "-v").ToArray();
var stores = new StoreFactory(log);

try
{
    AppPaths.EnsureDataDir();
    switch (args.Length > 0 ? args[0].ToLowerInvariant() : "help")
    {
        case "version":
            Console.WriteLine($"Unisono {version}{(buildTime != null ? $" ({buildTime})" : "")}");
            Console.WriteLine($"data: {AppPaths.DataDir}");
            return 0;

        case "config":
        {
            var config = Config.Load();
            Console.WriteLine(Config.FilePath);
            foreach (var p in config.Pairs)
                Console.WriteLine($"  [{p.Kind}] {p.Name}  <->  {p.LocalPath}   {(p.Server != null ? $"server={p.Server} user={p.User} " : "")}docs={p.GoogleDocs} cloudOnly>={p.CloudOnlyMinMB}MB folders=[{string.Join(", ", p.CloudOnlyFolders)}] poll={p.PollSeconds}s");
            if (config.Pairs.Count == 0) Console.WriteLine("  (no pairs; use: unisono init <remote/path> <localDir>)");
            return 0;
        }

        case "auth":
        {
            var drive = await DriveClient.ConnectAsync(ct);
            var (email, usage, limit, token) = await drive.WhoAmIAsync(ct);
            Console.WriteLine($"Signed in as {email}");
            Console.WriteLine($"Storage: {Gb(usage)} of {(limit > 0 ? Gb(limit) : "unlimited")} used");
            Console.WriteLine($"Changes start token: {token}");
            Console.WriteLine($"Tokens stored in {AppPaths.TokenDir}");
            return 0;
        }

        case "nas":
        {
            if (args.Length < 3) { Usage(); return 2; }
            var server = NormalizeServer(args[2]);
            if (args[1].Equals("logout", StringComparison.OrdinalIgnoreCase))
            {
                SynologyCredentials.Delete(server);
                Console.WriteLine($"Forgot credentials for {server}");
                return 0;
            }
            if (!args[1].Equals("login", StringComparison.OrdinalIgnoreCase) || args.Length < 4) { Usage(); return 2; }
            var allowInvalid = args.Contains("--insecure");
            var creds = new SynologyCredentials { Server = server, User = args[3] };
            Console.Write($"DSM password for {creds.User}@{server}: ");
            creds.Password = ReadSecret();
            if (creds.Password.Length == 0) { Console.Error.WriteLine("error: no password given"); return 1; }
            using var nas = await SynologyClient.ConnectAsync(creds, allowInvalid, PromptOtp, ct);
            creds.Save();
            Console.WriteLine($"Signed in to {nas.BaseUrl} as {creds.User}{(creds.DeviceId != null ? " (device token stored; no more 2FA prompts)" : "")}");
            var shares = await nas.ListAsync("/", ct);
            Console.WriteLine("Shared folders: " + string.Join(", ", shares.Where(s => s.IsDir).Select(s => s.Path)));
            await nas.LogoutAsync(ct);
            return 0;
        }

        case "ls":
        {
            var drive = await DriveClient.ConnectAsync(ct, quiet: true);
            var folder = await drive.ResolveFolderAsync(args.Length > 1 ? args[1] : "", ct);
            var children = await drive.ListChildrenAsync(folder.Id, ct);
            foreach (var f in children)
            {
                var kind = DriveClient.IsFolder(f) ? "<dir>" : DriveClient.IsGoogleNative(f) ? "<gdoc>" : (f.Size?.ToString() ?? "?");
                Console.WriteLine($"{f.ModifiedTimeDateTimeOffset:yyyy-MM-dd HH:mm}  {kind,10}  {f.Name}");
            }
            Console.WriteLine($"{children.Count} item(s)");
            return 0;
        }

        case "changes":
        {
            // Diagnostic: raw Drive change feed since a token (see 'auth' for the current start token).
            if (args.Length < 2) { Usage(); return 2; }
            var drive = await DriveClient.ConnectAsync(ct, quiet: true);
            var (changes, newToken) = await drive.ListChangesAsync(args[1], ct);
            foreach (var c in changes)
                Console.WriteLine($"{(c.Removed == true ? "removed" : c.File?.Trashed == true ? "trashed" : "changed"),-8} {c.FileId}  {c.File?.Name}  parents=[{string.Join(",", c.File?.Parents ?? new List<string>())}]  {c.File?.ModifiedTimeDateTimeOffset:HH:mm:ss}");
            Console.WriteLine($"{changes.Count} change(s); new start token {newToken}");
            return 0;
        }

        case "pull":
        {
            if (args.Length < 3) { Usage(); return 2; }
            var drive = await DriveClient.ConnectAsync(ct, quiet: true);
            var folder = await drive.ResolveFolderAsync(args[1], ct);
            var stats = new PullStats();
            await PullAsync(drive, folder, Path.GetFullPath(args[2]), stats, ct);
            Console.WriteLine($"Done: {stats.Downloaded} downloaded, {stats.Skipped} up to date, {stats.GoogleNative} Google-native skipped, {stats.Failed} failed");
            return stats.Failed == 0 ? 0 : 1;
        }

        case "init":
        {
            var create = args.Contains("--create");
            var insecure = args.Contains("--insecure");
            var nasIdx = Array.IndexOf(args, "--nas");
            string? server = null, user = null;
            if (nasIdx >= 0)
            {
                if (args.Length < nasIdx + 3) { Usage(); return 2; }
                server = NormalizeServer(args[nasIdx + 1]);
                user = args[nasIdx + 2];
                args = args.Where((_, i) => i < nasIdx || i > nasIdx + 2).ToArray();
            }
            args = args.Where(a => a is not ("--create" or "--insecure")).ToArray();
            if (args.Length < 3) { Usage(); return 2; }
            var localPath = Path.GetFullPath(args[2]);
            var pair = new SyncPair
            {
                Kind = server != null ? RemoteKind.Synology : RemoteKind.Drive,
                RemotePath = server != null ? "/" + args[1].Replace('\\', '/').Trim('/') : args[1].Trim('/', '\\'),
                LocalPath = localPath,
                Server = server,
                User = user,
                AllowInvalidCertificate = insecure,
            };
            if (pair.Kind == RemoteKind.Synology) pair.PollSeconds = 60;

            var config = Config.Load();
            if (config.Pairs.Any(p => p.LocalPath.Equals(localPath, StringComparison.OrdinalIgnoreCase)))
            {
                Console.Error.WriteLine($"error: {localPath} is already part of a pair (see: unisono config)");
                return 1;
            }
            // Verify the remote folder (or create it) before touching the config.
            if (pair.Kind == RemoteKind.Synology)
            {
                var store = (SynologyStore)await stores.CreateAsync(pair, ct);
                if (create) await store.EnsureRootAsync(ct);
                await store.GetTreeAsync(log, ct);
            }
            else
            {
                var drive = await stores.DriveAsync(ct);
                if (create) await drive.EnsureFolderAsync(pair.RemotePath, ct);
                else await drive.ResolveFolderAsync(pair.RemotePath, ct);
            }
            Directory.CreateDirectory(localPath);
            config.Pairs.Add(pair);
            config.Save();
            Console.WriteLine($"Added pair: {pair.Name}  <->  {localPath}");
            Console.WriteLine($"Settings (Google Docs export, cloud-only rules, poll interval) live in {Config.FilePath}");
            Console.WriteLine("Run 'unisono sync' for one cycle or 'unisono run' to keep syncing.");
            return 0;
        }

        case "sync":
        case "run":
        {
            var config = Config.Load();
            if (config.Pairs.Count == 0)
            {
                Console.Error.WriteLine("error: no sync pairs configured; use: unisono init <remote/path> <localDir>");
                return 1;
            }
            var engines = new List<SyncEngine>();
            foreach (var p in config.Pairs)
                engines.Add(new SyncEngine(await stores.CreateAsync(p, ct), p, log));
            log.Info($"Unisono {version} starting, {engines.Count} pair(s)");
            try
            {
                if (args[0] == "sync")
                {
                    var failed = 0;
                    foreach (var e in engines)
                    {
                        try { var s = await e.RunCycleAsync(ct); failed += s.Errors; }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex) { log.Error($"[{e.Pair.Name}] cycle failed: {ex.Message}"); failed++; }
                    }
                    return failed == 0 ? 0 : 1;
                }
                await RunForeverAsync(engines, log, ct);
                return 0;
            }
            finally
            {
                await stores.DisposeAsync();
            }
        }

        default:
            Usage();
            return 2;
    }
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("cancelled");
    return 130;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 1;
}

static async Task RunForeverAsync(List<SyncEngine> engines, Log log, CancellationToken ct)
{
    // The watcher only nudges the loop; every cycle rescans, so a missed event costs nothing but latency.
    var nudge = new SemaphoreSlim(0, 1);
    void Nudge() { if (nudge.CurrentCount == 0) { try { nudge.Release(); } catch (SemaphoreFullException) { } } }

    var watchers = new List<FileSystemWatcher>();
    foreach (var e in engines)
    {
        if (!Directory.Exists(e.Pair.LocalPath)) continue;
        var w = new FileSystemWatcher(e.Pair.LocalPath)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            InternalBufferSize = 64 * 1024,
        };
        FileSystemEventHandler onChange = (_, ev) => { if (!LocalScanner.IsIgnored(Path.GetFileName(ev.FullPath))) Nudge(); };
        w.Created += onChange; w.Changed += onChange; w.Deleted += onChange;
        w.Renamed += (_, ev) => Nudge();
        w.Error += (_, ev) => { log.Warn($"watcher: {ev.GetException().Message}"); Nudge(); };
        w.EnableRaisingEvents = true;
        watchers.Add(w);
    }
    var poll = TimeSpan.FromSeconds(Math.Max(5, engines.Min(e => e.Pair.PollSeconds)));
    log.Info($"watching {watchers.Count} folder(s), polling every {poll.TotalSeconds:0}s. Ctrl+C to stop.");

    var backoff = TimeSpan.Zero;
    while (!ct.IsCancellationRequested)
    {
        var ok = true;
        foreach (var e in engines)
        {
            try { await e.RunCycleAsync(ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { ok = false; log.Error($"[{e.Pair.Name}] cycle failed: {ex.Message}"); }
        }
        // Network trouble: back off up to 5 minutes rather than hammering the API.
        backoff = ok ? TimeSpan.Zero : TimeSpan.FromSeconds(Math.Min(300, Math.Max(15, backoff.TotalSeconds * 2)));
        var wait = ok ? poll : backoff;

        try
        {
            if (await nudge.WaitAsync(wait, ct))
            {
                // Let a burst of writes (save, rename, temp file dance) settle before scanning.
                await Task.Delay(2000, ct);
                while (nudge.CurrentCount > 0) await nudge.WaitAsync(0, ct);
            }
        }
        catch (OperationCanceledException) { break; }
    }
    foreach (var w in watchers) w.Dispose();
    log.Info("stopped");
}

static async Task PullAsync(DriveClient drive, File folder, string localDir, PullStats stats, CancellationToken ct)
{
    Directory.CreateDirectory(localDir);
    foreach (var f in await drive.ListChildrenAsync(folder.Id, ct))
    {
        ct.ThrowIfCancellationRequested();
        var localPath = Path.Combine(localDir, SafeName(f.Name));
        if (DriveClient.IsFolder(f))
        {
            await PullAsync(drive, f, localPath, stats, ct);
        }
        else if (DriveClient.IsGoogleNative(f))
        {
            Console.WriteLine($"  skip (Google-native) {Rel(localPath)}");
            stats.GoogleNative++;
        }
        else if (DriveClient.LocalMatches(f, localPath))
        {
            stats.Skipped++;
        }
        else
        {
            try
            {
                Console.WriteLine($"  get  {Rel(localPath)}  ({f.Size ?? 0:N0} bytes)");
                await drive.DownloadAsync(f, localPath, ct);
                stats.Downloaded++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.Error.WriteLine($"  FAIL {Rel(localPath)}: {ex.Message}");
                stats.Failed++;
            }
        }
    }

    static string Rel(string p) => Path.GetRelativePath(Environment.CurrentDirectory, p);
}

// Drive allows characters in names that NTFS does not; substitute rather than fail.
static string SafeName(string name)
{
    var invalid = Path.GetInvalidFileNameChars();
    var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
    return new string(chars).TrimEnd(' ', '.');
}

static string NormalizeServer(string s)
{
    s = s.Trim().TrimEnd('/');
    return s.Contains("://") ? s : "https://" + s;
}

static string? PromptOtp()
{
    Console.Write("Two-factor code: ");
    return Console.ReadLine();
}

/// <summary>Reads a line without echoing it.</summary>
static string ReadSecret()
{
    if (Console.IsInputRedirected) return Console.ReadLine() ?? "";
    var sb = new System.Text.StringBuilder();
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); return sb.ToString(); }
        if (key.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; continue; }
        if (!char.IsControl(key.KeyChar)) sb.Append(key.KeyChar);
    }
}

static string Gb(long bytes) => $"{bytes / 1024.0 / 1024 / 1024:F1} GB";

static void Usage()
{
    Console.WriteLine("usage:");
    Console.WriteLine("  unisono auth");
    Console.WriteLine("  unisono nas login <server> <user> [--insecure]   |   unisono nas logout <server>");
    Console.WriteLine("  unisono init <remote/path> <localDir> [--create] [--nas <server> <user>] [--insecure]");
    Console.WriteLine("  unisono sync [-v]   |   unisono run [-v]   |   unisono config   |   unisono version");
    Console.WriteLine("  unisono ls [drive/path]   |   unisono pull <drive/path> <localDir>   |   unisono changes <token>");
}

sealed class PullStats
{
    public int Downloaded, Skipped, GoogleNative, Failed;
}

/// <summary>One Drive sign-in and one NAS session per server, shared by all pairs that use them.</summary>
sealed class StoreFactory : IAsyncDisposable
{
    private readonly Log _log;
    private DriveClient? _drive;
    private readonly Dictionary<string, SynologyClient> _nas = new(StringComparer.OrdinalIgnoreCase);

    public StoreFactory(Log log) => _log = log;

    public async Task<DriveClient> DriveAsync(CancellationToken ct) =>
        _drive ??= await DriveClient.ConnectAsync(ct, quiet: true);

    public async Task<IRemoteStore> CreateAsync(SyncPair pair, CancellationToken ct)
    {
        if (pair.Kind == RemoteKind.Drive)
            return new DriveStore(await DriveAsync(ct), pair);

        if (string.IsNullOrEmpty(pair.Server) || string.IsNullOrEmpty(pair.User))
            throw new InvalidOperationException($"pair {pair.Name}: Server and User are required for a Synology pair");
        if (!_nas.TryGetValue(pair.Server, out var client))
        {
            var creds = SynologyCredentials.Load(pair.Server)
                        ?? throw new InvalidOperationException($"no stored credentials for {pair.Server}; run: unisono nas login {pair.Server} {pair.User}");
            if (!creds.User.Equals(pair.User, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"stored credentials for {pair.Server} are for '{creds.User}', pair wants '{pair.User}'; run 'unisono nas login' again");
            client = await SynologyClient.ConnectAsync(creds, pair.AllowInvalidCertificate, otpPrompt: null, ct);
            if (creds.DeviceId != null) creds.Save(); // resolved base / device token may have been refreshed
            _log.Debug($"NAS session on {client.BaseUrl} as {creds.User}");
            _nas[pair.Server] = client;
        }
        return new SynologyStore(client, pair);
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
