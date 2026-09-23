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

    /// <summary>Drive: folder under My Drive, e.g. "Projects/Unisono" (empty = all of My Drive). Synology: absolute path starting with the shared folder, e.g. "/home/Sync".</summary>
    public string RemotePath { get; set; } = "";

    /// <summary>Older config files called it DrivePath; still accepted.</summary>
    [JsonInclude, JsonPropertyName("DrivePath")]
    internal string? LegacyDrivePath { set { if (!string.IsNullOrEmpty(value)) RemotePath = value; } get => null; }

    public string LocalPath { get; set; } = "";

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
            var key = $"{Kind}|{Server?.ToLowerInvariant()}|{RemotePath.ToLowerInvariant()}|{LocalPath.ToLowerInvariant()}";
            var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key)))[..8].ToLowerInvariant();
            return $"{readable}-{hash}";
        }
    }

    [JsonIgnore]
    public string Name => Kind == RemoteKind.Synology
        ? $"NAS:{RemotePath}"
        : RemotePath.Length == 0 ? "My Drive" : RemotePath;
}

public sealed class Config
{
    public List<SyncPair> Pairs { get; set; } = new();

    public static string FilePath => Path.Combine(AppPaths.DataDir, "config.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static Config Load()
    {
        if (!File.Exists(FilePath)) return new Config();
        return JsonSerializer.Deserialize<Config>(File.ReadAllText(FilePath), Options) ?? new Config();
    }

    public void Save()
    {
        AppPaths.EnsureDataDir();
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
    }
}
