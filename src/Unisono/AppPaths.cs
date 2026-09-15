namespace Unisono;

/// <summary>Where Unisono keeps its own state. Everything lives under the user's profile; nothing needs admin.</summary>
public static class AppPaths
{
    /// <summary>%LOCALAPPDATA%\Unisono</summary>
    public static string DataDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Unisono");

    /// <summary>OAuth client downloaded from Google Cloud Console (see README).</summary>
    public static string ClientSecretPath => Path.Combine(DataDir, "client_secret.json");

    /// <summary>Refresh/access tokens written by the Google client library after the first sign-in.</summary>
    public static string TokenDir => Path.Combine(DataDir, "tokens");

    public static void EnsureDataDir() => Directory.CreateDirectory(DataDir);
}
