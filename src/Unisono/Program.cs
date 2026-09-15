using System.Reflection;
using Unisono;
using File = Google.Apis.Drive.v3.Data.File;

// v0: prove the API path from a locked-down machine. Commands:
//   unisono auth                        sign in (opens the browser once) and print the account
//   unisono ls [drive/path]             list a Drive folder
//   unisono pull <drive/path> <localDir> one-way mirror Drive -> local (adds and updates only, never deletes)
//   unisono version

var asm = Assembly.GetExecutingAssembly();
var version = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";
var buildTime = asm.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "BuildTime")?.Value;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
var ct = cts.Token;

try
{
    AppPaths.EnsureDataDir();
    switch (args.Length > 0 ? args[0].ToLowerInvariant() : "help")
    {
        case "version":
            Console.WriteLine($"Unisono {version}{(buildTime != null ? $" ({buildTime})" : "")}");
            Console.WriteLine($"data: {AppPaths.DataDir}");
            return 0;

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
            var drive = await DriveClient.ConnectAsync(ct);
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

        case "pull":
        {
            if (args.Length < 3) { Usage(); return 2; }
            var drive = await DriveClient.ConnectAsync(ct);
            var folder = await drive.ResolveFolderAsync(args[1], ct);
            var stats = new PullStats();
            await PullAsync(drive, folder, Path.GetFullPath(args[2]), stats, ct);
            Console.WriteLine($"Done: {stats.Downloaded} downloaded, {stats.Skipped} up to date, {stats.GoogleNative} Google-native skipped, {stats.Failed} failed");
            return stats.Failed == 0 ? 0 : 1;
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

static string Gb(long bytes) => $"{bytes / 1024.0 / 1024 / 1024:F1} GB";

static void Usage()
{
    Console.WriteLine("usage: unisono auth | ls [drive/path] | pull <drive/path> <localDir> | version");
}

sealed class PullStats
{
    public int Downloaded, Skipped, GoogleNative, Failed;
}
