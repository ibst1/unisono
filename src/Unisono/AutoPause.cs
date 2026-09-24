using System.Runtime.InteropServices;

namespace Unisono.App;

/// <summary>
/// Why syncing should wait right now, per the settings: a metered connection (Windows' own connection cost, as set
/// for a mobile hotspot or a network marked metered) or a presentation / full-screen app (the same states that make
/// Windows hold back notifications). Null means go ahead.
/// </summary>
static class AutoPause
{
    public static string? Reason(AppOptions options)
    {
        if (options.PauseOnMetered && IsMetered()) return "metered connection";
        if (options.PauseWhilePresenting && Presenting() is { } what) return what;
        return null;
    }

    // --------------------------------------------------------- metered

    [ComImport, Guid("DCB00008-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface INetworkCostManager
    {
        void GetCost(out uint cost, IntPtr destIpAddr);
        void GetDataPlanStatus(IntPtr dataPlanStatus, IntPtr destIpAddr);
        void SetDestinationAddresses(uint length, IntPtr destIpAddrList, short append);
    }

    private const uint CostFixed = 0x2, CostVariable = 0x4, OverDataLimit = 0x10000, Roaming = 0x40000;

    public static bool IsMetered()
    {
        object? manager = null;
        try
        {
            var type = Type.GetTypeFromCLSID(new Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B")); // NetworkListManager
            if (type == null) return false;
            manager = Activator.CreateInstance(type);
            ((INetworkCostManager)manager!).GetCost(out var cost, IntPtr.Zero); // machine-wide cost
            return (cost & (CostFixed | CostVariable | OverDataLimit | Roaming)) != 0;
        }
        catch { return false; } // unknown means "not metered": never stop syncing because of a failed probe
        finally
        {
            if (manager != null && Marshal.IsComObject(manager)) Marshal.ReleaseComObject(manager);
        }
    }

    // ---------------------------------------------------- presentation

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);

    private static string? Presenting()
    {
        try
        {
            if (SHQueryUserNotificationState(out var state) != 0) return null;
            return state switch
            {
                4 => "presentation",                  // QUNS_PRESENTATION_MODE
                3 => "full-screen app",               // QUNS_RUNNING_D3D_FULL_SCREEN
                2 => "full-screen app",               // QUNS_BUSY
                _ => null,
            };
        }
        catch { return null; }
    }
}
