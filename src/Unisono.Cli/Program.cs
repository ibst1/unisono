using System.Reflection;
using Unisono;
using File = Google.Apis.Drive.v3.Data.File;

// Commands:
//   unisono auth                                    Google: sign in (opens the browser once) and print the account
//   unisono nas login <server> <user>               Synology: store DSM credentials (DPAPI) after a test login
//   unisono nas logout <server>                     Synology: forget stored credentials
//   unisono init <remote/path> <localDir> [--create] [--nas <server> <user> | --folder | --box [login]]
//   unisono box app <client id> | login | accounts | ls [path] | logout <login>   Box: your app, sign-in, browsing
//   unisono sync [-v]                               run one sync cycle for every pair
//   unisono run [-v]                                keep syncing: watch the local folders, poll the remote side
//   unisono ls [drive/path] | pull <drive/path> <localDir> | changes <token>   Drive helpers
//   unisono config | version
//   unisono groups [create <name> <hub> <member>... | members <name> <member>... | hub <name> <hub> | join <hub> | remove <name> [--everywhere]]

var asm = Assembly.GetExecutingAssembly();
var version = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";
var buildTime = asm.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "BuildTime")?.Value;

CloudFiles.ExposePlaceholders();
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
var ct = cts.Token;
var log = new Log { Verbose = args.Contains("-v") };
var dryRun = args.Contains("--dry-run");
// Carries out a mass deletion that a cycle would otherwise hold back (see SyncEngine.Held).
var allowDeletes = args.Contains("--allow-deletes");
args = args.Where(a => a is not ("-v" or "--dry-run" or "--allow-deletes")).ToArray();
var stores = new StoreFactory(log, interactive: true);

try
{
    AppPaths.EnsureDataDir();
    switch (args.Length > 0 ? args[0].ToLowerInvariant() : "help")
    {
        case "ondemand-off":
        {
            // Ends files on demand for every pair that has it (placeholders back to ordinary files, sync roots unregistered).
            var config = Config.Load();
            foreach (var p in config.Pairs.Where(p => p.OnDemand))
            {
                CloudFiles.TurnOff(p, log);
                p.OnDemand = false;
            }
            config.Save();
            return 0;
        }
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

        case "box":
        {
            var sub = args.Length > 1 ? args[1].ToLowerInvariant() : "";
            if (sub == "app" && args.Length >= 3)
            {
                // The secret is typed (not echoed), or piped in: it never goes on the command line.
                Console.Write("Client secret of the Box app: ");
                var secret = ReadSecret().Trim();
                if (secret.Length == 0) { Console.Error.WriteLine("no secret given"); return 1; }
                new BoxApp { ClientId = args[2].Trim(), ClientSecret = secret }.Save();
                Console.WriteLine($"Box app stored (redirect URI to register in the app: {BoxApp.RedirectUri})");
                return 0;
            }
            if (sub == "login")
            {
                var client = await BoxClient.SignInAsync(url => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }), ct);
                var (login, name, used, limit) = await client.MeAsync(ct);
                Console.WriteLine($"Signed in to Box as {login} ({name}); {Gb(used)} of {(limit > 0 ? Gb(limit) : "unlimited")} used");
                return 0;
            }
            if (sub == "logout" && args.Length >= 3) { BoxAccount.Delete(args[2]); Console.WriteLine($"forgot the Box sign-in of {args[2]}"); return 0; }
            if (sub == "ls")
            {
                var box = BoxClient.Connect(args.Length > 3 ? args[3] : null);
                var path = args.Length > 2 ? args[2] : "";
                foreach (var i in (await box.ListAsync(await box.ResolveFolderAsync(path, ct), ct)).OrderBy(i => !i.IsFolder).ThenBy(i => i.Name))
                    Console.WriteLine(i.IsFolder ? $"  {i.Name}/" : $"  {i.Name}  ({i.Size:N0} bytes, {i.ModifiedUtc:yyyy-MM-dd HH:mm})");
                return 0;
            }
            if (sub == "put" && args.Length >= 5)
            {
                // Tests: a local file uploaded under any name, also one Windows cannot hold ("a:b.txt").
                var box = BoxClient.Connect(null);
                var item = await box.UploadAsync(Path.GetFullPath(args[2]), await box.ResolveFolderAsync(args[3], ct), args[4], null, ct);
                Console.WriteLine($"uploaded {args[3]}/{item.Name}");
                return 0;
            }
            if (sub == "exists" && args.Length >= 4)
            {
                // Tests: whether a name is in a Box folder (exit code 0 = yes).
                var box = BoxClient.Connect(null);
                return (await box.ListAsync(await box.ResolveFolderAsync(args[2], ct), ct)).Any(i => i.Name == args[3]) ? 0 : 1;
            }
            if (sub == "trash" && args.Length >= 3)
            {
                // Tests: the test folder into Box's trash.
                var box = BoxClient.Connect(args.Length > 3 ? args[3] : null);
                await box.DeleteAsync(await box.ResolveFolderAsync(args[2], ct), folder: true, ct);
                Console.WriteLine($"moved Box folder {args[2]} to the trash");
                return 0;
            }
            if (sub == "accounts")
            {
                Console.WriteLine(BoxClient.HasApp() ? $"Box app set (redirect URI {BoxApp.RedirectUri})" : "no Box app yet: UnisonoCli box app <client id>");
                foreach (var a in BoxAccount.ListAll()) Console.WriteLine($"  {a.Login} ({a.Name})");
                return 0;
            }
            Usage();
            return 2;
        }

        case "groups":
        {
            var config = Config.Load();
            var sub = args.Length > 1 ? args[1].ToLowerInvariant() : "list";
            if (sub == "create" && args.Length >= 5)
            {
                var g = new SyncGroup
                {
                    Name = args[2], Hub = SyncPair.Portable(SyncGroups.Full(args[3])),
                    Members = args.Skip(4).Select(m => SyncPair.Portable(SyncGroups.Full(m))).ToList(),
                };
                if (SyncGroups.Problem(config, g) is { } problem) { Console.Error.WriteLine(problem); return 1; }
                foreach (var m in g.Members) Directory.CreateDirectory(SyncGroups.Full(m));
                SyncGroups.Apply(config, g, log);
                config.Save();
                Console.WriteLine($"group '{g.Name}': {g.Members.Count} folder(s) kept in step with {g.HubFullPath}");
                return 0;
            }
            if (sub == "members" && args.Length >= 4)
            {
                var old = config.Groups.FirstOrDefault(x => x.Name.Equals(args[2], StringComparison.OrdinalIgnoreCase) || x.Id == args[2]);
                if (old == null) { Console.Error.WriteLine($"no group '{args[2]}'"); return 1; }
                var g = SyncGroups.Copy(old);
                g.Members = args.Skip(3).Select(m => old.Members.FirstOrDefault(x => SyncGroups.Full(x).Equals(SyncGroups.Full(m), StringComparison.OrdinalIgnoreCase))
                                                     ?? SyncPair.Portable(SyncGroups.Full(m))).ToList();
                if (SyncGroups.Problem(config, g) is { } problem) { Console.Error.WriteLine(problem); return 1; }
                foreach (var m in g.Members) Directory.CreateDirectory(SyncGroups.Full(m));
                SyncGroups.Apply(config, g, log);
                config.Save();
                return 0;
            }
            if (sub == "hub" && args.Length >= 4)
            {
                // Another folder becomes the hub; a member that does leaves the members, and the old hub becomes one.
                var old = config.Groups.FirstOrDefault(x => x.Name.Equals(args[2], StringComparison.OrdinalIgnoreCase) || x.Id == args[2]);
                if (old == null) { Console.Error.WriteLine($"no group '{args[2]}'"); return 1; }
                var g = SyncGroups.WithHub(old, args[3]);
                if (SyncGroups.Problem(config, g) is { } problem) { Console.Error.WriteLine(problem); return 1; }
                SyncGroups.Apply(config, g, log);
                config.Save();
                return 0;
            }
            if (sub == "join" && args.Length >= 3)
            {
                SyncGroups.Join(config, args[2].Length == 32 && !args[2].Contains('\\') ? args[2] : SyncGroups.Full(args[2]), log);
                config.Save();
                return 0;
            }
            if (sub == "remove" && args.Length >= 3)
            {
                var g = config.Groups.FirstOrDefault(x => x.Name.Equals(args[2], StringComparison.OrdinalIgnoreCase) || x.Id == args[2]);
                if (g == null) { Console.Error.WriteLine($"no group '{args[2]}'"); return 1; }
                SyncGroups.Remove(config, g, args.Contains("--everywhere"), log);
                config.Save();
                return 0;
            }
            if (sub != "list") { Usage(); return 2; }
            var (changed, offers) = SyncGroups.Scan(config, log);
            if (changed) config.Save();
            foreach (var g in config.Groups)
            {
                Console.WriteLine($"{g.Name}  hub {g.HubFullPath}");
                foreach (var m in g.Members) Console.WriteLine($"  {SyncGroups.Full(m)}{(Directory.Exists(SyncGroups.Full(m)) ? "" : "  (not on this computer)")}");
            }
            foreach (var o in offers)
            {
                Console.WriteLine($"can join: {o.Name}  hub {o.Hub}  ({o.MembersHere.Count} of {o.Members.Count} folder(s) on this computer; UnisonoCli groups join \"{o.Hub}\")");
                foreach (var p in o.Replaces) Console.WriteLine($"  replaces the pair {p.LocalFullPath} <-> {p.Name}");
            }
            if (config.Groups.Count == 0 && offers.Count == 0) Console.WriteLine("(no sync groups)");
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
                Console.WriteLine($"{f.ModifiedTimeDateTimeOffset:yyyy-MM-dd HH:mm:ss.fff}  {kind,10}  {f.Name}  v{f.Version}  md5 {f.Md5Checksum ?? "-"}  id {f.Id}");
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

        case "drive-trash":
        {
            // Tests: move a Drive folder (e.g. a test folder) to the Drive trash. Refuses My Drive itself.
            if (args.Length < 2 || args[1].Trim('/', '\\').Length == 0) { Usage(); return 2; }
            var drive = await DriveClient.ConnectAsync(ct, quiet: true);
            var folder = await drive.ResolveFolderAsync(args[1], ct);
            await drive.TrashAsync(folder.Id, ct);
            Console.WriteLine($"moved {args[1]} to the Drive trash");
            return 0;
        }
        case "docx-fix":
        {
            // Diagnostic: repair a docx the way exports are repaired (fractional sizes Word rejects).
            if (args.Length < 2) { Usage(); return 2; }
            List<(string, int)>? tabs = null;
            if (args.Length > 2) { var drive = await DriveClient.ConnectAsync(ct, quiet: true); tabs = await drive.DocsTabsAsync(args[2], ct); Console.WriteLine("tabs: " + string.Join(", ", tabs?.Select(t => $"{t.Item1} ({t.Item2})") ?? Array.Empty<string>())); }
            DocxFix.Apply(args[1], tabs);
            Console.WriteLine($"repaired {args[1]}");
            return 0;
        }
        case "drive-newtabdoc":
        {
            // Tests: a Google document with two tabs: "First" (a heading and a paragraph) and "Second" (a paragraph).
            if (args.Length < 3) { Usage(); return 2; }
            var drive = await DriveClient.ConnectAsync(ct, quiet: true);
            var folder = await drive.ResolveFolderAsync(args[1], ct);
            var file = await drive.CreateNativeAsync(args[2], "application/vnd.google-apps.document", folder.Id, ct);
            var second = await drive.DocsAddTabAsync(file.Id, "Second", ct);
            await drive.DocsRequestsAsync(file.Id, new object[]
            {
                new { updateDocumentTabProperties = new { tabProperties = new { tabId = "t.0", title = "First" }, fields = "title" } },
                new { insertText = new { location = new { index = 1, tabId = second }, text = "Text in the second tab" } },
                new { insertText = new { location = new { index = 1, tabId = "t.0" }, text = "Heading one\nText in the first tab" } },
                new { updateParagraphStyle = new { range = new { startIndex = 1, endIndex = 13, tabId = "t.0" }, paragraphStyle = new { namedStyleType = "HEADING_1" }, fields = "namedStyleType" } },
            }, ct);
            Console.WriteLine($"created {args[1]}/{args[2]} with tabs First, Second");
            return 0;
        }
        case "drive-doctext":
        {
            // Tests: each tab of a Google document with its paragraphs and styles.
            if (args.Length < 3) { Usage(); return 2; }
            var drive = await DriveClient.ConnectAsync(ct, quiet: true);
            var folder = await drive.ResolveFolderAsync(args[1], ct);
            var file = (await drive.ListChildrenAsync(folder.Id, ct)).FirstOrDefault(f => f.Name == args[2]) ?? throw new FileNotFoundException(args[2]);
            using var json = await drive.DocsDocumentAsync(file.Id, ct) ?? throw new InvalidOperationException("Docs API not reachable");
            foreach (var tab in DocsPatch.ReadGoogle(json.RootElement).Tabs)
            {
                Console.WriteLine($"[tab] {tab.Title}");
                foreach (var p in tab.Paras) Console.WriteLine($"  {p.Style}: {p.Text}");
            }
            return 0;
        }
        case "drive-docedit":
        {
            // Tests: edit a Google document through the Docs API, like typing in the browser.
            if (args.Length < 4) { Usage(); return 2; }
            var drive = await DriveClient.ConnectAsync(ct, quiet: true);
            var folder = await drive.ResolveFolderAsync(args[1], ct);
            var file = (await drive.ListChildrenAsync(folder.Id, ct)).FirstOrDefault(f => f.Name == args[2]) ?? throw new FileNotFoundException(args[2]);
            await drive.DocsInsertAsync(file.Id, args[3], ct);
            Console.WriteLine($"edited {args[1]}/{args[2]}");
            return 0;
        }
        case "drive-docrisk":
        {
            // Diagnostic: what a docx round trip would lose in a Google document (tabs, open comments).
            if (args.Length < 2) { Usage(); return 2; }
            var drive = await DriveClient.ConnectAsync(ct, quiet: true);
            var r = await drive.RoundTripRiskAsync(args[1], isDocument: true, ct);
            Console.WriteLine(r == null ? "Docs API not reachable (not enabled for this OAuth project?)" : $"tabs={r.Value.Tabs} openComments={r.Value.Comments}");
            return 0;
        }        case "drive-newdoc":
        {
            // Tests: an empty Google document (Docs) in a Drive folder.
            if (args.Length < 3) { Usage(); return 2; }
            var drive = await DriveClient.ConnectAsync(ct, quiet: true);
            var folder = await drive.ResolveFolderAsync(args[1], ct);
            await drive.CreateNativeAsync(args[2], "application/vnd.google-apps.document", folder.Id, ct);
            Console.WriteLine($"created Google document {args[1]}/{args[2]}");
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
            var folder = args.Contains("--folder");
            var onDemand = args.Contains("--on-demand");
            var nasIdx = Array.IndexOf(args, "--nas");
            string? server = null, user = null;
            if (nasIdx >= 0)
            {
                if (args.Length < nasIdx + 3) { Usage(); return 2; }
                server = NormalizeServer(args[nasIdx + 1]);
                user = args[nasIdx + 2];
                args = args.Where((_, i) => i < nasIdx || i > nasIdx + 2).ToArray();
            }
            // --box [login]: a Box folder (the login may be left out when one Box account is signed in).
            var boxIdx = Array.IndexOf(args, "--box");
            string? boxLogin = null;
            if (boxIdx >= 0)
            {
                if (args.Length > boxIdx + 1 && args[boxIdx + 1].Contains('@')) boxLogin = args[boxIdx + 1];
                boxLogin ??= BoxAccount.Resolve(null)?.Login ?? throw new InvalidOperationException("no Box account signed in (UnisonoCli box login)");
                args = args.Where((a, i) => i != boxIdx && !(i == boxIdx + 1 && a == boxLogin)).ToArray();
            }
            args = args.Where(a => a is not ("--create" or "--insecure" or "--folder" or "--on-demand")).ToArray();
            if (args.Length < 3) { Usage(); return 2; }
            var localPath = Path.GetFullPath(args[2]);
            var pair = new SyncPair
            {
                Kind = folder ? RemoteKind.Folder : server != null ? RemoteKind.Synology : boxIdx >= 0 ? RemoteKind.Box : RemoteKind.Drive,
                RemotePath = folder ? Path.GetFullPath(args[1]).TrimEnd('\\') : server != null ? "/" + args[1].Replace('\\', '/').Trim('/') : args[1].Replace('\\', '/').Trim('/'),
                LocalPath = localPath,
                Server = server,
                User = user,
                Account = boxLogin,
                AllowInvalidCertificate = insecure,
                OnDemand = onDemand,
            };
            if (onDemand && CloudFiles.WhyNot(localPath) is { } why) { Console.Error.WriteLine("error: " + why); return 1; }
            if (pair.Kind == RemoteKind.Synology) pair.PollSeconds = 60;

            var config = Config.Load();
            if (config.Pairs.Any(p => p.LocalFullPath.Equals(localPath, StringComparison.OrdinalIgnoreCase)))
            {
                Console.Error.WriteLine($"error: {localPath} is already part of a pair (see: unisono config)");
                return 1;
            }
            // Verify the remote folder (or create it) before touching the config.
            if (pair.Kind == RemoteKind.Folder)
            {
                if (create) Directory.CreateDirectory(pair.RemoteFullPath);
                else if (!Directory.Exists(pair.RemoteFullPath)) throw new DirectoryNotFoundException(pair.RemoteFullPath);
            }
            else if (pair.Kind == RemoteKind.Synology)
            {
                var store = (SynologyStore)await stores.CreateAsync(pair, ct);
                if (create) await store.EnsureRootAsync(ct);
                await store.GetTreeAsync(log, ct);
            }
            else if (pair.Kind == RemoteKind.Box)
            {
                var box = stores.Box(boxLogin);
                if (create) await box.EnsureFolderAsync(pair.RemotePath, ct);
                else await box.ResolveFolderAsync(pair.RemotePath, ct);
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
            log.Info($"UnisonoCli {version} starting, {config.Pairs.Count} pair(s)");
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
                foreach (var p in config.Pairs)
                {
                    try
                    {
                        if (VaultConverter.Pending(p) && !dryRun)
                        {
                            // Turning the pair's remote folder into a Cryptomator vault: a one-off sync does it all.
                            var converter = new VaultConverter(p, await stores.CreatePlainAsync(p, ct), await stores.CreateVaultAsync(p, ct), log, VaultConverter.PendingDecrypt(p));
                            for (var turn = 0; turn < 5 && !await converter.RunAsync(null, s => { }, ct); turn++) { }
                        }
                        var engine = new SyncEngine(await stores.CreateAsync(p, ct), p, log, config.Pairs, config.Options) { DryRun = dryRun, AllowDeletionsOnce = allowDeletes };
                        var s = await engine.RunCycleAsync(ct);
                        // Files still being written were left alone: a one-off sync waits for them rather than skipping them.
                        for (var round = 0; round < 5 && engine.NeedsFollowUp && !dryRun; round++)
                        {
                            await Task.Delay(2500, ct);
                            s = await engine.RunCycleAsync(ct);
                        }
                        failed += s.Errors;
                        if (dryRun) Console.WriteLine($"[{p.Name}] dry run: {engine.Plan.Count} action(s) planned, nothing changed");
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { log.Error($"[{p.Name}] cycle failed: {Log.Text(ex)}"); log.Debug(ex.ToString()); failed++; }
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
    Console.WriteLine("  UnisonoCli init <otherDir> <localDir> --folder [--create]   (two ordinary folders, e.g. a network share)");
    Console.WriteLine("  UnisonoCli init ... --on-demand      files on demand: online-only placeholders, downloaded when opened or kept");
    Console.WriteLine("  UnisonoCli ondemand-off              end files on demand for all pairs (keeps what is local, unregisters)");
    Console.WriteLine("  UnisonoCli sync [-v] [--dry-run] [--allow-deletes]   |   UnisonoCli run [-v]   |   UnisonoCli config   |   UnisonoCli version");
    Console.WriteLine("  UnisonoCli ls [drive/path]   |   UnisonoCli pull <drive/path> <localDir>   |   UnisonoCli changes <token>");
    Console.WriteLine("  UnisonoCli box app <client id>       store your Box app (the secret is asked for)   |   box login   |   box accounts");
    Console.WriteLine("  UnisonoCli box ls [path] [login]     |   box logout <login>   |   init <box/path> <localDir> --box [login] [--create]");
    Console.WriteLine("  UnisonoCli groups                    sync groups, and groups found in synced folders that this computer can join");
    Console.WriteLine("  UnisonoCli groups create <name> <hubDir> <memberDir>...   |   groups members <name> <memberDir>...   |   groups hub <name> <newHubDir>");
    Console.WriteLine("  UnisonoCli groups join <hubDir>   |   groups remove <name> [--everywhere]");
}

sealed class PullStats
{
    public int Downloaded, Skipped, GoogleNative, Failed;
}
