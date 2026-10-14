using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using ViewLight.Models;

namespace ViewLight.Services;

/// <summary>
/// Looks for Imlight servers on the local network by asking each address on this machine's subnets
/// whether anything is listening on the login port.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this can and cannot prove.</b> A port that answers a TCP connection is a port with a server
/// behind it, but nothing is said about <i>which</i> server: another game, an unrelated program, or an
/// Imlight server booting and not yet accepting players would all look the same. So a hit is offered as
/// "something is listening here", never as "a server is ready to play on", and the user decides.
/// </para>
/// <para>
/// Discovery is local by definition. There is no directory of Imlight servers to ask - nothing publishes
/// one - so a server across the internet has to be typed in by address, the way a Minecraft server list
/// works. That is why this only ever sweeps the subnets this machine is actually on.
/// </para>
/// </remarks>
public sealed class LanServerDiscovery {

    /// <summary>How long a single address gets to answer before it is called empty.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMilliseconds(600);

    /// <summary>How many addresses are asked at once. Bounded so a scan cannot flood the network.</summary>
    private const int MaxParallelProbes = 64;

    private readonly Func<IPAddress, int, TimeSpan, CancellationToken, Task<bool>> _probe;

    /// <param name="probe">
    /// How one address is asked. Injected in tests so a scan can be driven without touching a network.
    /// </param>
    public LanServerDiscovery(Func<IPAddress, int, TimeSpan, CancellationToken, Task<bool>>? probe = null)
        => _probe = probe ?? ProbeTcpAsync;

    /// <summary>
    /// Sweeps the subnets this machine is on and returns everything that answered, in address order.
    /// </summary>
    public async Task<IReadOnlyList<ServerEndpoint>> ScanAsync(
        int port = ServerEndpoint.DefaultPort,
        TimeSpan? timeout = null,
        CancellationToken token = default) {

        var hosts = CandidatesFromLocalAddresses();
        return await ScanHostsAsync(hosts, port, timeout, token).ConfigureAwait(false);
    }

    /// <summary>
    /// Asks the given addresses, which is the half of a scan worth testing on its own.
    /// </summary>
    public async Task<IReadOnlyList<ServerEndpoint>> ScanHostsAsync(
        IReadOnlyList<IPAddress> hosts,
        int port = ServerEndpoint.DefaultPort,
        TimeSpan? timeout = null,
        CancellationToken token = default) {

        if (hosts.Count == 0) {
            return [];
        }

        var wait = timeout ?? DefaultTimeout;
        using var gate = new SemaphoreSlim(MaxParallelProbes);

        async Task<ServerEndpoint?> Ask(IPAddress host) {
            try {
                await gate.WaitAsync(token).ConfigureAwait(false);
            }
            catch (Exception) {
                return null;
            }

            try {
                return await _probe(host, port, wait, token).ConfigureAwait(false)
                    ? new ServerEndpoint(host.ToString(), port)
                    : null;
            }
            catch (Exception) {
                // One address that misbehaves - refused, filtered, or a probe that threw - is one
                // address that is not a server. It must not abandon the rest of the sweep.
                return null;
            }
            finally {
                gate.Release();
            }
        }

        var answered = await Task.WhenAll(hosts.Select(Ask)).ConfigureAwait(false);

        return answered
            .Where(endpoint => endpoint is not null)
            .Select(endpoint => endpoint!)
            .OrderBy(endpoint => endpoint.Host, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Every address worth asking on this machine: each current IPv4 address's own /24, minus this
    /// machine itself.
    /// </summary>
    /// <remarks>
    /// Clamped to a /24 on purpose. A machine on a /16 would otherwise sweep 65k addresses, and the
    /// server a person is looking for is on their own segment in every case that matters.
    /// </remarks>
    public static IReadOnlyList<IPAddress> CandidatesFromLocalAddresses() {
        var hosts = new List<IPAddress>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var local in LocalAddresses()) {
            foreach (var host in CandidateHosts(local)) {
                if (seen.Add(host.ToString())) {
                    hosts.Add(host);
                }
            }
        }

        return hosts;
    }

    /// <summary>The addresses of a /24: everything but the network address, the broadcast and the subnet mask.</summary>
    public static IReadOnlyList<IPAddress> CandidateHosts(IPAddress address) {
        var bytes = address.GetAddressBytes();
        if (bytes.Length != 4) {
            // IPv6 has no sane /24 sweep, and Imlight the server is IPv4-addressed anyway.
            return [];
        }

        var hosts = new List<IPAddress>(254);
        for (var last = 1; last <= 254; last++) {
            if (last == bytes[3]) {
                // This machine. Playing locally is what the Play button is for.
                continue;
            }

            hosts.Add(new IPAddress([bytes[0], bytes[1], bytes[2], (byte)last]));
        }

        return hosts;
    }

    /// <summary>The machine's own IPv4 addresses, on interfaces that are actually up.</summary>
    public static IReadOnlyList<IPAddress> LocalAddresses() {
        var addresses = new List<IPAddress>();

        try {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces()) {
                if (nic.OperationalStatus != OperationalStatus.Up
                    || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) {
                    continue;
                }

                foreach (var unicast in nic.GetIPProperties().UnicastAddresses) {
                    if (unicast.Address.AddressFamily == AddressFamily.InterNetwork) {
                        addresses.Add(unicast.Address);
                    }
                }
            }
        }
        catch (Exception) {
            // A machine with no usable network information is simply a machine with nothing to find.
        }

        return addresses;
    }

    private static async Task<bool> ProbeTcpAsync(
        IPAddress address, int port, TimeSpan timeout, CancellationToken token) {
        using var client = new TcpClient();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);

        try {
            await client.ConnectAsync(address, port, deadline.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception) {
            // Refused, filtered or timed out: all mean "nothing to join here".
            return false;
        }
    }
}
