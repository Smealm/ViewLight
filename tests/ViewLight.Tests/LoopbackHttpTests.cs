using System.Net;
using System.Net.Sockets;
using System.Text;
using ViewLight.Services;
using Xunit;

namespace ViewLight.Tests;

/// <summary>
/// The launcher's databases are on this machine, so nothing about reaching one may depend on what the
/// machine's network is doing.
/// </summary>
/// <remarks>
/// The address a database is read at, the client it is read with, and what a database that could not start
/// said along the way - the three things that decide whether a launcher with no wifi card and no internet
/// connection can still read the processes running beside it.
/// </remarks>
public class LoopbackHttpTests {

    [Fact]
    public void ADatabaseIsReadAtThisMachinesOwnAddress() {
        var address = PlayerDatabaseClient.BaseUrl(28850);

        Assert.Equal("http://127.0.0.1:28850", address);

        // The rule behind it: the database is this machine's own, so the address is loopback by name. An
        // address that had to be looked up - or one on the machine's network rather than on itself - is one
        // more thing that could fail while everything the read needs is already here.
        Assert.True(Uri.TryCreate(address, UriKind.Absolute, out var uri));
        Assert.True(uri.IsLoopback);
        Assert.Equal("127.0.0.1", uri.Host);
    }

    [Fact]
    public async Task ADatabaseIsReadWithTheSystemsProxySwitchedOff() {
        using var server = new OneRequestServer("ok");

        var previous = HttpClient.DefaultProxy;
        var proxy = new RecordingProxy();
        HttpClient.DefaultProxy = proxy;

        try {
            // The client the databases are read with. A request that went through the system proxy would ask
            // this one first, and it hands out an address nothing is listening on.
            using var http = LoopbackHttp.Client(TimeSpan.FromSeconds(5));

            Assert.Equal("ok", await http.GetStringAsync(server.Url));
            Assert.False(proxy.WasAsked);
        }
        finally {
            HttpClient.DefaultProxy = previous;
        }
    }

    [Fact]
    public async Task TheProfilesAreReadWithTheSystemsProxySwitchedOffToo() {
        // The launcher's own database is where a profile lives, and it is read through the same address and
        // the same kind of client as the player database. The bug this pins down is the one that was reported:
        // with a system proxy configured and no connection behind it, reading a profile went through the proxy
        // rather than to the process beside the launcher. Nothing is injected here on purpose - the client the
        // real code builds is the one under test.
        using var server = new OneRequestServer(
            "{\"Results\":[{\"@metadata\":{\"@collection\":\"Profiles\"},\"AccountId\":7}]}",
            "application/json");

        var previous = HttpClient.DefaultProxy;
        var proxy = new RecordingProxy();
        HttpClient.DefaultProxy = proxy;

        try {
            var port = server.Url.Port;
            using var client = new LauncherDatabaseClient(port);

            var documents = await client.ReadDocumentsAsync();

            Assert.NotNull(documents);
            Assert.Single(documents);
            Assert.Null(client.Problem);
            Assert.False(proxy.WasAsked);
        }
        finally {
            HttpClient.DefaultProxy = previous;
        }
    }

    [Fact]
    public void WhatADatabaseSaysIsKeptToALineCountThatCannotGrow() {
        var tail = new OutputTail(3);

        tail.Add(null);
        tail.Add("   ");

        tail.Add("first");
        tail.Add("second");
        tail.Add("third");
        tail.Add("fourth");

        // Blank lines are the console's spacing, and the oldest of the kept lines is what falls off: the tail
        // is the part about how the process ended rather than the banner it printed on the way in.
        Assert.Equal(["second", "third", "fourth"], tail.Lines);
        Assert.False(tail.IsEmpty);

        tail.Clear();
        Assert.True(tail.IsEmpty);
        Assert.Empty(tail.Lines);

        // The default is room for a stack trace and still something a person can paste into a report.
        Assert.InRange(OutputTail.Capacity, 20, 200);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// A stand-in for the system proxy, which records being asked and offers an address nothing listens on.
    /// </summary>
    /// <remarks>
    /// An <c>HttpClient</c> is built with the proxy it finds in <see cref="HttpClient.DefaultProxy"/>, so a
    /// request that has been switched off the proxy never touches this - and a request that has not cannot
    /// reach anything, which is what makes the difference observable rather than assumed.
    /// </remarks>
    private sealed class RecordingProxy : IWebProxy {

        public bool WasAsked { get; private set; }

        public ICredentials? Credentials { get; set; }

        public Uri? GetProxy(Uri destination) {
            WasAsked = true;
            return new Uri("http://127.0.0.1:1");
        }

        public Uri? GetProxy(Uri destination, Uri? bypassUri) {
            WasAsked = true;
            return new Uri("http://127.0.0.1:1");
        }

        public bool IsBypassed(Uri? uri) {
            WasAsked = true;
            return false;
        }
    }

    /// <summary>A listener on this machine's own loopback that answers one request with a short body.</summary>
    private sealed class OneRequestServer : IDisposable {

        private readonly TcpListener _listener;

        public Uri Url { get; }

        public OneRequestServer(string body, string contentType = "text/plain") {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();

            var port = ((IPEndPoint) _listener.LocalEndpoint).Port;
            Url = new Uri($"http://127.0.0.1:{port}/");

            _ = ServeAsync(body, contentType);
        }

        private async Task ServeAsync(string body, string contentType) {
            try {
                using var connection = await _listener.AcceptTcpClientAsync();
                await using var stream = connection.GetStream();

                var buffer = new byte[8192];
                _ = await stream.ReadAsync(buffer);

                var payload = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 200 OK\r\nContent-Type: {contentType}\r\nContent-Length: "
                    + Encoding.ASCII.GetByteCount(body)
                    + "\r\nConnection: close\r\n\r\n"
                    + body);

                await stream.WriteAsync(payload);
            }
            catch (Exception) {
                // A connection that went away is the same as one that never arrived, to a test that has
                // already finished.
            }
        }

        public void Dispose() => _listener.Stop();
    }
}
