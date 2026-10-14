using System.Net;
using System.Net.Sockets;
using ViewLight.Services;
using Xunit;

namespace ViewLight.Tests;

public class PortAllocatorTests {

    [Fact]
    public void IsAvailable_IsFalseWhileSomethingElseHoldsThePort() {
        using var holder = new TcpListener(IPAddress.Loopback, 0);
        holder.Start();
        var busyPort = ((IPEndPoint)holder.LocalEndpoint).Port;

        Assert.False(PortAllocator.IsAvailable(busyPort));
    }

    [Fact]
    public void IsAvailable_IsTrueAfterThePortIsReleased() {
        int freedPort;
        using (var holder = new TcpListener(IPAddress.Loopback, 0)) {
            holder.Start();
            freedPort = ((IPEndPoint)holder.LocalEndpoint).Port;
        }

        Assert.True(PortAllocator.IsAvailable(freedPort));
    }

    [Fact]
    public void IsAvailable_RejectsNonsensePorts() {
        Assert.False(PortAllocator.IsAvailable(0));
        Assert.False(PortAllocator.IsAvailable(-1));
        Assert.False(PortAllocator.IsAvailable(70000));
    }

    [Fact]
    public void FindFreePort_ReturnsAPortInsideTheChosenRangeThatIsActuallyFree() {
        var port = PortAllocator.FindFreePort();

        Assert.NotNull(port);
        Assert.InRange(port!.Value, PortAllocator.MinimumPort, PortAllocator.MaximumPort);
        Assert.True(PortAllocator.IsAvailable(port.Value));
    }

    [Fact]
    public void FindFreePort_StaysBelowTheWindowsEphemeralRange() {
        // Above 49152 Windows hands ports out to outbound connections, so picking there would race.
        Assert.True(PortAllocator.MaximumPort < 49152);
    }

    [Fact]
    public void TheChosenRangeExcludesTheStockRavenDbPort() {
        // 8080 is exactly the value this exists to get away from, so it must never be picked.
        Assert.False(PortAllocator.IsAvailable(ImlightConfig.DefaultEmbeddedDatabasePort)
                     && ImlightConfig.DefaultEmbeddedDatabasePort >= PortAllocator.MinimumPort
                     && ImlightConfig.DefaultEmbeddedDatabasePort <= PortAllocator.MaximumPort);

        Assert.True(ImlightConfig.DefaultEmbeddedDatabasePort < PortAllocator.MinimumPort);
    }
}
