using ViewLight.Models;
using Xunit;

namespace ViewLight.Tests;

/// <summary>
/// The address a player types is the only thing standing between them and joining the right server, so
/// what is accepted and what is refused is pinned down here.
/// </summary>
public class ServerEndpointTests {

    [Theory]
    [InlineData("192.168.1.20", 12000, "192.168.1.20", 12000)]
    [InlineData("192.168.1.20:13001", 12000, "192.168.1.20", 13001)]
    [InlineData("10.0.0.5", 13001, "10.0.0.5", 13001)]
    [InlineData("  host.example.com  ", 12000, "host.example.com", 12000)]
    [InlineData("login.us.wizard101.com:12000", 9999, "login.us.wizard101.com", 12000)]
    public void ReadsAnAddressTheWayPeopleWriteThem(string text, int defaultPort, string host, int port) {
        var endpoint = ServerEndpoint.Parse(text, defaultPort);

        Assert.NotNull(endpoint);
        Assert.Equal(host, endpoint!.Host);
        Assert.Equal(port, endpoint.Port);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(":12000")]
    [InlineData("host:")]
    [InlineData("host:not-a-port")]
    [InlineData("host:0")]
    [InlineData("host:70000")]
    public void RefusesWhatIsNotAnAddress(string? text)
        => Assert.Null(ServerEndpoint.Parse(text));

    [Fact]
    public void WritesItselfAsHostColonPort() {
        Assert.Equal("10.0.0.5:12000", new ServerEndpoint("10.0.0.5", 12000).ToString());
    }

    [Fact]
    public void PointsAtWizard101sOwnServerOnTheStandardPort() {
        Assert.Equal("login.us.wizard101.com:12000", OfficialServer.Endpoint.ToString());
    }
}
