using System.Net;

namespace ViewLight.Services;

/// <summary>
/// What a <see cref="ServerConfigRepair"/> run changed, so the caller can tell the user what it did.
/// </summary>
/// <param name="EmbeddedDatabasePort">
/// The new embedded RavenDB port, or <c>null</c> when the configured one was left alone.
/// </param>
/// <param name="PatchServerUrl">
/// The patch server the config was pointed at, or <c>null</c> when it was left alone.
/// </param>
public sealed record ConfigRepair(int? EmbeddedDatabasePort, string? PatchServerUrl) {

    public static readonly ConfigRepair None = new(null, null);

    public bool ChangedAnything => EmbeddedDatabasePort is not null || PatchServerUrl is not null;
}

/// <summary>
/// Fixes the stock <c>Imlight.ini</c> values that make Imlight die during boot.
/// </summary>
/// <remarks>
/// Both repairs exist because Imlight ships settings that only work on the maintainer's machine, and
/// because it fails in ways that tell the user nothing:
/// <list type="bullet">
/// <item>
/// <b>Embedded RavenDB port.</b> Stock is 8080, one of the most contested ports on any PC. When it is
/// already taken RavenDB cannot bind and the server dies.
/// </item>
/// <item>
/// <b>Patch server.</b> Stock is a localhost address that only answers if the user started their own
/// patch server there. Without a reachable endpoint and without a cached copy, Imlight throws
/// <c>"Patch server is not reachable. Cannot load Root.wad."</c> from deep inside resource loading.
/// Nothing catches it, so the whole process is torn down by the .NET runtime and the user sees only an
/// exit code (0xE0434352).
/// </item>
/// </list>
/// A value the user set themselves is never overwritten, so a self-hosted patch server - even one that
/// happens to sit on the stock address - is left exactly as it is.
/// </remarks>
public static class ServerConfigRepair {

    /// <summary>Applies every repair, in one pass over the config.</summary>
    public static ConfigRepair Apply(string configPath) {
        if (!File.Exists(configPath)) {
            return ConfigRepair.None;
        }

        IniFile ini;
        try {
            ini = IniFile.Load(configPath);
        }
        catch (IOException) {
            return ConfigRepair.None;
        }

        var port = EnsureFreeEmbeddedDatabasePort(ini);
        var patchServer = EnsureUsablePatchServer(ini);

        if (port is null && patchServer is null) {
            return ConfigRepair.None;
        }

        try {
            ini.Save(configPath);
        }
        catch (IOException) {
            return ConfigRepair.None;
        }
        catch (UnauthorizedAccessException) {
            return ConfigRepair.None;
        }

        return new ConfigRepair(port, patchServer);
    }

    /// <summary>
    /// Makes sure the embedded RavenDB has a port it can actually bind, or <c>null</c> when the
    /// existing one was kept.
    /// </summary>
    public static int? EnsureFreeEmbeddedDatabasePort(IniFile ini) {
        var configured = int.TryParse(ini.Get(ImlightConfig.SectionDatabase, "EmbeddedDatabasePort"), out var parsed)
            ? parsed
            : ImlightConfig.DefaultEmbeddedDatabasePort;

        var isStockDefault = configured == ImlightConfig.DefaultEmbeddedDatabasePort;
        if (!isStockDefault && PortAllocator.IsAvailable(configured)) {
            return null;
        }

        var replacement = PortAllocator.FindFreePort();
        if (replacement is null) {
            return null;
        }

        ini.Set(ImlightConfig.SectionDatabase, "EmbeddedDatabasePort", replacement.Value.ToString());
        return replacement;
    }

    /// <summary>
    /// Points the config at a patch server that answers, or <c>null</c> when the configured one was
    /// kept. See <see cref="ServerConfigRepair"/> for why this decides whether the server boots at all.
    /// </summary>
    public static string? EnsureUsablePatchServer(IniFile ini) {
        var configured = ImlightConfig.NormalizePatchServerUrl(
            ini.Get(ImlightConfig.SectionPatch, "PatchServerInternalUrl"));

        if (!ShouldReplacePatchServer(configured, IsEndpointListening)) {
            return null;
        }

        ini.Set(ImlightConfig.SectionPatch, "PatchServerInternalUrl", ImlightConfig.PublicPatchServerUrl);
        return ImlightConfig.PublicPatchServerUrl;
    }

    /// <summary>
    /// Decides whether a configured patch server has to be replaced by
    /// <see cref="ImlightConfig.PublicPatchServerUrl"/>.
    /// </summary>
    /// <param name="configuredUrl">The value in the config, already normalized.</param>
    /// <param name="isEndpointListening">Reports whether a given URL currently answers.</param>
    /// <remarks>
    /// A patch server the user chose is never touched, even if it looks like the stock address: the
    /// stock address is exactly where a self-hosted patch server would run. Only a blank value, or the
    /// stock value with nothing behind it, is the broken default this repairs.
    /// </remarks>
    public static bool ShouldReplacePatchServer(string? configuredUrl, Func<string, bool> isEndpointListening) {
        var configured = ImlightConfig.NormalizePatchServerUrl(configuredUrl);

        if (!ImlightConfig.IsUnusablePatchServerUrl(configured)) {
            // The user pointed Imlight at their own patch server; that is their call.
            return false;
        }

        // The stock localhost address is a working patch server when one is genuinely running there,
        // e.g. an advanced user self-hosting Aurorium. Only a silent one is the broken default.
        return configured.Length == 0 || !isEndpointListening(configured);
    }

    /// <summary>Can something be connected to on this URL right now, on the loopback interface?</summary>
    public static bool IsEndpointListening(string url) {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !IsLoopbackHost(uri.Host)) {
            return false;
        }

        // The port being unavailable to bind is precisely what "something is already listening" means.
        return !PortAllocator.IsAvailable(uri.Port);
    }

    private static bool IsLoopbackHost(string host)
        => string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
           || (IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address));
}
