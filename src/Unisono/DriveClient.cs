using System.Security.Cryptography;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Drive.v3.Data;
using Google.Apis.Services;
using Google.Apis.Util.Store;
using File = Google.Apis.Drive.v3.Data.File;

namespace Unisono;

/// <summary>
/// Thin wrapper over the Drive v3 API: sign-in, path resolution, listing and download.
/// Google-native documents (Docs, Sheets, ...) have no bytes to download and are reported but skipped.
/// </summary>
public sealed class DriveClient
{
    public const string FolderMime = "application/vnd.google-apps.folder";
    private const string FileFields = "id, name, mimeType, size, modifiedTime, md5Checksum";

    private readonly DriveService _service;

    private DriveClient(DriveService service) => _service = service;

    /// <summary>
    /// Opens the browser for consent the first time; afterwards the refresh token in <see cref="AppPaths.TokenDir"/>
    /// is used silently. The consent redirect lands on a loopback HttpListener, which the probe confirmed works
    /// for a standard user.
    /// </summary>
    public static async Task<DriveClient> ConnectAsync(CancellationToken ct)
    {
        if (!System.IO.File.Exists(AppPaths.ClientSecretPath))
            throw new InvalidOperationException(
                $"No OAuth client found at {AppPaths.ClientSecretPath}. See README.md for how to create one.");

        ClientSecrets secrets;
        await using (var stream = System.IO.File.OpenRead(AppPaths.ClientSecretPath))
            secrets = (await GoogleClientSecrets.FromStreamAsync(stream, ct)).Secrets;

        var credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
            secrets,
            new[] { DriveService.Scope.Drive },
            "user",
            ct,
            new FileDataStore(AppPaths.TokenDir, fullPath: true));

        var service = new DriveService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "Unisono",
        });
        return new DriveClient(service);
    }

    public async Task<(string Email, long Usage, long Limit, string StartPageToken)> WhoAmIAsync(CancellationToken ct)
    {
        var about = _service.About.Get();
        about.Fields = "user(emailAddress), storageQuota(usage, limit)";
        var a = await about.ExecuteAsync(ct);
        // The changes start token is what the sync engine will poll from; fetching it here proves that API too.
        var token = await _service.Changes.GetStartPageToken().ExecuteAsync(ct);
        return (a.User.EmailAddress, a.StorageQuota.Usage ?? 0, a.StorageQuota.Limit ?? 0, token.StartPageTokenValue);
    }

    /// <summary>Resolves "Foo/Bar" (relative to My Drive) to a folder id. "" or "/" is the root.</summary>
    public async Task<File> ResolveFolderAsync(string drivePath, CancellationToken ct)
    {
        var current = new File { Id = "root", Name = "", MimeType = FolderMime };
        foreach (var part in drivePath.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var req = _service.Files.List();
            req.Q = $"'{current.Id}' in parents and name = '{Escape(part)}' and mimeType = '{FolderMime}' and trashed = false";
            req.Fields = $"files({FileFields})";
            req.PageSize = 10;
            var res = await req.ExecuteAsync(ct);
            var next = res.Files.FirstOrDefault()
                ?? throw new DirectoryNotFoundException($"No folder '{part}' under '{current.Name}' on Drive.");
            if (res.Files.Count > 1)
                Console.Error.WriteLine($"warning: {res.Files.Count} folders named '{part}' under '{current.Name}'; using the first.");
            current = next;
        }
        return current;
    }

    public async Task<List<File>> ListChildrenAsync(string folderId, CancellationToken ct)
    {
        var all = new List<File>();
        string? pageToken = null;
        do
        {
            var req = _service.Files.List();
            req.Q = $"'{folderId}' in parents and trashed = false";
            req.Fields = $"nextPageToken, files({FileFields})";
            req.PageSize = 1000;
            req.OrderBy = "folder, name";
            req.PageToken = pageToken;
            var res = await req.ExecuteAsync(ct);
            all.AddRange(res.Files);
            pageToken = res.NextPageToken;
        } while (pageToken != null);
        return all;
    }

    /// <summary>Downloads to a temp file next to the target, then swaps it in and stamps the Drive modified time.</summary>
    public async Task DownloadAsync(File file, string localPath, CancellationToken ct)
    {
        var tmp = localPath + ".unisono-part";
        await using (var fs = System.IO.File.Create(tmp))
        {
            var progress = await _service.Files.Get(file.Id).DownloadAsync(fs, ct);
            if (progress.Status != Google.Apis.Download.DownloadStatus.Completed)
                throw new IOException($"Download of '{file.Name}' failed: {progress.Exception?.Message ?? progress.Status.ToString()}");
        }
        System.IO.File.Move(tmp, localPath, overwrite: true);
        if (file.ModifiedTimeDateTimeOffset is { } mt)
            System.IO.File.SetLastWriteTimeUtc(localPath, mt.UtcDateTime);
    }

    public static bool IsFolder(File f) => f.MimeType == FolderMime;

    /// <summary>Docs, Sheets, Slides, Forms, ... : exist on Drive but have no byte representation.</summary>
    public static bool IsGoogleNative(File f) =>
        f.MimeType != null && f.MimeType.StartsWith("application/vnd.google-apps.", StringComparison.Ordinal) && !IsFolder(f);

    /// <summary>True when the local file already matches the Drive copy (size and md5), so a pull can skip it.</summary>
    public static bool LocalMatches(File f, string localPath)
    {
        if (!System.IO.File.Exists(localPath) || f.Md5Checksum == null) return false;
        var info = new FileInfo(localPath);
        if (f.Size is { } size && info.Length != size) return false;
        using var md5 = MD5.Create();
        using var fs = System.IO.File.OpenRead(localPath);
        var hash = Convert.ToHexString(md5.ComputeHash(fs));
        return string.Equals(hash, f.Md5Checksum, StringComparison.OrdinalIgnoreCase);
    }

    private static string Escape(string name) => name.Replace("\\", "\\\\").Replace("'", "\\'");
}
