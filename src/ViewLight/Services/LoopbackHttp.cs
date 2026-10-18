namespace ViewLight.Services;

/// <summary>
/// The HTTP client the launcher talks to a database on <i>this machine</i> with.
/// </summary>
/// <remarks>
/// <para>
/// Everything the launcher reads and writes in a database answers on <c>127.0.0.1</c> and nowhere else - the
/// server's own player database, the launcher's notes, and the port probe that says whether one is up - so
/// no part of that conversation may depend on what the machine's network happens to be doing.
/// </para>
/// <para>
/// <b>The default client is not that client.</b> An <c>HttpClient</c> is built with <c>UseProxy = true</c>,
/// which means the system proxy - and everything that goes with it: a PAC file, a WPAD lookup, a proxy that
/// is only reachable over a connection this machine does not have - stands between the launcher and its own
/// database. On a machine with no network at all, that is a request that leaves the loopback address and
/// never comes back, which is how a database running beside the launcher can be unreadable. Here the proxy
/// is switched off: the request goes straight to the address, on a machine with or without a wifi card.
/// </para>
/// <para>
/// This is only for the databases. Requests that really do leave this machine - the game's art, a patch
/// server somebody else runs, GitHub - keep the system proxy, because there it is the difference between
/// working behind a proxy and not.
/// </para>
/// </remarks>
public static class LoopbackHttp {

    /// <summary>
    /// A client for a database on this machine, which ignores the system proxy entirely.
    /// </summary>
    /// <param name="timeout">
    /// How long a call may take. Short on purpose for the callers here: a database on this machine answers
    /// in milliseconds, and one that does not must not hold the screen while it is waited for.
    /// </param>
    public static HttpClient Client(TimeSpan timeout) {
        var client = new HttpClient(new SocketsHttpHandler { UseProxy = false }) {
            Timeout = timeout,
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd("ViewLight-Launcher/1.0");
        return client;
    }
}
