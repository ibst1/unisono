using System.Security.Cryptography;

namespace Unisono;

/// <summary>Relative paths are '/'-separated and named as on the remote side; these helpers keep that in one place.</summary>
public static class PathUtil
{
    public static string ParentOf(string rel) { var i = rel.LastIndexOf('/'); return i < 0 ? "" : rel[..i]; }
    public static string NameOf(string rel) { var i = rel.LastIndexOf('/'); return i < 0 ? rel : rel[(i + 1)..]; }
    public static int Depth(string rel) => rel.Count(c => c == '/');
    public static string Join(string parentRel, string name) => parentRel.Length == 0 ? name : parentRel + "/" + name;

    public static string Md5Of(string localPath)
    {
        using var md5 = MD5.Create();
        using var fs = OpenShared(localPath);
        return Convert.ToHexString(md5.ComputeHash(fs)).ToLowerInvariant();
    }

    /// <summary>Read even while another program has the file open for writing (Office keeps its documents open).</summary>
    public static FileStream OpenShared(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
}
