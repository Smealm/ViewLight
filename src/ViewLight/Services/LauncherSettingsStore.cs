using System.Globalization;
using System.Text.Json;
using ViewLight.Models;

namespace ViewLight.Services;

/// <summary>
/// Persists <see cref="LauncherSettings"/> to <c>%LOCALAPPDATA%\ViewLight\launcher.json</c>.
/// </summary>
public sealed class LauncherSettingsStore {

    private static readonly JsonSerializerOptions s_options = new() {
        WriteIndented = true,
    };

    public string FilePath { get; }

    public LauncherSettings Settings { get; private set; } = new();

    /// <summary>
    /// Why the settings file could not be read, when it could not be.
    /// </summary>
    /// <remarks>
    /// A file that will not load is replaced with defaults so the launcher still starts - but it is a real
    /// event: it is how a launcher comes up with somebody's folders and accounts "forgotten". Keeping the
    /// reason is what lets it be said out loud instead of looking like the settings were never there.
    /// </remarks>
    public string? LoadProblem { get; private set; }

    public LauncherSettingsStore(string? filePath = null) {
        FilePath = filePath ?? LauncherPaths.SettingsFile;
        Settings = Load();
    }

    private LauncherSettings Load() {
        try {
            if (!File.Exists(FilePath)) {
                return CreateDefault();
            }

            var json = File.ReadAllText(FilePath);
            var loaded = JsonSerializer.Deserialize<LauncherSettings>(json, s_options);
            if (loaded is null) {
                return CreateDefault();
            }

            // A portable launcher stores the folders that sit inside its own root relative to that root,
            // so they are turned back into absolute paths before anything else looks at them.
            loaded.ClientPath = LauncherPaths.ResolveStoredPath(loaded.ClientPath);
            loaded.ServerPath = LauncherPaths.ResolveStoredPath(loaded.ServerPath);
            loaded.PatchServerPath = LauncherPaths.ResolveStoredPath(loaded.PatchServerPath);

            // Empty paths are left empty on purpose: a path the user cleared must stay cleared, and
            // the first-run setup asks for them rather than having them silently reappear.
            //
            // Non-empty ones are nudged onto the game folder itself, because the setting means the
            // folder holding bin\ and Data\ - not the bin\ folder the executable sits in.
            loaded.ClientPath = GameFolderLocator.NormaliseGameFolder(loaded.ClientPath);

            return loaded;
        }
        catch (Exception ex) {
            // A corrupt settings file must never stop the launcher from starting, but it is not nothing
            // either: this is the replacement, and the reason is kept so the launcher can say so.
            LoadProblem = $"{Path.GetFileName(FilePath)} could not be read ({ex.GetType().Name}: {ex.Message})";
            return CreateDefault();
        }
    }

    private static LauncherSettings CreateDefault() {
        // Portable: the game belongs in the launcher's own folder and the server beside it, so that is
        // where they are looked for. Nothing outside the folder is assumed to exist - which is the whole
        // point of the mode.
        if (PortableMode.IsOn) {
            return new LauncherSettings {
                ClientPath = LauncherPaths.GameDirectory,
                ServerPath = LauncherPaths.DefaultServerPath,
            };
        }

        return new LauncherSettings {
            ClientPath = GameFolderLocator.Detect()?.Path ?? string.Empty,
            ServerPath = string.Empty,
        };
    }

    public void Save() {
        var directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(directory)) {
            Directory.CreateDirectory(directory);
        }

        // Written with its own folders stored relative to the root in portable mode, so the whole folder
        // can be moved between drives. The live settings keep the absolute paths the rest of the
        // launcher uses; they are put back as soon as the file is written.
        var (clientPath, serverPath, patchServerPath) =
            (Settings.ClientPath, Settings.ServerPath, Settings.PatchServerPath);

        try {
            Settings.ClientPath = LauncherPaths.ToStoredPath(clientPath);
            Settings.ServerPath = LauncherPaths.ToStoredPath(serverPath);
            Settings.PatchServerPath = LauncherPaths.ToStoredPath(patchServerPath);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Settings, s_options));
        }
        finally {
            Settings.ClientPath = clientPath;
            Settings.ServerPath = serverPath;
            Settings.PatchServerPath = patchServerPath;
        }
    }
}

/// <summary>Where the launcher keeps its own files.</summary>
public static class LauncherPaths {

    private static string? s_dataRoot;

    /// <summary>
    /// Where the launcher keeps its own files. Settable so the launcher can be made portable, and so
    /// tests can point it at a scratch folder instead of the real user profile.
    /// </summary>
    public static string DataRoot {
        get => s_dataRoot ??= NormalDataRoot;
        set => s_dataRoot = value;
    }

    /// <summary>The data root a normal, non-portable launcher uses.</summary>
    public static string NormalDataRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ViewLight");

    public static string SettingsFile => Path.Combine(DataRoot, "launcher.json");

    /// <summary>
    /// The launcher's own profiles, and the playtime and achievements they have collected. Launcher
    /// state rather than server state, so it lives beside the settings and moves with a portable folder.
    /// </summary>
    public static string ProfilesFile => Path.Combine(DataRoot, "profiles.json");

    /// <summary>The folder an account's own profile files - its picture and banner - live in.</summary>
    public static string ProfileDirectory(ulong accountId)
        => Path.Combine(DataRoot, "profiles", accountId.ToString(CultureInfo.InvariantCulture));

    /// <summary>Downloaded Imlight release archives, cached so a retry does not re-download.</summary>
    public static string DownloadsDirectory => Path.Combine(DataRoot, "downloads");

    /// <summary>
    /// The game's own art, downloaded on first use and kept so later starts need no connection.
    /// </summary>
    public static string ArtCacheDirectory => Path.Combine(DataRoot, "art");

    /// <summary>
    /// The seven school element logos, fetched once and kept with the rest of the art.
    /// </summary>
    public static string SchoolIconsDirectory => Path.Combine(ArtCacheDirectory, "schools");

    /// <summary>
    /// The index of the gamerpic archive: the repository's own listing of everything it holds.
    /// </summary>
    /// <remarks>
    /// One file rather than 32,000 requests. The archive is a repository of Xbox 360 gamerpics belonging
    /// to the person who asked for this screen, and it publishes a CSV naming every one of them - so the
    /// browser reads that once, keeps it, and fetches only the pictures actually looked at.
    /// </remarks>
    public static string GamerpicIndexFile => Path.Combine(ArtCacheDirectory, "gamerpics.csv");

    /// <summary>Gamerpics that have been looked at, kept so a page is not downloaded twice.</summary>
    public static string GamerpicDirectory => Path.Combine(ArtCacheDirectory, "gamerpics");

    /// <summary>Wallpapers and their thumbnails that have been looked at.</summary>
    public static string WallhavenDirectory => Path.Combine(ArtCacheDirectory, "wallhaven");

    /// <summary>Scratch space used while extracting a release or building one.</summary>
    public static string StagingDirectory => Path.Combine(DataRoot, "staging");

    /// <summary>
    /// Where the bleeding-edge source checkout lives, along with the Imcodec type dump, the private
    /// Python environment that makes it and the extracted message definitions. Beside the launcher's
    /// other files so a portable folder carries them too - and so a source build is a build of something
    /// the launcher owns rather than of whatever happens to be in a folder somebody chose.
    /// </summary>
    public static string SourceDirectory => Path.Combine(DataRoot, "source");

    /// <summary>The folder the launcher suggests when the user has no preference.</summary>
    public static string DefaultServerPath => Path.Combine(DataRoot, "server");

    /// <summary>
    /// Where the local patch server is installed by default: beside the server, inside the launcher's
    /// own folder, so a portable launcher carries it too.
    /// </summary>
    public static string DefaultPatchServerPath => Path.Combine(DataRoot, "patchserver");

    /// <summary>
    /// Where portable mode keeps the game once it has been copied in, and where Play looks for it by
    /// default - so a portable launcher is self-contained rather than pointing at an install elsewhere.
    /// </summary>
    public static string GameDirectory => Path.Combine(DataRoot, "game");

    /// <summary>
    /// Turns a folder the user picked into what gets written to <c>launcher.json</c>.
    /// </summary>
    /// <remarks>
    /// In portable mode a folder inside the launcher's own root is stored <b>relative to it</b>, which is
    /// what lets the whole thing be moved to another drive and keep working. A folder outside the root is
    /// stored as an absolute path, because it genuinely does live somewhere else and no relative path
    /// could describe it.
    /// </remarks>
    public static string ToStoredPath(string? path) => ToStoredPath(path, PortableMode.IsOn, DataRoot);

    /// <summary>
    /// The same rule with the mode and the root given rather than read from the ambient ones, which is
    /// what makes it testable without a portable launcher on disk.
    /// </summary>
    public static string ToStoredPath(string? path, bool portable, string root) {
        if (string.IsNullOrWhiteSpace(path) || !portable) {
            return path ?? string.Empty;
        }

        try {
            var full = Path.GetFullPath(path);
            var relative = Path.GetRelativePath(root, full);

            // GetRelativePath answers with ".." segments - or with the input itself, across drives - when
            // the target is not inside the root. Neither is portable, so those stay absolute.
            return relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative)
                ? full
                : relative;
        }
        catch (Exception) {
            return path;
        }
    }

    /// <summary>The absolute folder behind what was stored, for everything that needs a real path.</summary>
    public static string ResolveStoredPath(string? stored)
        => ResolveStoredPath(stored, PortableMode.IsOn, DataRoot);

    /// <summary>The same rule with the mode and the root given, for the same reason as above.</summary>
    public static string ResolveStoredPath(string? stored, bool portable, string root) {
        if (string.IsNullOrWhiteSpace(stored)) {
            return string.Empty;
        }

        if (!portable || Path.IsPathRooted(stored)) {
            return stored;
        }

        try {
            return Path.GetFullPath(Path.Combine(root, stored));
        }
        catch (Exception) {
            return stored;
        }
    }
}
