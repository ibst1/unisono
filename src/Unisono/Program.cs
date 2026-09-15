using System.Reflection;
using Unisono;
using File = Google.Apis.Drive.v3.Data.File;

// Commands:
//   unisono auth                          sign in (opens the browser once) and print the account
//   unisono ls [drive/path]               list a Drive folder
//   unisono pull <drive/path> <localDir>  one-way mirror Drive -> local (adds and updates only, never deletes)
//   unisono init <drive/path> <localDir>  add a sync pair to config.json
//   unisono sync [-v]                     run one sync cycle for every pair
//   unisono run [-v]                      keep syncing: watch the local folders, poll Drive for changes
//   unisono config | version

var asm = Assembly.GetExecutingAssembly();
var version = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";
var buildTime = asm.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "BuildTime")?.Value;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
var ct = cts.Token;
var log = new Log { Verbose = args.Contains("-v") };
args = args.Where(a => a != "-v").ToArray();

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
                Console.WriteLine($"  {p.Name}  <->  {p.LocalPath}   docs={p.GoogleDocs} cloudOnly>={p.CloudOnlyMinMB}MB folders=[{string.Join(", ", p.CloudOnlyFolders)}] poll={p.PollSeconds}s");
            if (config.Pairs.Count == 0) Console.WriteLine("  (no pairs; use: unisono init <drive/path> <localDir>)");
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
            if (args.Length < 3) { Usage(); return 2; }
            var create = args.Contains("--create");
            args = args.Where(a => a != "--create").ToArray();
            var drivePath = args[1].Trim('/', '\\');
            var localPath = Path.GetFullPath(args[2]);
            var drive = await DriveClient.ConnectAsync(ct, quiet: true);
            if (create) await drive.EnsureFolderAsync(drivePath, ct);
            else await drive.ResolveFolderAsync(drivePath, ct); // must exist on Drive (or pass --create)
            var config = Config.Load();
            if (config.Pairs.Any(p => p.LocalPath.Equals(localPath, StringComparison.OrdinalIgnoreCase)))
            {
                Console.Error.WriteLine($"error: {localPath} is already part of a pair (see: unisono config)");
                return 1;
            }
            Directory.CreateDirectory(localPath);
            config.Pairs.Add(new SyncPair { DrivePath = drivePath, LocalPath = localPath });
            config.Save();
            Console.WriteLine($"Added pair: {(drivePath.Length == 0 ? "My Drive" : drivePath)}  <->  {localPath}");
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
                Console.Error.WriteLine("error: no sync pairs configured; use: unisono init <drive/path> <localDir>");
                return 1;
            }
            var drive = await DriveClient.ConnectAsync(ct, quiet: true);
            var engines = config.Pairs.Select(p => new SyncEngine(drive, p, log)).ToList();
            log.Info($"Unisono {version} starting, {engines.Count} pair(s)");
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
    log.Info($"watching {watchers.Count} folder(s), polling Drive every {poll.TotalSeconds:0}s. Ctrl+C to stop.");

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

static string Gb(long bytes) => $"{bytes / 1024.0 / 1024 / 1024:F1} GB";

static void Usage()
{
    Console.WriteLine("usage: unisono auth | ls [drive/path] | pull <drive/path> <localDir> | init <drive/path> <localDir> [--create] | sync [-v] | run [-v] | config | version");
}

sealed class PullStats
{
    public int Downloaded, Skipped, GoogleNative, Failed;
}
