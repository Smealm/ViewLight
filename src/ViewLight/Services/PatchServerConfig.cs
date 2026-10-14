namespace ViewLight.Services;

/// <summary>
/// The patch server's own <c>config.toml</c>: the file that says where Aurorium listens and where it
/// keeps the assets it has fetched.
/// </summary>
/// <remarks>
/// <para>
/// Aurorium writes a default config with its defaults when it finds none, but every one of those
/// defaults is its author's rather than this machine's - and the launcher has an opinion about one of
/// them, the endpoint, because that is the address Imlight is then pointed at. So the launcher writes
/// the file itself, in the shape Aurorium documents.
/// </para>
/// <para>
/// The file is left alone while it already names the endpoint the launcher wants. That matters: a
/// config a user has grown by hand - a different fetch interval, a local patch host, a bigger download
/// queue - should not be flattened into this one just because the launcher was opened.
/// </para>
/// </remarks>
public static class PatchServerConfig {

    public const string FileName = "config.toml";

    /// <summary>
    /// The port Aurorium listens on by default, and the one Imlight's own stock config points at
    /// (<c>http://127.0.0.1:12369</c>), so a local patch server on this port is used by Imlight even
    /// before anything is written into its config.
    /// </summary>
    public const int DefaultPort = 12369;

    /// <summary>The interface the patch server is bound to. Loopback only: it is this machine's.</summary>
    public const string Host = "127.0.0.1";

    /// <summary>Aurorium's default upstream: Wizard101's own patch server.</summary>
    public const string PublicPatchHost = "patch.us.wizard101.com";

    public const int PublicPatchPort = 12500;

    /// <summary>
    /// How often Aurorium looks upstream for a new revision - and how long it waits before retrying what
    /// it could not download.
    /// </summary>
    /// <remarks>
    /// It ships eight hours, which is a service's cadence rather than a session's. That interval is not
    /// only "when to look for something new": a pass that ends with an asset it could not get - a big one
    /// that ran past its own two-minute request limit, a connection that dropped mid-file - waits the
    /// whole of it before that asset is tried again. Eight hours is what "the patch server is up and
    /// fetching nothing" looks like from the outside, and a first fetch that never finishes is exactly
    /// that. Five minutes keeps a patch server that is up making progress, and notices a genuinely new
    /// upstream revision on the same cadence.
    /// </remarks>
    public const int FetchIntervalSeconds = 300;

    /// <summary>
    /// How many assets Aurorium fetches at once. Two is its default; a revision's first fetch is
    /// thousands of files, so the launcher asks for more of them in parallel.
    /// </summary>
    public const int ConcurrentDownloads = 4;

    /// <summary>
    /// Whether Aurorium also writes what it does to <c>logs/</c> beside itself.
    /// </summary>
    /// <remarks>
    /// Off as it ships, which leaves a fetch that will not finish saying so only on the console - where a
    /// launcher that has been closed is not looking. On, a failed asset leaves a line naming the file.
    /// </remarks>
    public const bool FileLogging = true;

    /// <summary>Where fetched assets are kept, relative to the patch server's own folder.</summary>
    public const string DefaultSaveDirectory = "data";

    /// <summary>Where Aurorium's own database is kept, relative to its own folder.</summary>
    public const string DefaultDatabasePath = "aurorium.db";

    /// <summary>The URL Imlight is pointed at for a patch server running on <paramref name="port"/>.</summary>
    public static string EndpointUrl(int port) => $"http://{Host}:{port}";

    /// <summary>
    /// The game revision a patch server's revision name stands for, or <c>null</c> when the name is not
    /// one of its own.
    /// </summary>
    /// <remarks>
    /// Imlight builds the address it works with as <c>{PatchServerInternalUrl}/V_{GameRevision}</c> and
    /// Aurorium names every revision it fetches the same way - <c>V_</c> and the revision, taken verbatim
    /// from the upstream patch server's file-list URL - so this is what turns a name a patch server lists
    /// back into the value <c>GameRevision</c> takes.
    /// </remarks>
    public static string? GameRevisionFrom(string? revisionName) {
        var name = (revisionName ?? string.Empty).Trim();

        if (name.Length <= 2 || !name.StartsWith("V_", StringComparison.OrdinalIgnoreCase)) {
            return null;
        }

        var revision = name[2..].Trim();

        return revision.Length == 0 ? null : revision;
    }

    /// <summary>
    /// The newest game revision a patch server's revision names stand for, or <c>null</c> when it names
    /// none this understands.
    /// </summary>
    /// <remarks>
    /// Ordered by the revision number each name carries rather than by the order the names arrive in.
    /// Aurorium lists newest first, but a server that was handed a revision out of turn, or one that was
    /// written to by hand, would otherwise have its first entry read as its newest. A name with no number
    /// this can read is kept only when nothing better turns up, which leaves the list's own order as the
    /// fallback rather than as the rule.
    /// </remarks>
    public static string? NewestGameRevision(IReadOnlyList<string>? revisionNames) {
        string? newest = null;

        foreach (var name in revisionNames ?? []) {
            var revision = GameRevisionFrom(name);
            if (revision is null) {
                continue;
            }

            // An unreadable revision number is not a comparison, so the one already in hand stands.
            if (newest is null || ClientBuildReader.Compare(revision, newest) > 0) {
                newest = revision;
            }
        }

        return newest;
    }

    public static string PathFor(string patchServerFolder)
        => Path.Combine(patchServerFolder, FileName);

    /// <summary>
    /// The endpoint port a folder's config names, or <c>null</c> when there is no config to read or it
    /// does not say.
    /// </summary>
    public static int? TryReadPort(string patchServerFolder) {
        try {
            var path = PathFor(patchServerFolder);
            return File.Exists(path) ? ParsePort(File.ReadAllText(path)) : null;
        }
        catch (Exception) {
            // An unreadable config is simply one the launcher will write again.
            return null;
        }
    }

    /// <summary>
    /// Makes the folder's config name <paramref name="port"/>, and brings a config the launcher itself
    /// wrote up to the current template.
    /// </summary>
    /// <remarks>
    /// Two reasons to write, and they are deliberately narrow. A config that names another endpoint is
    /// written again, because the endpoint is the one thing the launcher has an opinion about - Imlight is
    /// pointed at it, and a patch server listening somewhere else is one nothing can reach. An endpoint
    /// that agrees is left alone, <b>unless</b> the file is an older copy of this template that nobody has
    /// touched: the template carries the fetch settings a first fetch needs to finish (see
    /// <see cref="FetchIntervalSeconds"/>), and a machine that was set up before those changed would keep
    /// the ones that do not. A config the user has grown by hand matches no template of ours, so it is
    /// never rewritten - the file on disk stops being ours the moment somebody edits it.
    /// </remarks>
    /// <returns>Whether the file was written.</returns>
    public static bool EnsureConfig(string patchServerFolder, int port) {
        if (TryReadPort(patchServerFolder) != port) {
            return TryWrite(patchServerFolder, port);
        }

        return IsALauncherTemplate(patchServerFolder, port, out var isCurrent) && !isCurrent
               && TryWrite(patchServerFolder, port);
    }

    /// <summary>
    /// Whether the folder's config is a template this launcher wrote and nothing has changed since, and
    /// whether it is the current one.
    /// </summary>
    /// <remarks>
    /// Compared whole rather than by a marker comment on purpose: the marker would say only that the
    /// launcher wrote the file once, while the comparison says nobody has edited it since. That is the
    /// difference between updating a file that is ours and flattening one that is not.
    /// </remarks>
    public static bool IsALauncherTemplate(string patchServerFolder, int port, out bool isCurrent) {
        isCurrent = false;

        string text;
        try {
            text = File.ReadAllText(PathFor(patchServerFolder));
        }
        catch (Exception) {
            // No config, or one that cannot be read: nothing to recognise, and TryWrite has its own say.
            return false;
        }

        if (Same(text, Build(port))) {
            isCurrent = true;
            return true;
        }

        return Same(text, LegacyBuild(port));
    }

    private static bool Same(string? left, string? right)
        => string.Equals(Normalise(left), Normalise(right), StringComparison.Ordinal);

    private static string? Normalise(string? text) => text?.Replace("\r\n", "\n").Trim();

    /// <summary>Writes a complete config for <paramref name="port"/>, replacing any existing one.</summary>
    public static bool TryWrite(string patchServerFolder, int port) {
        if (string.IsNullOrWhiteSpace(patchServerFolder)) {
            return false;
        }

        try {
            Directory.CreateDirectory(patchServerFolder);
            File.WriteAllText(PathFor(patchServerFolder), Build(port));
            return true;
        }
        catch (Exception) {
            // A folder that refuses the file still gets a patch server: Aurorium writes its own
            // defaults, and the log says what happened rather than the start being stopped.
            return false;
        }
    }

    /// <summary>
    /// The config as Aurorium expects it, with the launcher's endpoint and Aurorium's own settings
    /// everywhere the launcher has no reason to differ.
    /// </summary>
    public static string Build(int port) =>
        $"""
        # Written by ViewLight. Aurorium reads this file from its own folder.
        #
        # fetch_interval is how often it looks upstream and how long it waits before retrying an asset it
        # could not download: the eight hours it ships mean one file that failed - a large one is easy to
        # lose inside Aurorium's own two-minute limit - holds the whole revision back for most of a day.
        [server]
        endpoint = "{Host}:{port}"

        [fetcher]
        concurrent_downloads = {ConcurrentDownloads}
        save_directory = "{DefaultSaveDirectory}"
        fetch_interval = {FetchIntervalSeconds}

        [patch]
        host = "{PublicPatchHost}"
        port = "{PublicPatchPort}"

        [database]
        path = "{DefaultDatabasePath}"

        [debug]
        level = "info"
        file_logging = {(FileLogging ? "true" : "false")}
        """;

    /// <summary>
    /// The first config this launcher wrote, kept so a machine set up back then can be recognised as
    /// having one of ours - and updated - rather than mistaken for a config somebody tuned by hand.
    /// </summary>
    public static string LegacyBuild(int port) =>
        $"""
        # Written by ViewLight. Aurorium reads this file from its own folder.
        [server]
        endpoint = "{Host}:{port}"

        [fetcher]
        concurrent_downloads = 2
        save_directory = "{DefaultSaveDirectory}"
        fetch_interval = 28800

        [patch]
        host = "{PublicPatchHost}"
        port = "{PublicPatchPort}"

        [database]
        path = "{DefaultDatabasePath}"

        [debug]
        level = "info"
        file_logging = false
        """;

    /// <summary>
    /// The folder a patch server keeps its revisions in, as its config says, or <c>null</c> when the
    /// config does not name one.
    /// </summary>
    /// <remarks>
    /// Read rather than assumed, for the same reason the port is: anything that writes into a patch
    /// server's revision folder has to write where that patch server will look, and a config that moved
    /// its data folder is a config whose revisions are somewhere else.
    /// </remarks>
    public static string? TryReadSaveDirectory(string patchServerFolder) {
        try {
            var path = PathFor(patchServerFolder);
            return File.Exists(path) ? ParseValue(File.ReadAllText(path), "[fetcher]", "save_directory") : null;
        }
        catch (Exception) {
            return null;
        }
    }

    /// <summary>
    /// Reads the endpoint port out of a config. Deliberately a line scan rather than TOML parser: the
    /// launcher only ever needs the one value, and a config it cannot fully parse must still yield it.
    /// </summary>
    public static int? ParsePort(string? toml) {
        if (string.IsNullOrWhiteSpace(toml)) {
            return null;
        }

        var inServerSection = false;

        foreach (var rawLine in toml.Split('\n')) {
            var line = rawLine.Trim();

            if (line.StartsWith('[')) {
                inServerSection = line.Equals("[server]", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inServerSection || !line.StartsWith("endpoint", StringComparison.OrdinalIgnoreCase)) {
                continue;
            }

            var equals = line.IndexOf('=');
            if (equals < 0) {
                continue;
            }

            var value = line[(equals + 1)..].Trim().Trim('"');
            var colon = value.LastIndexOf(':');
            if (colon >= 0 && int.TryParse(value[(colon + 1)..].Trim(), out var port)
                           && port is > 0 and <= 65535) {
                return port;
            }
        }

        return null;
    }

    /// <summary>
    /// One value out of one section of a config, or <c>null</c> when the file does not spell it out.
    /// </summary>
    private static string? ParseValue(string? toml, string section, string key) {
        if (string.IsNullOrWhiteSpace(toml)) {
            return null;
        }

        var inSection = false;

        foreach (var rawLine in toml.Split('\n')) {
            var line = rawLine.Trim();

            if (line.StartsWith('[')) {
                inSection = line.Equals(section, StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inSection || !line.StartsWith(key, StringComparison.OrdinalIgnoreCase)) {
                continue;
            }

            var equals = line.IndexOf('=');
            if (equals < 0) {
                continue;
            }

            // A key that merely starts with the one being read - save_directory_something - is not it.
            var name = line[..equals].Trim();
            if (!name.Equals(key, StringComparison.OrdinalIgnoreCase)) {
                continue;
            }

            var value = line[(equals + 1)..].Trim().Trim('"').Trim();
            if (value.Length > 0) {
                return value;
            }
        }

        return null;
    }
}
