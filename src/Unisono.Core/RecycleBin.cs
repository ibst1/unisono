using System.Runtime.InteropServices;

namespace Unisono;

/// <summary>
/// Local deletes go to the Recycle Bin, never straight to oblivion. If the shell refuses (network drives, huge
/// files, a disabled bin) the item is moved to %LOCALAPPDATA%\Unisono\trash instead, which is still recoverable.
/// </summary>
public static class RecycleBin
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    private const uint FO_DELETE = 3;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

    /// <summary>Returns where the item ended up: "Recycle Bin" or the fallback path.</summary>
    public static string Delete(string fullPath)
    {
        ClearReadOnly(fullPath);
        // On a network path the shell has no Recycle Bin: with "no confirmation" it deletes for good and still reports success.
        if (!IsNetwork(fullPath))
        {
            var op = new SHFILEOPSTRUCT
            {
                wFunc = FO_DELETE,
                pFrom = fullPath + "\0\0", // double-null terminated list
                fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI,
            };
            var rc = SHFileOperation(ref op);
            if (rc == 0 && !op.fAnyOperationsAborted && !Exists(fullPath))
                return "Recycle Bin";
        }

        var fallbackRoot = Path.Combine(AppPaths.DataDir, "trash", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        var target = Path.Combine(fallbackRoot, Path.GetFileName(fullPath));
        Directory.CreateDirectory(fallbackRoot);
        if (Directory.Exists(fullPath))
        {
            if (SameVolume(fullPath, fallbackRoot)) Directory.Move(fullPath, target);
            else { CopyTree(fullPath, target); Directory.Delete(fullPath, recursive: true); } // Directory.Move cannot cross volumes
        }
        else File.Move(fullPath, target, overwrite: true);
        return target;
    }

    public static bool IsNetwork(string path)
    {
        var full = Path.GetFullPath(path);
        if (full.StartsWith(@"\\", StringComparison.Ordinal)) return true;
        try { return new DriveInfo(Path.GetPathRoot(full)!).DriveType == DriveType.Network; }
        catch { return false; }
    }

    private static bool SameVolume(string a, string b) =>
        string.Equals(Path.GetPathRoot(Path.GetFullPath(a)), Path.GetPathRoot(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.EnumerateFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), overwrite: true);
        foreach (var d in Directory.EnumerateDirectories(from)) CopyTree(d, Path.Combine(to, Path.GetFileName(d)));
    }

    private static bool Exists(string p) => File.Exists(p) || Directory.Exists(p);

    private static void ClearReadOnly(string fullPath)
    {
        try
        {
            if (File.Exists(fullPath))
                File.SetAttributes(fullPath, File.GetAttributes(fullPath) & ~FileAttributes.ReadOnly);
            else if (Directory.Exists(fullPath))
                foreach (var f in Directory.EnumerateFiles(fullPath, "*", SearchOption.AllDirectories))
                    File.SetAttributes(f, File.GetAttributes(f) & ~FileAttributes.ReadOnly);
        }
        catch { /* best effort; the shell copes with most of it */ }
    }
}
