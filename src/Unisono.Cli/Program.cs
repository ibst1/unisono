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
var dryRun = args.Contains("--dry-run");
args = args.Where(a => a is not ("-v" or "--dry-run")).ToArray();
var stores = new StoreFactory(log, interactive: true);

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
                Console.WriteLine($"  [{p.Kind}] {p.Name}  <->  {p.LocalPath}{(p.ActiveHere ? "" : " (not on this computer)")}   {(p.Server != null ? $"server={p.Server} user={p.User} " : "")}docs={p.GoogleDocs} cloudOnly>={p.CloudOnlyMinMB}MB folders=[{string.Join(", ", p.CloudOnlyFolders)}] poll={p.PollSeconds}s");
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
                var who = args.Length > 3 ? args[3] : SynologyCredentials.Load(server, null)?.User ?? "";
                SynologyCredentials.Delete(server, who);
                Console.WriteLine($"Forgot credentials for {server}");
                return 0;
            }
            if (args[1].Equals("api", StringComparison.OrdinalIgnoreCase))
            {
                // Diagnostic: UnisonoCli nas api <server> <cgi> key=value ... [--out file]   (uses the stored login)
                var stored = SynologyCredentials.Load(server, null) ?? throw new InvalidOperationException($"no stored login for {server}");
                using var diag = await SynologyClient.ConnectAsync(stored, stored.AllowInvalidCertificate, null, ct, m => log.Debug(m));
                var outIdx = Array.IndexOf(args, "--out");
                var outFile = outIdx > 0 && outIdx + 1 < args.Length ? args[outIdx + 1] : null;
                var form = args.Skip(4).Where((a, i) => a.Contains('=') && (outIdx < 0 || i + 4 < outIdx))
                    .ToDictionary(a => a[..a.IndexOf('=')], a => a[(a.IndexOf('=') + 1)..]);
                var (type, body) = await diag.RawAsync(args[3], form, ct);
                Console.WriteLine($"content-type: {type}, {body.Length} bytes");
                if (outFile != null) { await System.IO.File.WriteAllBytesAsync(outFile, body, ct); Console.WriteLine($"saved to {outFile}"); }
                else if (type.Contains("json") || (body.Length > 0 && body[0] == (byte)'{')) Console.WriteLine(System.Text.Encoding.UTF8.GetString(body));
                await diag.LogoutAsync(ct);
                return 0;
            }
            if (!args[1].Equals("login", StringComparison.OrdinalIgnoreCase) || args.Length < 4) { Usage(); return 2; }
            var allowInvalid = args.Contains("--insecure");
            var creds = new SynologyCredentials { Server = server, User = args[3], AllowInvalidCertificate = allowInvalid };
            Console.Write($"DSM password for {creds.User}@{server}: ");
            creds.Password = ReadSecret();
            if (creds.Password.Length == 0) { Console.Error.WriteLine("error: no password given"); return 1; }
            SynologyClient nasClient;
            while (true)
            {
                try
                {
                    nasClient = await SynologyClient.ConnectAsync(creds, allowInvalid, _ => Task.FromResult(PromptOtp()), ct, m => log.Info(m));
                    break;
                }
                catch (UntrustedCertificateException ex)
                {
                    var certs = ex.Distinct.Where(c => !creds.PinnedCertificates.Contains(c.Sha256, StringComparer.OrdinalIgnoreCase)).ToList();
                    if (certs.Count == 0) throw;
                    Console.WriteLine("The NAS answered with certificate(s) Windows does not trust:");
                    foreach (var cert in certs) Console.WriteLine(cert.Describe());
                    Console.Write("Trust exactly these for this NAS? [y/N] ");
                    if (!string.Equals(Console.ReadLine()?.Trim(), "y", StringComparison.OrdinalIgnoreCase)) return 1;
                    creds.PinnedCertificates.AddRange(certs.Select(c => c.Sha256));
                }
            }
            using var nas = nasClient;
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
            if (config.Pairs.Any(p => p.LocalFullPath.Equals(localPath, StringComparison.OrdinalIgnoreCase)))
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
                var drive = await stores.DriveAsync(null, ct);
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
            log.Info($"UnisonoCli {version} starting, {config.ActivePairs.Count} pair(s) on this computer");
            if (args[0] == "run")
            {
                await using var host = new SyncHost(log, interactive: true);
                await host.StartAsync(config);
                try { await Task.Delay(Timeout.Infinite, ct); }
                catch (OperationCanceledException) { }
                await host.StopAsync();
                log.Info("stopped");
                return 0;
            }

            // A dry run writes nothing, so it may run next to the tray app.
            using var syncLock = dryRun ? null : SyncLock.TryAcquire();
            if (syncLock == null && !dryRun)
            {
                Console.Error.WriteLine("error: another Unisono is already syncing (the tray app or 'UnisonoCli run'); close it first");
                return 1;
            }
            try
            {
                var failed = 0;
                foreach (var p in config.ActivePairs)
                {
                                        try
                    {
                        var engine = new SyncEngine(await stores.CreateAsync(p, ct), p, log, config.Pairs, config.Options) { DryRun = dryRun };
                        var s = await engine.RunCycleAsync(ct);
                        failed += s.Errors;
                        if (dryRun) Console.WriteLine($"[{p.Name}] dry run: {engine.Plan.Count} action(s) planned, nothing changed");
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { log.Error($"[{p.Name}] cycle failed: {ex.Message}"); failed++; }
                }
                return failed == 0 ? 0 : 1;
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

static string NormalizeServer(string s) => SynologyClient.NormalizeServer(s);

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
    Console.WriteLine("  UnisonoCli auth");
    Console.WriteLine("  UnisonoCli nas login <server> <user> [--insecure]   |   UnisonoCli nas logout <server> [user]");
    Console.WriteLine("  UnisonoCli init <remote/path> <localDir> [--create] [--nas <server> <user>] [--insecure]");
    Console.WriteLine("  UnisonoCli sync [-v] [--dry-run]   |   UnisonoCli run [-v]   |   UnisonoCli config   |   UnisonoCli version");
    Console.WriteLine("  UnisonoCli ls [drive/path]   |   UnisonoCli pull <drive/path> <localDir>   |   UnisonoCli changes <token>");
}

sealed class PullStats
{
    public int Downloaded, Skipped, GoogleNative, Failed;
}
