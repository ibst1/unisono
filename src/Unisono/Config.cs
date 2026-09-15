using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Unisono;

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
    /// <summary>Folder under My Drive, e.g. "Projects/Unisono". Empty means all of My Drive.</summary>
    public string DrivePath { get; set; } = "";

    public string LocalPath { get; set; } = "";

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public GoogleDocsMode GoogleDocs { get; set; } = GoogleDocsMode.Export;

    /// <summary>Files at or above this size stay cloud-only (a .url link) unless a local copy already exists. 0 disables.</summary>
    public int CloudOnlyMinMB { get; set; } = 0;

    /// <summary>Subfolders (relative to DrivePath) whose files stay cloud-only.</summary>
    public List<string> CloudOnlyFolders { get; set; } = new();

    /// <summary>How often to ask Drive for changes when nothing happens locally.</summary>
    public int PollSeconds { get; set; } = 30;

    /// <summary>
    /// Safety net: re-list the whole Drive folder this often. The change feed is eventually consistent (a folder
    /// trash was seen to take minutes to appear), so a periodic full listing catches anything it is slow with. 0 disables.
    /// </summary>
    public int FullRescanMinutes { get; set; } = 15;

    /// <summary>Stable id for the state file: readable prefix plus a hash of both paths.</summary>
    [JsonIgnore]
    public string Id
    {
        get
        {
            var readable = Regex.Replace(DrivePath.Trim('/', '\\'), "[^A-Za-z0-9]+", "_").Trim('_');
            if (readable.Length == 0) readable = "MyDrive";
            var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(DrivePath.ToLowerInvariant() + "|" + LocalPath.ToLowerInvariant())))[..8].ToLowerInvariant();
            return $"{readable}-{hash}";
        }
    }

    [JsonIgnore]
    public string Name => DrivePath.Length == 0 ? "My Drive" : DrivePath;
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
