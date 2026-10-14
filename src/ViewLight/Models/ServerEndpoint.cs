using System.Globalization;

namespace ViewLight.Models;

/// <summary>
/// Which server Play points the game at.
/// </summary>
/// <remarks>
/// Only <see cref="Local"/> starts a server: the other two hand the client an address and let it go,
/// which is the whole difference between running your own server and visiting someone else's.
/// </remarks>
public enum PlayServerKind {

    /// <summary>The Imlight server this launcher installs, starts and signs in to.</summary>
    Local,

    /// <summary>An Imlight server somebody else runs, reached by address.</summary>
    Remote,

    /// <summary>Wizard101's own login server.</summary>
    Official,
}

/// <summary>
/// A login server the game client can be pointed at with <c>-L {Host} {Port}</c>.
/// </summary>
public sealed record ServerEndpoint(string Host, int Port) {

    /// <summary>
    /// The port a private Imlight server listens on unless its config says otherwise. Wizard101's own
    /// login server uses it too, which is what makes a plain "something is listening on 12000" a decent
    /// guess that a server is there.
    /// </summary>
    public const int DefaultPort = 12000;

    public override string ToString() => $"{Host}:{Port.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>
    /// Reads an address the user typed. <c>host</c> alone takes <paramref name="defaultPort"/>; a colon
    /// splits off the port, which is why the <b>last</b> colon is the one that counts.
    /// </summary>
    /// <returns>The address, or <c>null</c> when the text is not one.</returns>
    public static ServerEndpoint? Parse(string? text, int defaultPort = DefaultPort) {
        if (string.IsNullOrWhiteSpace(text)) {
            return null;
        }

        var trimmed = text.Trim();
        var host = trimmed;
        var port = defaultPort;

        var colon = trimmed.LastIndexOf(':');
        if (colon >= 0) {
            host = trimmed[..colon].Trim();
            if (!int.TryParse(trimmed[(colon + 1)..].Trim(), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out port)) {
                return null;
            }
        }

        if (host.Length == 0 || port is < 1 or > 65535) {
            return null;
        }

        return new ServerEndpoint(host, port);
    }
}

/// <summary>
/// Wizard101's own login server, which an Imlight launcher has no business starting or signing in to.
/// </summary>
public static class OfficialServer {

    public const string Host = "login.us.wizard101.com";

    public const int Port = ServerEndpoint.DefaultPort;

    public static ServerEndpoint Endpoint => new(Host, Port);
}
