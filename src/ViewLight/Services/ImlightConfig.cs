namespace ViewLight.Services;

/// <summary>
/// Typed, validated access to the server's <c>Config/Imlight.ini</c>.
/// </summary>
/// <remarks>
/// The INI file stays the single source of truth - the launcher never keeps a shadow copy of these
/// values, it reads them from disk and writes them straight back. That is what makes "update the
/// server but keep my settings" a trivial merge instead of a re-sync of two competing stores.
/// <para>
/// Sections and keys below are taken from the stock config shipped in Imlight release
/// <c>r806919.Wizard_1_610</c>; see <see cref="IniFile"/> for why unknown keys are preserved rather
/// than dropped.
/// </para>
/// </remarks>
public sealed class ImlightConfig {

    public const string RelativeConfigPath = "Config/Imlight.ini";
    public const string ServerExecutableName = "Imlight.Director.exe";

    public const string SectionGlobal = "Global Settings";
    public const string SectionLogin = "Login Server";
    public const string SectionGame = "Game Server";
    public const string SectionCharacter = "Character";
    public const string SectionDatabase = "Database";
    public const string SectionPatch = "Patch Server";
    public const string SectionLogging = "Logging";

    private const string DefaultLogPath = "./logs/log.txt";

    public IniFile Ini { get; }

    private ImlightConfig(IniFile ini) => Ini = ini;

    public static string ConfigPathFor(string serverPath)
        => Path.Combine(serverPath, "Config", "Imlight.ini");

    public static string ExecutablePathFor(string serverPath)
        => Path.Combine(serverPath, ServerExecutableName);

    /// <summary>Loads the server config, or returns <c>null</c> when the server is not installed yet.</summary>
    public static ImlightConfig? TryLoad(string serverPath) {
        if (string.IsNullOrWhiteSpace(serverPath)) {
            return null;
        }

        var path = ConfigPathFor(serverPath);
        if (!File.Exists(path)) {
            return null;
        }

        try {
            return new ImlightConfig(IniFile.Load(path));
        }
        catch (IOException) {
            return null;
        }
    }

    public void Save(string serverPath) => Ini.Save(ConfigPathFor(serverPath));

    // ---------------------------------------------------------------- identity

    public string AdminUsername {
        get => Ini.GetOrDefault(SectionDatabase, "AdminAccountUsername", "admin");
        set => Ini.Set(SectionDatabase, "AdminAccountUsername", value);
    }

    public string AdminPassword {
        get => Ini.GetOrDefault(SectionDatabase, "AdminAccountPassword", string.Empty);
        set => Ini.Set(SectionDatabase, "AdminAccountPassword", value);
    }

    public string GameRevision {
        get => Ini.GetOrDefault(SectionGlobal, "GameRevision", string.Empty);
        set => Ini.Set(SectionGlobal, "GameRevision", value);
    }

    public bool EnforceRevision {
        get => GetBool(SectionGlobal, "EnforceRevision", false);
        set => Ini.Set(SectionGlobal, "EnforceRevision", BoolText(value));
    }

    // ---------------------------------------------------------------- servers

    public int LoginServerPort {
        get => GetInt(SectionLogin, "LoginServerPort", 12000);
        set => Ini.Set(SectionLogin, "LoginServerPort", value.ToString());
    }

    public string GameServerIp {
        get => Ini.GetOrDefault(SectionGame, "GameServerIP", "127.0.0.1");
        set => Ini.Set(SectionGame, "GameServerIP", value);
    }

    public int GameServerPort {
        get => GetInt(SectionGame, "GameServerPort", 12333);
        set => Ini.Set(SectionGame, "GameServerPort", value.ToString());
    }

    public int GameServerCount {
        get => GetInt(SectionGame, "GameServerCount", 3);
        set => Ini.Set(SectionGame, "GameServerCount", value.ToString());
    }

    public string RealmNames {
        get => Ini.GetOrDefault(SectionGame, "RealmNames", "Centaur,Pegasus,Phoenix");
        set => Ini.Set(SectionGame, "RealmNames", value);
    }

    // ---------------------------------------------------------------- characters

    /// <summary>
    /// When true, new characters skip the tutorial and are dropped straight into
    /// <see cref="StartingZone"/> - which is also when <see cref="DefaultItems"/> is granted.
    /// </summary>
    public bool TutorialDisabled {
        get => GetBool(SectionCharacter, "TutorialDisabled", false);
        set => Ini.Set(SectionCharacter, "TutorialDisabled", BoolText(value));
    }

    public string DefaultItems {
        get => Ini.GetOrDefault(SectionCharacter, "DefaultItems", string.Empty);
        set => Ini.Set(SectionCharacter, "DefaultItems", value);
    }

    /// <summary>The starter kit as template IDs, ignoring blanks and duplicates.</summary>
    public IReadOnlyList<uint> DefaultItemsIds => ItemList.Parse(DefaultItems);

    /// <summary>RavenDB batches at 16, so the starter kit cannot exceed it.</summary>
    public const int MaxDefaultItems = 16;

    /// <summary>
    /// The client does not tolerate a starter kit below two items - granting fewer bricks the
    /// character - so the launcher refuses to write one.
    /// </summary>
    public const int MinDefaultItems = 2;

    /// <summary>The exact wording the GUI shows when the kit is too small.</summary>
    public const string TooFewStarterItemsMessage = "Can't be less than two.";

    /// <summary>
    /// Checks the size of a starter kit before it is written to disk. Returns <c>null</c> when the kit
    /// is safe, or a message explaining why it is not.
    /// </summary>
    public static string? ValidateStarterKit(int itemCount) {
        if (itemCount < MinDefaultItems) {
            return TooFewStarterItemsMessage;
        }

        if (itemCount > MaxDefaultItems) {
            return $"Imlight cannot grant more than {MaxDefaultItems} items: RavenDB has a batch limit of {MaxDefaultItems}.";
        }

        return null;
    }

    public int StartingLevel {
        get => GetInt(SectionCharacter, "StartingLevel", 1);
        set => Ini.Set(SectionCharacter, "StartingLevel", value.ToString());
    }

    /// <summary>
    /// The highest level a wizard can reach on this server, from <c>Character.MaxLevel</c>.
    /// </summary>
    /// <remarks>
    /// Read rather than assumed because the launcher's level milestones are generated up to it: the stock
    /// cap is 200, and a server that lowers or raises it has a shorter or longer ladder for every school.
    /// </remarks>
    public int MaxLevel {
        get => GetInt(SectionCharacter, "MaxLevel", AchievementCatalog.DefaultMaxLevel);
        set => Ini.Set(SectionCharacter, "MaxLevel", value.ToString());
    }

    public string StartingZone {
        get => Ini.GetOrDefault(SectionCharacter, "StartingZone", string.Empty);
        set => Ini.Set(SectionCharacter, "StartingZone", value);
    }

    public int BaseGoldPouch {
        get => GetInt(SectionCharacter, "BaseGoldPouch", 1000000);
        set => Ini.Set(SectionCharacter, "BaseGoldPouch", value.ToString());
    }

    // ---------------------------------------------------------------- persistence

    public bool SpiralDbAutoFetch {
        get => GetBool(SectionDatabase, "SpiralDBAutoFetch", true);
        set => Ini.Set(SectionDatabase, "SpiralDBAutoFetch", BoolText(value));
    }

    public string EmbeddedDatabaseDataDirectory {
        get => Ini.GetOrDefault(SectionDatabase, "EmbeddedDatabaseDataDirectory", "../ImlightEmbeddedDatabase/");
        set => Ini.Set(SectionDatabase, "EmbeddedDatabaseDataDirectory", value);
    }

    /// <summary>The port the embedded RavenDB listens on.</summary>
    public const int DefaultEmbeddedDatabasePort = 8080;

    /// <summary>
    /// Port for the embedded RavenDB. The launcher replaces the stock <see cref="DefaultEmbeddedDatabasePort"/>
    /// with a random free port on install, because 8080 is a very popular port and Imlight will refuse to
    /// boot if something else already holds it.
    /// </summary>
    public int EmbeddedDatabasePort {
        get => GetInt(SectionDatabase, "EmbeddedDatabasePort", DefaultEmbeddedDatabasePort);
        set => Ini.Set(SectionDatabase, "EmbeddedDatabasePort", value.ToString());
    }

    /// <summary>How long Imlight lets the database take to come up before its own boot gives up on it.</summary>
    public int EmbeddedDatabaseTimeoutSeconds {
        get => GetInt(SectionDatabase, "EmbeddedDatabaseTimeoutTime", 90);
        set => Ini.Set(SectionDatabase, "EmbeddedDatabaseTimeoutTime", value.ToString());
    }

    /// <summary>Whether Imlight runs a full RavenDB install instead of the one it ships with.</summary>
    public bool EmbeddedDatabaseUsesFullServer {
        get => GetBool(SectionDatabase, "EmbeddedDatabaseUseFull", false);
        set => Ini.Set(SectionDatabase, "EmbeddedDatabaseUseFull", BoolText(value));
    }

    /// <summary>Where that full install lives. Only read when the switch above is on.</summary>
    public string EmbeddedDatabaseFullPath {
        get => Ini.GetOrDefault(SectionDatabase, "EmbeddedDatabaseFullPath", "../RavenDB/Server/");
        set => Ini.Set(SectionDatabase, "EmbeddedDatabaseFullPath", value);
    }

    /// <summary>
    /// The folder RavenDB keeps its documents in, as an absolute path.
    /// </summary>
    /// <remarks>
    /// Imlight hands <see cref="EmbeddedDatabaseDataDirectory"/> to Raven's embedded server unchanged, and
    /// that server resolves it against <b>its own</b> directory - see
    /// <see cref="RavenServerDirectory"/> - which is why the stock <c>../ImlightEmbeddedDatabase/</c> ends up
    /// beside the server's folder rather than above it. Resolving it the same way matters more than it
    /// looks: a path read differently points Raven at an empty folder and quietly makes a second, empty
    /// world next to the real one, with the accounts still in the first.
    /// </remarks>
    public string ResolveEmbeddedDatabaseDirectory(string serverPath)
        => ResolveAgainstRavenServerDirectory(serverPath, EmbeddedDatabaseDataDirectory);

    /// <summary>Where the RavenDB that owns the data directory lives.</summary>
    /// <remarks>
    /// The one Imlight ships unless the config switches to a full install, in which case that is what is
    /// started - and it is a different build with its own data, so which one is in use decides everything.
    /// </remarks>
    public string ResolveRavenServerDirectory(string serverPath)
        => EmbeddedDatabaseUsesFullServer
            ? ResolveAgainstRavenServerDirectory(serverPath, EmbeddedDatabaseFullPath)
            : RavenServerDirectory(serverPath);

    /// <summary>The folder Imlight's own embeddable RavenDB ships in, inside the server folder.</summary>
    public static string RavenServerDirectory(string serverPath)
        => Path.Combine(serverPath, RelativeRavenServerDirectory);

    public const string RelativeRavenServerDirectory = "RavenDBServer";

    /// <summary>The assembly the database server itself runs from.</summary>
    public static string RavenServerAssemblyFor(string ravenServerDirectory)
        => Path.Combine(ravenServerDirectory, "Raven.Server.dll");

    /// <summary>
    /// Resolves a Raven path the way the embedded server does: against the directory the database server
    /// runs from, not against the server folder.
    /// </summary>
    public static string ResolveAgainstRavenServerDirectory(string serverPath, string configured)
        => string.IsNullOrWhiteSpace(configured)
            ? RavenServerDirectory(serverPath)
            : Path.GetFullPath(Path.Combine(RavenServerDirectory(serverPath), configured));

    // ---------------------------------------------------------------- patch server

    /// <summary>
    /// The patch server the Revive101 team runs for everyone. Imlight loads <c>Root.wad</c> from a
    /// patch server, so a config that points at nothing cannot boot (see
    /// <see cref="ServerConfigRepair"/>).
    /// </summary>
    public const string PublicPatchServerUrl = "https://patcher.phill030.de";

    /// <summary>
    /// The value Imlight's own <c>Imlight.ini</c> ships with. Nothing listens there on a normal
    /// machine - it is only a real patch server when the user started their own on that address.
    /// </summary>
    public const string StockPatchServerUrl = "http://127.0.0.1:12369";

    /// <summary>
    /// The patch server Imlight downloads <c>Root.wad</c>, zone wads and the patch file list from.
    /// </summary>
    public string PatchServerUrl {
        // Normalized on the way out as well as in: Imlight appends "/V_{revision}" to whatever is
        // stored, so a trailing slash in a hand-edited config would be sent to the patch server.
        get => NormalizePatchServerUrl(Ini.GetOrDefault(SectionPatch, "PatchServerInternalUrl", string.Empty));
        set => Ini.Set(SectionPatch, "PatchServerInternalUrl", NormalizePatchServerUrl(value));
    }

    /// <summary>Strips the trailing slash Imlight's own URL building would double up.</summary>
    public static string NormalizePatchServerUrl(string? url)
        => (url ?? string.Empty).Trim().TrimEnd('/');

    /// <summary>
    /// True when the configured patch server is blank or is the stock localhost address. Imlight
    /// throws while loading <c>Root.wad</c> in that state, which kills the process with no usable
    /// message, so the launcher replaces it with <see cref="PublicPatchServerUrl"/>.
    /// </summary>
    public static bool IsUnusablePatchServerUrl(string? url) {
        var normalized = NormalizePatchServerUrl(url);
        return normalized.Length == 0
               || string.Equals(normalized, StockPatchServerUrl, StringComparison.OrdinalIgnoreCase);
    }

    public string LogLevel {
        get => Ini.GetOrDefault(SectionLogging, "LogLevel", "DEBUG");
        set => Ini.Set(SectionLogging, "LogLevel", value);
    }

    public string LogPath => Ini.GetOrDefault(SectionLogging, "LogPath", DefaultLogPath);

    /// <summary>
    /// Resolves <see cref="LogPath"/> (which the stock config writes relative to the server folder)
    /// into the absolute path of the file the server is actually writing, so the launcher can watch it
    /// while the server warms up.
    /// </summary>
    public string ResolveLogFilePath(string serverPath) {
        var configured = LogPath.Replace('/', Path.DirectorySeparatorChar).Trim();
        var absolute = Path.IsPathRooted(configured)
            ? configured
            : Path.GetFullPath(Path.Combine(serverPath, configured));

        if (File.Exists(absolute)) {
            return absolute;
        }

        // Imlight logs through Serilog with a daily rolling interval, so the name in the config is not
        // the name on disk: `./logs/log.txt` is written as `./logs/log20261013.txt`. Watching the
        // configured name means never seeing the server's readiness line, and never being able to quote
        // its `[ERR]` lines back when it crashes.
        var directory = Path.GetDirectoryName(absolute);
        var baseName = Path.GetFileNameWithoutExtension(absolute);
        var extension = Path.GetExtension(absolute);

        var rolled = RolledLogs(directory, baseName, extension);
        if (rolled.Count == 0) {
            return absolute;
        }

        var today = Path.Combine(directory!, $"{baseName}{DateTime.Now:yyyyMMdd}{extension}");
        if (File.Exists(today)) {
            return today;
        }

        // With daily files on disk, today's is where this run's output goes even though it does not
        // exist yet. Falling back to yesterday's would lose the readiness line, and the crash's error
        // lines, on the first run of every day.
        return IsDailyLogName(rolled[0].Name, baseName, extension) ? today : rolled[0].FullName;
    }

    /// <summary>
    /// The logs beside <paramref name="configuredPath"/> that could be rolled versions of it, newest
    /// first. The configured name itself is not one of them.
    /// </summary>
    private static IReadOnlyList<FileInfo> RolledLogs(string? directory, string baseName, string extension) {
        if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(baseName) || !Directory.Exists(directory)) {
            return [];
        }

        var minimumNameLength = baseName.Length + extension.Length;

        try {
            return Directory.EnumerateFiles(directory, $"{baseName}*{extension}")
                .Select(path => new FileInfo(path))
                .Where(file => file.Name.Length > minimumNameLength)
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ToList();
        }
        catch (Exception) {
            // An unreadable log folder is not worth failing a server start over.
            return [];
        }
    }

    /// <summary>
    /// Is this the daily name Serilog would give <c>log.txt</c> - <c>log20261013.txt</c>? The eight
    /// digits are only how the daily interval spells a date; a config using another interval produces
    /// other names, and those are taken as they are.
    /// </summary>
    private static bool IsDailyLogName(string fileName, string baseName, string extension) {
        var middle = fileName[baseName.Length..^extension.Length];
        return middle.Length == 8 && middle.All(char.IsAsciiDigit);
    }

    // ---------------------------------------------------------------- helpers

    private int GetInt(string section, string key, int fallback)
        => int.TryParse(Ini.Get(section, key), out var value) ? value : fallback;

    private bool GetBool(string section, string key, bool fallback)
        => Ini.Get(section, key)?.Trim().ToLowerInvariant() switch {
            "true" or "1" or "yes" => true,
            "false" or "0" or "no" => false,
            _ => fallback,
        };

    /// <summary>Imlight parses these with <c>AsBool()</c>; keep the spelling it ships with.</summary>
    private static string BoolText(bool value) => value ? "true" : "false";

    /// <summary>
    /// The launch argument the client needs in order to reach this server:
    /// <c>-L {GameServerIP} {LoginServerPort}</c>.
    /// </summary>
    public string BuildClientLaunchArguments()
        => $"-L {GameServerIp} {LoginServerPort}";
}
