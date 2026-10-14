namespace ViewLight.Services;

/// <summary>
/// The launcher's own database: the RavenDB the server ships, started against a data directory that
/// belongs to the launcher rather than to the server.
/// </summary>
/// <remarks>
/// <para>
/// The launcher keeps its own notes about a server - the profiles, the playtime, the badges, and which
/// world each zone turned out to belong to. Those used to be a JSON file the launcher read whole, changed
/// in memory and wrote whole, which is a file that only ever grows and cannot answer a question without
/// being held entirely. They are documents here instead, and the database they live in is the one the
/// server itself uses: same binaries, same storage format, started the same way.
/// </para>
/// <para>
/// <b>Its data directory is the launcher's own.</b> The server's database lives inside the server folder,
/// because that is where Imlight keeps it and where Imlight will look for it. This one belongs to the
/// launcher, so it lives under the launcher's own root - <c>%LOCALAPPDATA%\ViewLight\database</c> for a
/// normal launcher, and <c>&lt;launcher folder&gt;\database</c> for a portable one, so a portable folder
/// carries this with it the way it carries the settings. Nothing else ever opens it, so unlike the server's
/// database there is no taking turns with Imlight: the launcher holds it for as long as it runs.
/// </para>
/// <para>
/// <b>Its port is its own too, and is not written down.</b> The server's database uses the port Imlight's
/// config names, because the server and the launcher both have to reach it on that address. Nothing outside
/// the launcher ever reaches this one, so a free port is picked each run instead - which also means a port
/// left occupied by somebody else's program cannot leave the launcher with a database it cannot start.
/// </para>
/// <para>
/// <b>What that costs.</b> The database is a framework-dependent application, so this is why the launcher
/// requires a machine .NET runtime at all - see <see cref="ImlightRequirements"/>. That requirement is
/// checked before this is ever started, so a machine that cannot run it is told why rather than left with
/// an empty profile screen.
/// </para>
/// </remarks>
public static class LauncherDatabase {

    /// <summary>The database's name inside the Raven instance the launcher runs.</summary>
    public const string DatabaseName = "LauncherData";

    /// <summary>
    /// Where this database's documents live: inside the launcher's own root, beside the settings file it
    /// is a bigger sibling of.
    /// </summary>
    /// <remarks>
    /// One directory, and everything of the launcher's is inside it, which is what a backup of the
    /// launcher's data has to mean: the directory plus the server's own, and nothing else.
    /// </remarks>
    public static string DataDirectoryFor(string dataRoot) => Path.Combine(dataRoot, "database");

    /// <summary>
    /// The settings file written for the launcher's own instance.
    /// </summary>
    /// <remarks>
    /// Its own file rather than the one the server's database is started with: both carry a port and a data
    /// directory, and the two processes must not be handed each other's.
    /// </remarks>
    public static string SettingsPathFor(string dataRoot)
        => Path.Combine(dataRoot, "raven", "launcher-settings.json");

    /// <summary>Where this instance writes its own log, kept apart from the server's database log.</summary>
    public static string LogsDirectoryFor(string dataRoot)
        => Path.Combine(dataRoot, "raven", "launcher-logs");

    /// <summary>
    /// Works out how to run the launcher's own database, or why it cannot be run.
    /// </summary>
    /// <remarks>
    /// The binaries come from the Imlight install, because they are the server's: there is no second copy of
    /// RavenDB to ship or to update, and an Imlight update replaces this database along with everything else
    /// it comes with. What is the launcher's own is the data directory, the settings file, the log, and the
    /// port - so an install is still what makes this possible, which is why it is a requirement rather than
    /// a nicety.
    /// </remarks>
    /// <returns>The setup to start, or <c>null</c> with the reason in <paramref name="problem"/>.</returns>
    public static EmbeddedRavenSetup? For(
        ImlightConfig config, string? serverPath, string dataRoot, out string? problem) {

        problem = null;

        if (string.IsNullOrWhiteSpace(serverPath)) {
            problem = "No server folder is set, so the database the launcher's own data lives in cannot be run.";
            return null;
        }

        var serverDirectory = config.ResolveRavenServerDirectory(serverPath);
        var assembly = ImlightConfig.RavenServerAssemblyFor(serverDirectory);

        if (!File.Exists(assembly)) {
            problem = $"The RavenDB the server ships is not in {serverDirectory}, so the launcher's own "
                      + "database cannot be run. Installing the server again replaces it.";
            return null;
        }

        var port = PortAllocator.FindFreePort();
        if (port is null) {
            problem = "No free port could be found for the launcher's own database.";
            return null;
        }

        // The same waiting time the server's database is given: it is the same engine doing the same work on
        // the same machine, and the setting Imlight exposes for one is the honest answer for the other.
        var timeout = TimeSpan.FromSeconds(Math.Clamp(config.EmbeddedDatabaseTimeoutSeconds, 10, 300));

        return new EmbeddedRavenSetup(
            assembly,
            serverDirectory,
            DataDirectoryFor(dataRoot),
            SettingsPathFor(dataRoot),
            LogsDirectoryFor(dataRoot),
            port.Value,
            timeout);
    }
}
