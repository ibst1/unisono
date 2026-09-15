namespace Unisono;

/// <summary>Where Unisono keeps its own state. Everything lives under the user's profile; nothing needs admin.</summary>
public static class AppPaths
{
    /// <summary>%LOCALAPPDATA%\Unisono: the OAuth client and tokens. Never redirected, so tests share the sign-in.</summary>
    public static string AuthDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Unisono");

    /// <summary>Config, state, logs and the fallback trash. Same as <see cref="AuthDir"/> unless UNISONO_DATA points elsewhere (used by tests).</summary>
    public static string DataDir { get; } =
        Environment.GetEnvironmentVariable("UNISONO_DATA") is { Length: > 0 } d ? Path.GetFullPath(d) : AuthDir;

    /// <summary>OAuth client downloaded from Google Cloud Console (see README).</summary>
    public static string ClientSecretPath => Path.Combine(AuthDir, "client_secret.json");

    /// <summary>Refresh/access tokens written by the Google client library after the first sign-in.</summary>
    public static string TokenDir => Path.Combine(AuthDir, "tokens");

    public static void EnsureDataDir()
    {
        Directory.CreateDirectory(AuthDir);
        Directory.CreateDirectory(DataDir);
    }
}
