using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Unisono;

/// <summary>
/// Is this computer offline? Being offline is no fault: pairs that need the network wait quietly for it instead of
/// failing round after round. Windows' own verdict (NCSI) alone is not trusted to stop syncing, since some networks block
/// its probe and it then reports "no internet" while everything works: a pair counts as offline when no network is
/// connected at all, or when its cycle failed with a network error while Windows says there is no internet.
/// </summary>
public static class Connectivity
{
    /// <summary>No network adapter is connected (cable out, Wi-Fi off, flight mode).</summary>
    public static bool NoNetwork()
    {
        try { return !NetworkInterface.GetIsNetworkAvailable(); }
        catch { return false; }
    }

    /// <summary>Windows says there is no internet (no connection, local network only, or a captive portal).</summary>
    public static bool NoInternet()
    {
        try
        {
            var profile = Windows.Networking.Connectivity.NetworkInformation.GetInternetConnectionProfile();
            return profile == null
                || profile.GetNetworkConnectivityLevel() != Windows.Networking.Connectivity.NetworkConnectivityLevel.InternetAccess;
        }
        catch { return false; } // unknown: never call it offline on a failed probe
    }

    /// <summary>The failure is the network's (name not found, no route, no answer), not the server's or the data's.</summary>
    public static bool IsNetworkError(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            if (e is SocketException s && s.SocketErrorCode is SocketError.HostNotFound or SocketError.TryAgain or SocketError.NoData
                    or SocketError.NetworkUnreachable or SocketError.HostUnreachable or SocketError.NetworkDown or SocketError.TimedOut)
                return true;
            if (e is HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError }) return true;
        }
        return false;
    }

    /// <summary>This failure means "offline" rather than a problem worth reporting.</summary>
    public static bool MeansOffline(Exception ex) => NoNetwork() || (IsNetworkError(ex) && NoInternet());
}
