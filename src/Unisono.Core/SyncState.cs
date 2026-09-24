using System.Text.Json;
using System.Text.Json.Serialization;

namespace Unisono;

public enum EntryKind
{
    Folder,
    /// <summary>A real file synced both ways.</summary>
    File,
    /// <summary>A Google-native document exported to Office format; derived, one-way, read-only locally.</summary>
    Export,
    /// <summary>A .url shortcut to the file on Drive; derived, one-way.</summary>
    Link,
}

/// <summary>What both sides looked like the last time this path was in sync. The third leg of the three-way compare.</summary>
public sealed class StateEntry
{
    /// <summary>Path relative to the pair root, '/'-separated, as named on Drive.</summary>
    public string Path { get; set; } = "";

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public EntryKind Kind { get; set; }

    public string DriveId { get; set; } = "";
    public string? Md5 { get; set; }
    public long Size { get; set; }
    public DateTime DriveModifiedUtc { get; set; }

    public long LocalMtimeTicks { get; set; }
    public long LocalSize { get; set; }

    /// <summary>For Export/Link: the derived local path ("Plan.docx", "Video.mp4.url"). Null for File/Folder (same as Path).</summary>
    public string? LocalName { get; set; }
    /// <summary>Exports of Google Docs: the document's revision (Docs API) the export was made from.</summary>
    public string? Revision { get; set; }
    /// <summary>Exports: the <see cref="DocxFix.Version"/> they were made with (0 = before repairs existed).</summary>
    public int ExportVersion { get; set; }
}

/// <summary>Per-pair, per-machine persistent state: the remote index (with its changes token) and the sync entries.</summary>
public sealed class SyncState
{
    public string? RootId { get; set; }
    public string? ChangesToken { get; set; }
    /// <summary>When the index was last built from a full listing (the periodic safety rescan is timed from this).</summary>
    public DateTime IndexBuiltUtc { get; set; }
    public List<RemoteNode> Index { get; set; } = new();
    public Dictionary<string, StateEntry> Entries { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    public static string FilePathFor(SyncPair pair) => System.IO.Path.Combine(AppPaths.DataDir, "state", pair.Id + ".json");

    public static SyncState Load(SyncPair pair)
    {
        var path = FilePathFor(pair);
        if (!System.IO.File.Exists(path)) return new SyncState();
        var s = JsonSerializer.Deserialize<SyncState>(System.IO.File.ReadAllText(path), Options) ?? new SyncState();
        // The comparer is not serialized; paths on Windows are case-insensitive.
        s.Entries = new Dictionary<string, StateEntry>(s.Entries, StringComparer.OrdinalIgnoreCase);
        return s;
    }

    public void Save(SyncPair pair)
    {
        var path = FilePathFor(pair);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        System.IO.File.WriteAllText(tmp, JsonSerializer.Serialize(this, Options));
        System.IO.File.Move(tmp, path, overwrite: true);
    }

    public void RemoveUnder(string relPath)
    {
        var prefix = relPath + "/";
        foreach (var key in Entries.Keys.Where(k => k.Equals(relPath, StringComparison.OrdinalIgnoreCase) || k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
            Entries.Remove(key);
    }
}
