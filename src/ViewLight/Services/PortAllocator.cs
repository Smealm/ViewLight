using System.Net;
using System.Net.Sockets;

namespace ViewLight.Services;

/// <summary>Finds TCP ports that are actually free to bind.</summary>
public static class PortAllocator {

    /// <summary>
    /// Range to pick from. Kept below 49152 so we avoid the range Windows hands out as ephemeral
    /// ports, where a random pick could collide with an outbound connection.
    /// </summary>
    public const int MinimumPort = 20000;
    public const int MaximumPort = 49000;

    /// <summary>Can something listen on this port on the loopback interface right now?</summary>
    public static bool IsAvailable(int port) {
        if (port is < 1 or > 65535) {
            return false;
        }

        TcpListener? listener = null;
        try {
            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            return true;
        }
        catch (SocketException) {
            return false;
        }
        finally {
            try {
                listener?.Stop();
            }
            catch (SocketException) {
                // Nothing useful to do if the probe listener refuses to close.
            }
        }
    }

    /// <summary>Picks a free port, or <c>null</c> if none could be found.</summary>
    public static int? FindFreePort() {
        for (var attempt = 0; attempt < 64; attempt++) {
            var candidate = Random.Shared.Next(MinimumPort, MaximumPort + 1);
            if (IsAvailable(candidate)) {
                return candidate;
            }
        }

        return null;
    }
}
