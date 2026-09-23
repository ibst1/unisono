using System.Text.Json;

namespace Unisono;

/// <summary>
/// The parts of a Cloud Console client_secret.json that identify which client (and project) it belongs to.
/// The Google library only reads id and secret; the project id is what tells a user they dropped in the wrong file.
/// </summary>
public sealed record OAuthClientInfo(string Kind, string ClientId, string? ProjectId)
{
    /// <summary>Client ids look like "123456789012-abc...apps.googleusercontent.com"; the numeric prefix is the project number.</summary>
    public string TokenFolderName
    {
        get
        {
            var dash = ClientId.IndexOf('-');
            var prefix = dash > 0 ? ClientId[..dash] : ClientId;
            return string.Concat(prefix.Where(char.IsLetterOrDigit));
        }
    }

    public static OAuthClientInfo Read(string path)
    {
        using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(path));
        var root = doc.RootElement;
        foreach (var kind in new[] { "installed", "web" })
        {
            if (!root.TryGetProperty(kind, out var section)) continue;
            var clientId = section.TryGetProperty("client_id", out var id) ? id.GetString() : null;
            if (string.IsNullOrEmpty(clientId))
                throw new InvalidOperationException($"{path} has no client_id under \"{kind}\".");
            var projectId = section.TryGetProperty("project_id", out var pid) ? pid.GetString() : null;
            return new OAuthClientInfo(kind, clientId, projectId);
        }
        throw new InvalidOperationException(
            $"{path} is not a client_secret.json from Cloud Console (expected an \"installed\" section).");
    }
}
