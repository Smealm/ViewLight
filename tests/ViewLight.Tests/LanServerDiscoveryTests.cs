using System.Net;
using ViewLight.Services;
using Xunit;

namespace ViewLight.Tests;

/// <summary>
/// The network sweep is driven by a probe that is injected here, so what a scan does with the answers
/// can be tested without a network - and, more to the point, without a test that depends on whatever is
/// plugged into the machine running it.
/// </summary>
public class LanServerDiscoveryTests {

    private static LanServerDiscovery Answering(Func<IPAddress, bool> answers)
        => new((address, _, _, _) => Task.FromResult(answers(address)));

    [Fact]
    public async Task KeepsOnlyTheAddressesThatAnswered() {
        var hosts = new[] {
            IPAddress.Parse("10.0.0.1"),
            IPAddress.Parse("10.0.0.2"),
            IPAddress.Parse("10.0.0.3"),
        };

        var found = await Answering(address => address.ToString().EndsWith(".2")).ScanHostsAsync(hosts);

        Assert.Equal(["10.0.0.2:12000"], found.Select(endpoint => endpoint.ToString()).ToArray());
    }

    [Fact]
    public async Task AsksEveryAddressOnThePortItWasGiven() {
        var asked = new List<int>();
        var discovery = new LanServerDiscovery((_, port, _, _) => {
            lock (asked) {
                asked.Add(port);
            }

            return Task.FromResult(false);
        });

        await discovery.ScanHostsAsync([IPAddress.Parse("10.0.0.1"), IPAddress.Parse("10.0.0.2")], 13001);

        Assert.Equal([13001, 13001], asked.Order());
    }

    [Fact]
    public async Task AScanOfNothingAsksNothingAndFindsNothing() {
        var asked = 0;
        var discovery = new LanServerDiscovery((_, _, _, _) => {
            asked++;
            return Task.FromResult(true);
        });

        Assert.Empty(await discovery.ScanHostsAsync([]));
        Assert.Equal(0, asked);
    }

    [Fact]
    public async Task AProbeThatThrowsIsJustAnAddressWithNoServerOnIt() {
        // A refused connection, a timeout and a probe that blew up all mean the same thing here: this
        // address is not a server. One of them must not take the whole sweep down with it.
        var discovery = new LanServerDiscovery((address, _, _, _) =>
            address.ToString().EndsWith(".9")
                ? throw new InvalidOperationException("boom")
                : Task.FromResult(address.ToString().EndsWith(".2")));

        var found = await discovery.ScanHostsAsync([IPAddress.Parse("10.0.0.9"), IPAddress.Parse("10.0.0.2")]);

        Assert.Equal(["10.0.0.2:12000"], found.Select(endpoint => endpoint.ToString()).ToArray());
    }

    [Fact]
    public void CandidateHostsSweepTheSubnetButNotThisMachine() {
        var hosts = LanServerDiscovery.CandidateHosts(IPAddress.Parse("192.168.4.7"))
            .Select(host => host.ToString())
            .ToArray();

        Assert.Equal(253, hosts.Length);
        Assert.Contains("192.168.4.1", hosts);
        Assert.Contains("192.168.4.254", hosts);
        Assert.DoesNotContain("192.168.4.7", hosts);
    }

    [Fact]
    public void CandidateHostsIgnoreAddressesWithNoSubnetToSweep() {
        Assert.Empty(LanServerDiscovery.CandidateHosts(IPAddress.IPv6Loopback));
    }
}
