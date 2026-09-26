using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Unisono;

public enum RemoteKind
{
    Drive,
    Synology,
    /// <summary>An ordinary folder: a network share, a mapped or USB drive, or another local folder.</summary>
    Folder,
}

public enum SyncDirection
{
    /// <summary>Changes on either side are applied to the other.</summary>
    TwoWay,
    /// <summary>Remote changes (including deletions) are applied locally; nothing done locally is uploaded or undone.</summary>
    DownloadOnly,
    /// <summary>Local changes (including deletions) are applied remotely; nothing done remotely is downloaded or undone.</summary>
    UploadOnly,
    /// <summary>
    /// Like <see cref="UploadOnly"/>, and what a local change replaces or a local deletion removes on the other side is kept
    /// there in a versions folder (see SyncEngine.Backup.cs).
    /// </summary>
    Backup,
}

public enum GoogleDocsMode
{
    /// <summary>Docs/Sheets/Slides are exported to docx/xlsx/pptx as read-only local mirrors.</summary>
    Export,
    /// <summary>A .url link that opens the document on Drive in the browser.</summary>
    Link,
    /// <summary>Not represented locally at all.</summary>
    Skip,
}

public sealed class SyncPair
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public RemoteKind Kind { get; set; } = RemoteKind.Drive;

    /// <summary>Drive: folder under My Drive, e.g. "Projects/Unisono" (empty = all of My Drive). Synology: absolute path starting with the shared folder, e.g. "/home/Sync". Folder: a full path, e.g. "\\server\share\Projects" (may contain %VARS%).</summary>
    public string RemotePath { get; set; } = "";

    /// <summary>Older config files called it DrivePath; still accepted.</summary>
    [JsonInclude, JsonPropertyName("DrivePath")]
    internal string? LegacyDrivePath { set { if (!string.IsNullOrEmpty(value)) RemotePath = value; } get => null; }

    /// <summary>May contain %USERPROFILE%, %APPDATA% and the like, so one pair definition works on several computers.</summary>
    public string LocalPath { get; set; } = "";

    /// <summary>The local folder with environment variables expanded; what everything that touches the disk uses.</summary>
    [JsonIgnore]
    public string LocalFullPath => string.IsNullOrWhiteSpace(LocalPath) ? "" : Path.GetFullPath(Environment.ExpandEnvironmentVariables(LocalPath));

    /// <summary>Folder pairs: the other folder with environment variables expanded.</summary>
    [JsonIgnore]
    public string RemoteFullPath => Kind != RemoteKind.Folder || string.IsNullOrWhiteSpace(RemotePath) ? "" : Path.GetFullPath(Environment.ExpandEnvironmentVariables(RemotePath)).TrimEnd('\\');

    /// <summary>A full path written with %APPDATA%, %LOCALAPPDATA% or %USERPROFILE% where it lies inside them.</summary>
    public static string Portable(string fullPath)
    {
        foreach (var v in new[] { "APPDATA", "LOCALAPPDATA", "USERPROFILE" })
        {
            var root = Environment.GetEnvironmentVariable(v);
            if (string.IsNullOrEmpty(root)) continue;
            root = root.TrimEnd('\\');
            if (fullPath.Equals(root, StringComparison.OrdinalIgnoreCase)) return $"%{v}%";
            if (fullPath.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)) return $"%{v}%" + fullPath[root.Length..];
        }
        return fullPath;
    }

    /// <summary>
    /// Files on demand: everything remote shows up locally, but as online-only placeholders that take no space until
    /// opened; "Always keep on this device" in Explorer keeps a file or folder here, "Free up space" makes it online-only
    /// again. The local folder must not lie inside another provider's folder (OneDrive, Work Folders).
    /// </summary>
    public bool OnDemand { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SyncDirection Direction { get; set; } = SyncDirection.TwoWay;

    /// <summary>gitignore-style patterns for this pair, on top of the global ones and any .unisonoignore files.</summary>
    public List<string> IgnorePatterns { get; set; } = new();
    /// <summary>Drive only: the Google account's e-mail address. Empty means the default (first) account.</summary>
    public string? Account { get; set; }

    /// <summary>Synology only: DSM address, e.g. "https://nas.example.com:5001" or "https://myid.quickconnect.to".</summary>
    public string? Server { get; set; }

    /// <summary>Synology only: DSM account. The password is stored DPAPI-encrypted by 'unisono nas login'.</summary>
    public string? User { get; set; }

    /// <summary>Synology only: accept a self-signed DSM certificate.</summary>
    public bool AllowInvalidCertificate { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public GoogleDocsMode GoogleDocs { get; set; } = GoogleDocsMode.Export;

    /// <summary>Files at or above this size stay cloud-only (a .url link) unless a local copy already exists. 0 disables. Drive only.</summary>
    public int CloudOnlyMinMB { get; set; } = 0;

    /// <summary>
    /// Selective sync, paths relative to RemotePath. When IncludeFolders has entries only those folders (with everything
    /// in them) are synced; folders that appear later are ignored. ExcludeFolders are left out; with no includes, every
    /// other folder is synced, new ones too. Anything out of scope is never touched on either side.
    /// </summary>
    public List<string> IncludeFolders { get; set; } = new();
    public List<string> ExcludeFolders { get; set; } = new();

    /// <summary>The other side holds only ciphertext as a Cryptomator vault (see <see cref="CryptomatorVault"/>). Chosen when the pair is added.</summary>
    public bool Encrypted { get; set; }
    /// <summary>Encrypted pairs: which vault, i.e. which stored key on this computer (<see cref="CryptomatorVault.LoadKey"/>).</summary>
    public string? VaultId { get; set; }

    /// <summary>Files (paths relative to the pair) the user keeps syncing although they change very often: no warning.</summary>
    public List<string> FrequentOk { get; set; } = new();

    /// <summary>Subfolders (relative to RemotePath) whose files stay cloud-only. Drive only.</summary>
    public List<string> CloudOnlyFolders { get; set; } = new();

    /// <summary>How often to ask the remote side for changes when nothing happens locally. A NAS is re-listed in full each time, so keep it moderate there.</summary>
    public int PollSeconds { get; set; } = 30;

    /// <summary>
    /// Drive only. Safety net: re-list the whole Drive folder this often. The change feed is eventually consistent (a folder
    /// trash was seen to take minutes to appear), so a periodic full listing catches anything it is slow with. 0 disables.
    /// </summary>
    public int FullRescanMinutes { get; set; } = 15;

    /// <summary>Stable id for the state file: readable prefix plus a hash of both paths.</summary>
    [JsonIgnore]
    public string Id
    {
        get
        {
            var readable = Regex.Replace(RemotePath.Trim('/', '\\'), "[^A-Za-z0-9]+", "_").Trim('_');
            if (readable.Length == 0) readable = "MyDrive";
            var key = $"{Kind}|{Server?.ToLowerInvariant()}|{RemotePath.ToLowerInvariant()}|{LocalPath.ToLowerInvariant()}" + (string.IsNullOrEmpty(Account) ? "" : "|" + Account.ToLowerInvariant());
            var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key)))[..8].ToLowerInvariant();
            return $"{readable}-{hash}";
        }
    }

    [JsonIgnore]
    public string Name => Kind switch
    {
        RemoteKind.Synology => $"NAS:{RemotePath}",
        RemoteKind.Folder => RemoteFullPath,
        _ => RemotePath.Length == 0 ? "My Drive" : RemotePath,
    };
}

/// <summary>Settings that apply to all pairs.</summary>
public sealed class AppOptions
{
    /// <summary>Before Unisono overwrites a local file, this many earlier versions are kept (0 = off).</summary>
    public int KeepVersions { get; set; } = 5;
    /// <summary>Kept versions older than this are pruned (0 = no age limit).</summary>
    public int KeepVersionsDays { get; set; } = 30;
    /// <summary>Every this many days each pair's content is checked (see <see cref="ContentCheck"/>); 0 = only on request.</summary>
    public int CheckContentDays { get; set; } = 7;
    /// <summary>Where kept versions go; empty = the "versions" folder in Unisono's data folder. May contain %VARS%.</summary>
    public string? VersionsFolder { get; set; }
    /// <summary>No syncing while Windows says the connection is metered (mobile hotspot, a network set to metered).</summary>
    public bool PauseOnMetered { get; set; } = true;
    /// <summary>No syncing during presentations and full-screen apps (Windows' "do not disturb" states).</summary>
    public bool PauseWhilePresenting { get; set; } = false;
    /// <summary>gitignore-style patterns for every pair.</summary>
    public List<string> IgnorePatterns { get; set; } = new() { ".DS_Store" };
    /// <summary>"Open in Google Drive" and the like in Explorer's context menu for synced folders.</summary>
    public bool ExplorerMenu { get; set; } = true;
}

public sealed class Config
{
    public List<SyncPair> Pairs { get; set; } = new();
    public AppOptions Options { get; set; } = new();

    public static string FilePath => Path.Combine(AppPaths.DataDir, "config.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static Config Load()
    {
        var config = File.Exists(FilePath)
            ? JsonSerializer.Deserialize<Config>(File.ReadAllText(FilePath), JsonOptions) ?? new Config()
            : new Config();
        VersionStore.Folder = config.Options.VersionsFolder;
        return config;
    }

    public void Save()
    {
        AppPaths.EnsureDataDir();
        WriteAtomically(FilePath, JsonSerializer.Serialize(this, JsonOptions));
    }

    private static void WriteAtomically(string path, string text)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, text);
        File.Move(tmp, path, overwrite: true);
    }}
