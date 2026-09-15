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
        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            pFrom = fullPath + "\0\0", // double-null terminated list
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI,
        };
        var rc = SHFileOperation(ref op);
        if (rc == 0 && !op.fAnyOperationsAborted && !Exists(fullPath))
            return "Recycle Bin";

        var fallbackRoot = Path.Combine(AppPaths.DataDir, "trash", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        var target = Path.Combine(fallbackRoot, Path.GetFileName(fullPath));
        Directory.CreateDirectory(fallbackRoot);
        if (Directory.Exists(fullPath)) Directory.Move(fullPath, target);
        else File.Move(fullPath, target, overwrite: true);
        return target;
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
