using System.Text.Json;

namespace Unisono;

public sealed class GoogleAccount
{
    /// <summary>Key of the stored token ("user" for the first account from before multi-account support).</summary>
    public string Id { get; set; } = "";
    public string? Email { get; set; }
}

/// <summary>
/// The Google accounts signed in on this machine. Each has its own refresh token (keyed by <see cref="GoogleAccount.Id"/>);
/// pairs refer to an account by e-mail address, so config.json stays readable and portable between machines.
/// </summary>
public static class GoogleAccounts
{
    public const string LegacyId = "user";
    private static readonly object Gate = new();

    private static string FilePath => Path.Combine(AppPaths.AuthDir, "google-accounts.json");

    public static List<GoogleAccount> Load()
    {
        lock (Gate)
        {
            var list = new List<GoogleAccount>();
            try
            {
                if (File.Exists(FilePath))
                    list = JsonSerializer.Deserialize<List<GoogleAccount>>(File.ReadAllText(FilePath)) ?? new();
            }
            catch { /* unreadable: rebuilt from the tokens below */ }
            // The single account from before this list existed.
            if (list.All(a => a.Id != LegacyId) && DriveClient.HasStoredToken(LegacyId))
                list.Insert(0, new GoogleAccount { Id = LegacyId });
            return list;
        }
    }

    public static void Save(List<GoogleAccount> list)
    {
        lock (Gate)
        {
            AppPaths.EnsureDataDir();
            File.WriteAllText(FilePath, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    /// <summary>By e-mail or id; null or empty means the default (first) account.</summary>
    public static GoogleAccount? Resolve(string? emailOrId)
    {
        var list = Load();
        if (string.IsNullOrEmpty(emailOrId)) return list.FirstOrDefault();
        return list.FirstOrDefault(a => string.Equals(a.Email, emailOrId, StringComparison.OrdinalIgnoreCase))
               ?? list.FirstOrDefault(a => a.Id == emailOrId);
    }

    public static string NewId() => "acct-" + Guid.NewGuid().ToString("N")[..10];

    /// <summary>Records or updates an account after a sign-in; the same e-mail signed in twice keeps the older entry.</summary>
    public static GoogleAccount Upsert(string id, string email)
    {
        var list = Load();
        var existing = list.FirstOrDefault(a => string.Equals(a.Email, email, StringComparison.OrdinalIgnoreCase));
        if (existing != null && existing.Id != id)
        {
            DriveClient.SignOut(id); // duplicate sign-in of a known account: keep the first token
            return existing;
        }
        var mine = list.FirstOrDefault(a => a.Id == id);
        if (mine == null) list.Add(mine = new GoogleAccount { Id = id });
        mine.Email = email;
        Save(list);
        return mine;
    }

    public static void Remove(string id)
    {
        var list = Load();
        list.RemoveAll(a => a.Id == id);
        Save(list);
        DriveClient.SignOut(id);
    }
}
