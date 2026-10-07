using System.Runtime.InteropServices;
using System.Text;

namespace Unisono.App;

/// <summary>
/// AutoSave for Office documents in synced folders. Office's own AutoSave only works for files on OneDrive and
/// SharePoint, so Unisono saves them itself: every few seconds it asks the running Word, Excel and PowerPoint which open
/// documents have unsaved changes, and saves (as Ctrl+S would) those that lie in a synced folder and have had unsaved
/// changes for <see cref="AppOptions.AutoSaveSeconds"/>. The sync then carries the file over as usual.
/// Left alone: documents never saved, read-only ones, the old formats (.doc, .xls, .ppt, .csv: saving them can ask
/// questions), exports of Google documents (each save would go back into the document), and anything outside the pairs.
/// Office is reached through its document windows (OBJID_NATIVEOM): an Office app registers itself for GetActiveObject
/// only after it first loses focus, which someone writing in it may never do. Office is never started, and no reference
/// to it is kept between rounds, so closing it is never held up. A busy app (Excel while a cell is being edited, a
/// dialog open) refuses the call; the document is simply tried again in the next round.
/// </summary>
sealed class OfficeAutoSave : IDisposable
{
    private readonly Log _log;
    private readonly Func<Config> _config;
    private readonly Func<string, bool> _isExport;
    private readonly Thread _thread;
    // A WaitHandle, not ManualResetEventSlim: waiting on it on an STA thread pumps COM's messages.
    private readonly ManualResetEvent _stop = new(false);
    /// <summary>Since when each open document has had unsaved changes (UTC). Only touched on the worker thread.</summary>
    private readonly Dictionary<string, DateTime> _dirtySince = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _toldOnce = new(StringComparer.OrdinalIgnoreCase);

    private static readonly string[] Extensions = { ".docx", ".docm", ".dotx", ".xlsx", ".xlsm", ".xlsb", ".pptx", ".pptm" };
    private static TimeSpan Round => TimeSpan.FromSeconds(int.TryParse(Environment.GetEnvironmentVariable("UNISONO_AUTOSAVE_ROUND"), out var s) && s > 0 ? s : 5);

    private enum Kind { Word, Excel, PowerPoint }

    public OfficeAutoSave(Log log, Func<Config> config, Func<string, bool> isExport)
    {
        _log = log;
        _config = config;
        _isExport = isExport;
        // COM calls into Office belong on an STA thread; this one is Unisono's own, so a slow Office never stalls the window.
        _thread = new Thread(Loop) { IsBackground = true, Name = "Office AutoSave" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    private void Loop()
    {
        while (!_stop.WaitOne(Round))
        {
            var options = _config().Options;
            if (!options.AutoSaveOffice) { _dirtySince.Clear(); continue; }
            try { RunRound(options); }
            catch (Exception ex) { _log.Debug("office autosave: " + ex.Message); }
            finally
            {
                // Office's objects reached in this round are let go now: a reference kept would keep a closed Office running
                // (for minutes even after Unisono ends: that is how long Windows takes to notice). They belong to this STA
                // thread, so the finalizer cannot release them by itself; this thread does, here.
                GC.Collect();
                GC.WaitForPendingFinalizers();
                Marshal.CleanupUnusedObjectsInCurrentContext();
                GC.Collect();
            }
        }
    }

    private void RunRound(AppOptions options)
    {
        var roots = _config().Pairs.Where(p => p.LocalPath.Length > 0 && p.Direction is SyncDirection.TwoWay or SyncDirection.UploadOnly or SyncDirection.Backup)
            .Select(p => p.LocalFullPath.TrimEnd('\\') + "\\").Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var delay = TimeSpan.FromSeconds(Math.Clamp(options.AutoSaveSeconds, 5, 3600));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (kind, app) in RunningApps())
        {
            dynamic? list = null;
            try
            {
                if (kind == Kind.Excel && !options.AutoSaveExcel) continue;
                // Excel answers nothing useful while a cell is being edited: wait for it.
                if (kind == Kind.Excel && !(bool)((dynamic)app).Ready) { foreach (var k in _dirtySince.Keys) seen.Add(k); continue; }
                list = kind switch { Kind.Word => ((dynamic)app).Documents, Kind.Excel => ((dynamic)app).Workbooks, _ => ((dynamic)app).Presentations };
                int count = list!.Count;
                for (var i = 1; i <= count; i++)
                {
                    object? doc = null;
                    try { doc = list[i]; Consider(kind, app, doc!, roots, delay, seen); }
                    catch (COMException ex) { _log.Debug($"office autosave: {kind} is busy ({ex.Message}); trying again shortly"); }
                    finally { Release(doc); }
                }
            }
            catch (COMException ex) { _log.Debug($"office autosave: {kind} did not answer ({ex.Message})"); foreach (var k in _dirtySince.Keys) seen.Add(k); }
            finally { Release(list); Release(app); }
        }
        foreach (var gone in _dirtySince.Keys.Where(k => !seen.Contains(k)).ToList()) _dirtySince.Remove(gone);
    }

    /// <summary>
    /// Lets go of an Office object at once. Left to the garbage collector, a reference could outlive the round, and a
    /// reference held keeps an Office that was closed running (for minutes even after Unisono ends).
    /// </summary>
    private static void Release(object? o)
    {
        if (o != null && Marshal.IsComObject(o)) try { Marshal.FinalReleaseComObject(o); } catch { }
    }

    private void Consider(Kind kind, dynamic app, dynamic doc, List<string> roots, TimeSpan delay, HashSet<string> seen)
    {
        string full = doc.FullName;
        // Never saved ("Document1") or on a web address (OneDrive's own, with its own AutoSave).
        if (full.Length < 3 || full[1] != ':' && !full.StartsWith(@"\\", StringComparison.Ordinal)) return;
        if (!Extensions.Contains(Path.GetExtension(full), StringComparer.OrdinalIgnoreCase)) return;
        if (!roots.Any(r => full.StartsWith(r, StringComparison.OrdinalIgnoreCase)) || _isExport(full)) return;
        seen.Add(full);
        bool saved = kind == Kind.PowerPoint ? (int)doc.Saved == -1 : (bool)doc.Saved; // PowerPoint: MsoTriState
        bool readOnly = kind == Kind.PowerPoint ? (int)doc.ReadOnly == -1 : (bool)doc.ReadOnly;
        if (saved || readOnly) { _dirtySince.Remove(full); return; }
        var now = DateTime.UtcNow;
        if (!_dirtySince.TryGetValue(full, out var since)) { _dirtySince[full] = now; return; }
        if (now - since < delay) return;
        if (kind == Kind.Excel)
        {
            // Workbook.Save from outside empties Excel's undo list. Excel's own Save command (the ribbon's) is tried
            // instead; it saves the active workbook only, so another one waits until it is the active one again.
            object? active = null;
            try
            {
                active = app.ActiveWorkbook;
                if (active == null || !string.Equals((string)((dynamic)active).FullName, full, StringComparison.OrdinalIgnoreCase)) return;
            }
            finally { Release(active); }
            object? bars = null;
            try { bars = app.CommandBars; ((dynamic)bars).ExecuteMso("FileSave"); }
            finally { Release(bars); }
            if (!(bool)doc.Saved) return; // Excel did not save (busy, a question shown): next round
        }
        else doc.Save();
        _dirtySince.Remove(full);
        var line = $"saved automatically ({kind} had unsaved changes for {(now - since).TotalSeconds:N0} s): {full}";
        if (_toldOnce.Add(full)) _log.Info(line); else _log.Debug(line);
    }

    // ------------------------------------------------------------ finding Office

    /// <summary>Each running Word, Excel and PowerPoint (every instance, e.g. two Excels), as its Application object.</summary>
    private static List<(Kind Kind, object App)> RunningApps()
    {
        var frames = new Dictionary<string, (Kind Kind, string Child)>
        {
            ["OpusApp"] = (Kind.Word, "_WwG"), ["XLMAIN"] = (Kind.Excel, "EXCEL7"), ["PPTFrameClass"] = (Kind.PowerPoint, "mdiClass"),
        };
        var found = new List<(Kind, object)>();
        var processes = new HashSet<uint>();
        EnumWindows((top, _) =>
        {
            if (!frames.TryGetValue(ClassOf(top), out var f)) return true;
            GetWindowThreadProcessId(top, out var pid);
            if (processes.Contains(pid)) return true;
            var child = FindChild(top, f.Child);
            if (child == IntPtr.Zero) return true; // no document open in it
            var iid = IDispatch;
            if (AccessibleObjectFromWindow(child, OBJID_NATIVEOM, ref iid, out var window) == 0 && window != null)
            {
                try { found.Add((f.Kind, (object)((dynamic)window).Application)); processes.Add(pid); }
                catch (COMException) { }
                finally { Release(window); }
            }
            return true;
        }, IntPtr.Zero);
        // Not reached through a window (none found, e.g. PowerPoint with an unusual window): the registered instance, if any.
        foreach (var (kind, progId) in new[] { (Kind.Word, "Word.Application"), (Kind.Excel, "Excel.Application"), (Kind.PowerPoint, "PowerPoint.Application") })
        {
            if (found.Any(x => x.Item1 == kind)) continue;
            if (CLSIDFromProgID(progId, out var clsid) != 0) continue;
            try { GetActiveObject(ref clsid, IntPtr.Zero, out var app); if (app != null) found.Add((kind, app)); }
            catch (COMException) { } // not running
        }
        return found;
    }

    private static IntPtr FindChild(IntPtr parent, string className)
    {
        var result = IntPtr.Zero;
        EnumChildWindows(parent, (h, _) =>
        {
            if (ClassOf(h) != className) return true;
            result = h;
            return false;
        }, IntPtr.Zero);
        return result;
    }

    private static string ClassOf(IntPtr hwnd)
    {
        var sb = new StringBuilder(64);
        return GetClassName(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : "";
    }

    public void Dispose()
    {
        _stop.Set();
        _thread.Join(TimeSpan.FromSeconds(5));
    }

    private const uint OBJID_NATIVEOM = 0xFFFFFFF0;
    private static Guid IDispatch = new("00020400-0000-0000-C000-000000000046");
    private delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc proc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumProc proc, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("oleacc.dll")] private static extern int AccessibleObjectFromWindow(IntPtr hwnd, uint id, ref Guid iid, [MarshalAs(UnmanagedType.IDispatch)] out object? obj);
    [DllImport("ole32.dll", CharSet = CharSet.Unicode)] private static extern int CLSIDFromProgID(string progId, out Guid clsid);
    [DllImport("oleaut32.dll", PreserveSig = false)] private static extern void GetActiveObject(ref Guid clsid, IntPtr reserved, [MarshalAs(UnmanagedType.IUnknown)] out object? obj);
}
