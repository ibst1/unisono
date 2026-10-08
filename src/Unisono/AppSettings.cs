using System.Text.Json;

namespace Unisono.App;

/// <summary>Per-machine UI state (window placement, last tab, log detail) in the data dir's ui.json.</summary>
sealed class AppSettings
{
    public int X { get; set; } = int.MinValue;
    public int Y { get; set; }
    public int Width { get; set; } = 1040;
    public int Height { get; set; } = 740;
    public bool Maximized { get; set; }
    public string Tab { get; set; } = "accounts";
    public bool VerboseLog { get; set; }
    /// <summary>When GitHub was last asked for a newer release (once a day), and the newest one the user was told about.</summary>
    public DateTime LastGitHubCheck { get; set; }
    public string? ToldGitHubVersion { get; set; }

    private static string FilePath => Path.Combine(AppPaths.DataDir, "ui.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath)) return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch { }
        return new AppSettings();
    }

    public void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true })); }
        catch { }
    }
}
