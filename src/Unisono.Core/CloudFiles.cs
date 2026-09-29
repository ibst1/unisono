using System.Runtime.InteropServices;
using System.Security.Principal;
using Windows.Storage.Provider;
using FileAttributes = System.IO.FileAttributes;
using StorageFolder = Windows.Storage.StorageFolder;

namespace Unisono;

/// <summary>
/// Files on demand through the Windows Cloud Files API (the one OneDrive uses): a pair's local folder is registered as
/// a sync root, remote items appear as placeholders that take no space until opened, and Explorer shows the status
/// icons and its own "Always keep on this device" / "Free up space" commands, which set the pinned / unpinned
/// attributes that the sync engine acts on. Works for a standard user without package identity.
///
/// Opening an online-only file makes Windows call back into the connected provider (<see cref="HydrationProvider"/>),
/// which lives in the long-running host; everything else here works without a connection.
/// </summary>
public static class CloudFiles
{
    public const FileAttributes Pinned = (FileAttributes)0x80000;
    public const FileAttributes Unpinned = (FileAttributes)0x100000;
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x400000;
    private const FileAttributes RecallOnOpen = (FileAttributes)0x40000;

    /// <summary>
    /// By default Windows disguises placeholders as ordinary files for .NET processes (no reparse attribute). Unisono needs
    /// to see them for what they are, both its own and other providers' (OneDrive, Work Folders).
    /// </summary>
    public static void ExposePlaceholders()
    {
        try { RtlSetProcessPlaceholderCompatibilityMode(2 /* PHCM_EXPOSE_PLACEHOLDERS */); } catch { /* older Windows */ }
    }

    /// <summary>A placeholder whose content is not (fully) on this computer. Reading it downloads it.</summary>
    public static bool IsDehydrated(FileAttributes a) =>
        (a & FileAttributes.ReparsePoint) != 0 && (a & (RecallOnDataAccess | RecallOnOpen | FileAttributes.Offline)) != 0;

    public static bool IsPinned(FileAttributes a) => (a & Pinned) != 0;
    public static bool IsUnpinned(FileAttributes a) => (a & Unpinned) != 0;

    // -------------------------------------------------------------- registration

    /// <summary>
    /// Windows refuses long sync root ids (0x8007007A or "value does not fall within the expected range" somewhere
    /// between 99 and 214 characters), and a pair's id holds its whole remote path. Longer ids keep their hash and the
    /// start of the readable part; shorter ones, i.e. every root registered so far, stay as they are.
    /// </summary>
    public const int MaxPairIdInSyncRoot = 44;

    public static string SyncRootId(SyncPair pair)
    {
        var id = pair.Id;
        if (id.Length > MaxPairIdInSyncRoot)
        {
            var hash = id[(id.LastIndexOf('-') + 1)..];
            id = id[..(MaxPairIdInSyncRoot - hash.Length - 1)].TrimEnd('_') + "-" + hash;
        }
        return $"Unisono!{WindowsIdentity.GetCurrent().User!.Value}!{id}";
    }

    /// <summary>Registers (or re-registers, which is harmless) the pair's local folder as a sync root.</summary>
    public static void Register(SyncPair pair)
    {
        var root = pair.LocalFullPath;
        Directory.CreateDirectory(root);
        var icon = Path.Combine(AppContext.BaseDirectory, "Unisono.exe");
        var info = new StorageProviderSyncRootInfo
        {
            Id = SyncRootId(pair),
            Path = StorageFolder.GetFolderFromPathAsync(root).AsTask().GetAwaiter().GetResult(),
            DisplayNameResource = "Unisono - " + pair.Name,
            IconResource = File.Exists(icon) ? icon + ",0" : @"%SystemRoot%\system32\imageres.dll,-1043",
            HydrationPolicy = StorageProviderHydrationPolicy.Full,
            HydrationPolicyModifier = StorageProviderHydrationPolicyModifier.AutoDehydrationAllowed,
            PopulationPolicy = StorageProviderPopulationPolicy.AlwaysFull,
            InSyncPolicy = StorageProviderInSyncPolicy.FileLastWriteTime | StorageProviderInSyncPolicy.DirectoryLastWriteTime,
            HardlinkPolicy = StorageProviderHardlinkPolicy.None,
            ShowSiblingsAsGroup = false,
            Version = "1",
            Context = System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsBuffer(System.Text.Encoding.UTF8.GetBytes(pair.Id)),
        };
        StorageProviderSyncRootManager.Register(info);
    }

    public static void Unregister(SyncPair pair)
    {
        try { StorageProviderSyncRootManager.Unregister(SyncRootId(pair)); } catch { /* not registered */ }
    }

    /// <summary>
    /// Why a folder cannot hold files on demand, or null when it can: sync roots may not nest, so it must not lie inside
    /// or contain OneDrive, Work Folders or any other provider's folder. And Windows keeps sync roots on local NTFS
    /// volumes only: a network share (mapped drive or \\server\share) or a FAT/exFAT stick cannot hold them.
    /// </summary>
    public static string? WhyNot(string folder, SyncPair? self = null)
    {
        var full = Path.GetFullPath(folder);
        var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Unisono");
        if (full.StartsWith(@"\\", StringComparison.Ordinal))
            return $"{folder} is on a network share. Windows keeps online-only files on this computer's own disks only; turn off files on demand for this pair or pick a local folder, e.g. {local}.";
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(full)!);
            if (drive.DriveType == DriveType.Network)
                return $"{folder} is on a network drive ({drive.Name.TrimEnd('\\')}). Windows keeps online-only files on this computer's own disks only; turn off files on demand for this pair or pick a local folder, e.g. {local}.";
            if (drive.IsReady && !drive.DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
                return $"{folder} is on a {drive.DriveFormat} drive. Windows keeps online-only files on NTFS drives only; turn off files on demand for this pair or pick a folder on another drive, e.g. {local}.";
        }
        catch (ArgumentException) { } // no drive letter: let registration tell
        var mine = WithSep(full);
        var ownId = self == null ? null : SyncRootId(self);
        foreach (var (id, path) in RegisteredRoots())
        {
            if (id.Equals(ownId, StringComparison.OrdinalIgnoreCase)) continue;
            var other = WithSep(path);
            if (mine.StartsWith(other, StringComparison.OrdinalIgnoreCase))
                return $"{folder} lies inside {path}, which {Provider(id)} manages. Windows allows no online-only files inside another provider's folder; pick a folder outside it, e.g. {Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Unisono")}.";
            if (other.StartsWith(mine, StringComparison.OrdinalIgnoreCase))
                return $"{folder} contains {path}, which {Provider(id)} manages. Pick a folder that does not contain it.";
        }
        return null;
    }

    private static string Provider(string id)
    {
        var name = id.Split('!')[0];
        return name.StartsWith("OneDrive", StringComparison.OrdinalIgnoreCase) ? "OneDrive"
            : name.Equals("WorkFolders", StringComparison.OrdinalIgnoreCase) ? "Work Folders"
            : name.Equals("Unisono", StringComparison.OrdinalIgnoreCase) ? "another Unisono pair" : name;
    }

    /// <summary>Every sync root Windows knows for this user: (id, folder).</summary>
    public static IEnumerable<(string Id, string Path)> RegisteredRoots()
    {
        var result = new List<(string, string)>();
        var sid = WindowsIdentity.GetCurrent().User!.Value;
        using var mgr = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\SyncRootManager");
        if (mgr == null) return result;
        foreach (var name in mgr.GetSubKeyNames())
        {
            using var roots = mgr.OpenSubKey(name + @"\UserSyncRoots");
            if (roots == null) continue;
            foreach (var v in roots.GetValueNames())
            {
                // Values are per user SID (Work Folders lists several users' folders); only ours matter.
                if (!v.Equals(sid, StringComparison.OrdinalIgnoreCase)) continue;
                if (roots.GetValue(v) is string p && p.Length > 0) result.Add((name, p));
            }
        }
        return result;
    }

    /// <summary>
    /// Ends files on demand for a pair: what is on this computer becomes ordinary files and folders again, online-only
    /// placeholders are removed and forgotten in the sync state, so the next ordinary cycle downloads them instead of
    /// taking their absence for a local deletion. Returns how many online-only files were removed.
    /// </summary>
    public static int TurnOff(SyncPair pair, Log log)
    {
        var root = pair.LocalFullPath;
        var removed = 0;
        if (Directory.Exists(root))
        {
            var state = SyncState.Load(pair);
            var items = new DirectoryInfo(root).EnumerateFileSystemInfos("*", SearchOption.AllDirectories)
                .OrderByDescending(i => i.FullName.Length).ToList(); // children before their folders
            foreach (var i in items)
            {
                if ((i.Attributes & FileAttributes.ReparsePoint) == 0) continue;
                try
                {
                    if (i is FileInfo && IsDehydrated(i.Attributes))
                    {
                        i.Delete();
                        state.Entries.Remove(Path.GetRelativePath(root, i.FullName).Replace('\\', '/'));
                        removed++;
                    }
                    else if (i.LinkTarget == null) Revert(i.FullName);
                }
                catch (Exception ex) { log.Warn($"[{pair.Name}] {i.Name}: {ex.Message}"); }
            }
            state.Save(pair);
        }
        Unregister(pair);
        log.Info($"[{pair.Name}] files on demand turned off; {removed} online-only file(s) will be downloaded");
        return removed;
    }

    public static bool IsRegistered(SyncPair pair) =>
        RegisteredRoots().Any(r => r.Id.Equals(SyncRootId(pair), StringComparison.OrdinalIgnoreCase));

    private static string WithSep(string p) => p.TrimEnd('\\') + "\\";

    // -------------------------------------------------------------- placeholders

    /// <summary>A new online-only placeholder (or placeholder folder) under an existing folder of the sync root.</summary>
    public static unsafe void CreatePlaceholder(string fullPath, bool isDir, long size, DateTime modifiedUtc, string identity)
    {
        var dir = Path.GetDirectoryName(fullPath)!;
        var name = Marshal.StringToHGlobalUni(Path.GetFileName(fullPath));
        var id = Marshal.StringToHGlobalUni(identity);
        try
        {
            var t = modifiedUtc.ToFileTimeUtc();
            var info = new PlaceholderCreateInfo
            {
                RelativeFileName = name,
                Meta = new FsMetadata { CreationTime = t, LastAccessTime = t, LastWriteTime = t, ChangeTime = t, FileAttributes = isDir ? 0x10u : 0x80u, FileSize = isDir ? 0 : size },
                FileIdentity = id, FileIdentityLength = (uint)(identity.Length * 2),
                // Folders are always populated by Unisono itself (AlwaysFull), never on demand.
                Flags = 0x2 /* MARK_IN_SYNC */ | (isDir ? 0x1u /* DISABLE_ON_DEMAND_POPULATION */ : 0),
            };
            Check(CfCreatePlaceholders(dir, &info, 1, 0, out _), fullPath);
            Check(info.Result, fullPath);
        }
        finally
        {
            Marshal.FreeHGlobal(name);
            Marshal.FreeHGlobal(id);
        }
    }

    /// <summary>Turns an ordinary file or folder into an in-sync placeholder that stays on this computer.</summary>
    public static void Convert(string fullPath, string identity) => WithHandle(fullPath, write: true, h =>
    {
        var id = Marshal.StringToHGlobalUni(identity);
        try { return CfConvertToPlaceholder(h, id, (uint)(identity.Length * 2), 0x1 /* MARK_IN_SYNC */, IntPtr.Zero, IntPtr.Zero); }
        finally { Marshal.FreeHGlobal(id); }
    });

    /// <summary>New remote metadata for a placeholder that is not on this computer (it stays online-only).</summary>
    public static unsafe void UpdateOnline(string fullPath, long size, DateTime modifiedUtc, string identity) => WithHandle(fullPath, write: true, h =>
    {
        var id = Marshal.StringToHGlobalUni(identity);
        try
        {
            var t = modifiedUtc.ToFileTimeUtc();
            var meta = new FsMetadata { CreationTime = t, LastAccessTime = t, LastWriteTime = t, ChangeTime = t, FileAttributes = 0x80u, FileSize = size };
            // DEHYDRATE | MARK_IN_SYNC
            return CfUpdatePlaceholder(h, &meta, id, (uint)(identity.Length * 2), IntPtr.Zero, 0, 0x4 | 0x2, IntPtr.Zero, IntPtr.Zero);
        }
        finally { Marshal.FreeHGlobal(id); }
    });

    public static void MarkInSync(string fullPath) =>
        WithHandle(fullPath, write: true, h => CfSetInSyncState(h, 1 /* IN_SYNC */, 0, IntPtr.Zero));

    /// <summary>Frees the space a synced file takes; it stays visible and comes back when opened.</summary>
    public static void Dehydrate(string fullPath) =>
        WithHandle(fullPath, write: true, h => CfDehydratePlaceholder(h, 0, -1, 0, IntPtr.Zero));

    /// <summary>0 unspecified, 1 pinned ("Always keep on this device"), 2 unpinned ("Free up space").</summary>
    public static void SetPinState(string fullPath, int state) =>
        WithHandle(fullPath, write: true, h => CfSetPinState(h, state, 0, IntPtr.Zero));

    /// <summary>A placeholder back to an ordinary file or folder (only for content that is on this computer).</summary>
    public static void Revert(string fullPath) =>
        WithHandle(fullPath, write: true, h => CfRevertPlaceholder(h, 0, IntPtr.Zero));

    /// <summary>True when Windows considers the placeholder in sync (no pending-sync arrows in Explorer).</summary>
    public static unsafe bool IsInSync(string fullPath)
    {
        var result = true;
        try
        {
            WithHandle(fullPath, write: false, h =>
            {
                var buf = stackalloc byte[4096];
                if (CfGetPlaceholderInfo(h, 0 /* BASIC */, (IntPtr)buf, 4096, out _) == 0) result = *(int*)(buf + 4) == 1;
                return 0;
            });
        }
        catch { }
        return result;
    }

    /// <summary>The identity stored with a placeholder (Unisono stores the remote id), or null for an ordinary file.</summary>
    public static unsafe string? Identity(string fullPath)
    {
        string? result = null;
        try
        {
            WithHandle(fullPath, write: false, h =>
            {
                var buf = stackalloc byte[4096];
                var hr = CfGetPlaceholderInfo(h, 0 /* BASIC */, (IntPtr)buf, 4096, out _);
                if (hr != 0) return 0; // not a placeholder
                var len = *(uint*)(buf + 24);
                result = len == 0 ? "" : new string((char*)(buf + 28), 0, (int)len / 2);
                return 0;
            });
        }
        catch { }
        return result;
    }

    private static void WithHandle(string path, bool write, Func<IntPtr, int> op)
    {
        // Write access is refused on a read-only file (Google Docs exports are kept read-only): lift it meanwhile.
        var attrs = File.GetAttributes(path);
        var readOnly = write && (attrs & FileAttributes.ReadOnly) != 0 && (attrs & FileAttributes.Directory) == 0;
        if (readOnly) File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
        try
        {
            // A protected handle with an oplock: opening this way never downloads a placeholder's content.
            Check(CfOpenFileWithOplock(path, write ? 0x2 /* WRITE_ACCESS */ : 0, out var ph), path);
            try { Check(op(CfGetWin32HandleFromProtectedHandle(ph)), path); }
            finally { CfCloseHandle(ph); }
        }
        finally
        {
            if (readOnly) File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
        }
    }

    private static void Check(int hr, string path)
    {
        if (hr < 0) throw new IOException($"{Path.GetFileName(path)}: {Marshal.GetExceptionForHR(hr)?.Message ?? $"0x{hr:X8}"}", hr);
    }

    // ------------------------------------------------------------------ interop

    [StructLayout(LayoutKind.Sequential)]
    internal struct FsMetadata
    {
        public long CreationTime, LastAccessTime, LastWriteTime, ChangeTime;
        public uint FileAttributes;
        public long FileSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PlaceholderCreateInfo
    {
        public IntPtr RelativeFileName;
        public FsMetadata Meta;
        public IntPtr FileIdentity;
        public uint FileIdentityLength;
        public uint Flags;
        public int Result;
        public long CreateUsn;
    }

    [DllImport("ntdll.dll")] private static extern sbyte RtlSetProcessPlaceholderCompatibilityMode(sbyte mode);
    [DllImport("cldapi.dll", CharSet = CharSet.Unicode)]
    private static extern unsafe int CfCreatePlaceholders(string baseDir, PlaceholderCreateInfo* items, uint count, int flags, out uint processed);
    [DllImport("cldapi.dll", CharSet = CharSet.Unicode)] private static extern int CfOpenFileWithOplock(string path, int flags, out IntPtr protectedHandle);
    [DllImport("cldapi.dll")] private static extern IntPtr CfGetWin32HandleFromProtectedHandle(IntPtr protectedHandle);
    [DllImport("cldapi.dll")] private static extern void CfCloseHandle(IntPtr handle);
    [DllImport("cldapi.dll")] private static extern int CfConvertToPlaceholder(IntPtr h, IntPtr identity, uint identityLength, int flags, IntPtr usn, IntPtr overlapped);
    [DllImport("cldapi.dll")] private static extern unsafe int CfUpdatePlaceholder(IntPtr h, FsMetadata* meta, IntPtr identity, uint identityLength, IntPtr ranges, uint rangeCount, int flags, IntPtr usn, IntPtr overlapped);
    [DllImport("cldapi.dll")] private static extern int CfSetInSyncState(IntPtr h, int state, int flags, IntPtr usn);
    [DllImport("cldapi.dll")] private static extern int CfDehydratePlaceholder(IntPtr h, long offset, long length, int flags, IntPtr overlapped);
    [DllImport("cldapi.dll")] private static extern int CfSetPinState(IntPtr h, int state, int flags, IntPtr overlapped);
    [DllImport("cldapi.dll")] private static extern int CfRevertPlaceholder(IntPtr h, int flags, IntPtr overlapped);
    [DllImport("cldapi.dll")] private static extern int CfGetPlaceholderInfo(IntPtr h, int infoClass, IntPtr buffer, uint length, out uint returned);
}
