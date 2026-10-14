using System.Runtime.Versioning;
using Microsoft.Win32;

namespace ViewLight.Services;

/// <summary>A place that might hold a Wizard101 install, and where that suggestion came from.</summary>
public sealed record GameFolderCandidate(string Path, string Source);

/// <summary>
/// Finds a Wizard101 installation so the user usually does not have to go looking for it.
/// </summary>
/// <remarks>
/// The launcher stores the <b>game folder</b> (the one containing <c>bin\</c> and <c>Data\</c>), not the
/// executable, and everything else is resolved relative to it:
/// <list type="bullet">
///   <item><description>game client - <c>bin\WizardGraphicalClient.exe</c></description></item>
///   <item><description>root archive - <c>Data\GameData\root.wad</c></description></item>
/// </list>
/// Detection is best effort and never mandatory: whatever it finds is offered as a suggestion, and the
/// user can point at a folder anywhere on disk instead.
/// </remarks>
public static class GameFolderLocator {

    /// <summary>Client executable, relative to the game folder.</summary>
    public const string RelativeClientExecutable = @"bin\WizardGraphicalClient.exe";

    /// <summary>Root archive, relative to the game folder.</summary>
    public const string RelativeRootWad = @"Data\GameData\root.wad";

    /// <summary>The Steam client library a Steam copy of the game loads, relative to the game folder.</summary>
    public const string RelativeSteamApi = @"bin\steam_api.dll";

    /// <summary>The app id file a Steam copy ships beside it.</summary>
    private const string RelativeSteamAppId = @"bin\steam_appid.txt";

    private const string SteamGameFolderName = "Wizard101";

    /// <summary>Non-Steam installs land here by default.</summary>
    private static readonly string[] KnownInstallPaths = [
        @"C:\Program Files (x86)\KingsIsle Entertainment\Wizard101",
        @"C:\Program Files\KingsIsle Entertainment\Wizard101",
        @"C:\ProgramData\KingsIsle Entertainment\Wizard101",
    ];

    /// <summary>Does this folder actually look like a Wizard101 game folder?</summary>
    public static bool LooksLikeGameFolder(string? path) {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) {
            return false;
        }

        return File.Exists(Path.Combine(path, RelativeClientExecutable))
               || File.Exists(Path.Combine(path, RelativeRootWad));
    }

    /// <summary>
    /// Corrects a folder that is one level too deep, which is the easy mistake to make: the client
    /// executable lives in <c>bin\</c>, so <c>...\Wizard101\Bin</c> is a tempting thing to select even
    /// though everything is addressed relative to <c>...\Wizard101</c>.
    /// </summary>
    /// <returns>The game folder, or the original value when no correction applies.</returns>
    public static string NormaliseGameFolder(string? path) {
        if (string.IsNullOrWhiteSpace(path)) {
            return string.Empty;
        }

        if (LooksLikeGameFolder(path)) {
            return path;
        }

        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (Path.GetFileName(trimmed).Equals("bin", StringComparison.OrdinalIgnoreCase)) {
            var parent = Path.GetDirectoryName(trimmed);
            if (!string.IsNullOrWhiteSpace(parent) && LooksLikeGameFolder(parent)) {
                return parent;
            }
        }

        return path;
    }

    /// <summary>
    /// Whether this folder holds a Steam copy of Wizard101 rather than a standalone one.
    /// </summary>
    /// <remarks>
    /// A Steam install ships <c>bin\steam_api.dll</c> and <c>bin\steam_appid.txt</c>; the standalone
    /// installer from KingsIsle ships neither - verified against a real Steam install on this machine.
    /// <para>
    /// This matters to portable mode. A Steam copy still needs the Steam client installed and signed in
    /// to start, because its <c>steam_api.dll</c> has nothing to talk to otherwise, so copying the game's
    /// files into the launcher's own folder does <b>not</b> make that copy stand alone. A standalone copy
    /// has no such dependency, which is what makes one worth copying into a portable launcher.
    /// </para>
    /// </remarks>
    public static bool IsSteamInstall(string? path) {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) {
            return false;
        }

        return File.Exists(Path.Combine(path, RelativeSteamApi))
               || File.Exists(Path.Combine(path, RelativeSteamAppId));
    }

    /// <summary>Every suggested game folder, best first, de-duplicated.</summary>
    public static IReadOnlyList<GameFolderCandidate> Candidates() {
        var results = new List<GameFolderCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? path, string source) {
            if (string.IsNullOrWhiteSpace(path)) {
                return;
            }

            string normalised;
            try {
                normalised = Path.GetFullPath(path.Trim().Trim('"'));
            }
            catch (Exception) {
                return;
            }

            if (seen.Add(normalised)) {
                results.Add(new GameFolderCandidate(normalised, source));
            }
        }

        // -------- Steam: the registry records the install, and each library has a common/ folder.
        // Only Windows has a registry; elsewhere the conventional paths below are all there is.
        if (OperatingSystem.IsWindows()) {
            foreach (var steam in SteamRoots()) {
                foreach (var library in LibraryFolders(steam)) {
                    Add(Path.Combine(library, "steamapps", "common", SteamGameFolderName), "Steam library");
                }
            }
        }

        foreach (var known in KnownInstallPaths) {
            Add(known, "default install location");
        }

        return results;
    }

    /// <summary>The first candidate that actually contains a Wizard101 install, or <c>null</c>.</summary>
    public static GameFolderCandidate? Detect()
        => Candidates().FirstOrDefault(candidate => LooksLikeGameFolder(candidate.Path));

    /// <summary>
    /// Steam's own folders, from the registry keys Steam writes at install time. Missing keys or a
    /// non-Windows host simply produce nothing.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static IEnumerable<string> SteamRoots() {
        var valueNames = new (RegistryHive Hive, RegistryView View, string SubKey, string Value)[] {
            (RegistryHive.CurrentUser, RegistryView.Registry64, @"Software\Valve\Steam", "SteamPath"),
            (RegistryHive.CurrentUser, RegistryView.Registry32, @"Software\Valve\Steam", "SteamPath"),
            (RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
            (RegistryHive.LocalMachine, RegistryView.Registry32, @"SOFTWARE\Valve\Steam", "InstallPath"),
        };

        foreach (var (hive, view, subKey, value) in valueNames) {
            var path = ReadRegistryString(hive, view, subKey, value);
            if (!string.IsNullOrWhiteSpace(path)) {
                yield return path;
            }
        }

        // SteamExe points at steam.exe rather than the folder.
        var steamExe = ReadRegistryString(
            RegistryHive.CurrentUser, RegistryView.Registry64, @"Software\Valve\Steam", "SteamExe");
        if (!string.IsNullOrWhiteSpace(steamExe)) {
            var directory = Path.GetDirectoryName(steamExe.Replace('/', '\\'));
            if (!string.IsNullOrWhiteSpace(directory)) {
                yield return directory;
            }
        }
    }

    /// <summary>
    /// The Steam root plus every additional library listed in <c>steamapps\libraryfolders.vdf</c>,
    /// which is where the game actually lives when it is on a second drive.
    /// </summary>
    private static IEnumerable<string> LibraryFolders(string steamRoot) {
        var root = steamRoot.Replace('/', '\\').TrimEnd('\\');

        yield return root;

        var vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf)) {
            yield break;
        }

        IEnumerable<string> lines;
        try {
            lines = File.ReadLines(vdf).ToList();
        }
        catch (IOException) {
            yield break;
        }

        foreach (var line in lines) {
            var tokens = QuotedTokens(line);
            if (tokens.Count >= 2 && tokens[0].Equals("path", StringComparison.OrdinalIgnoreCase)) {
                // VDF escapes separators, so "D:\\Steam" means D:\Steam.
                yield return tokens[^1].Replace(@"\\", @"\");
            }
        }
    }

    private static List<string> QuotedTokens(string line) {
        var tokens = new List<string>();
        var index = 0;

        while (index < line.Length) {
            if (line[index] != '"') {
                index++;
                continue;
            }

            var end = line.IndexOf('"', index + 1);
            if (end < 0) {
                break;
            }

            tokens.Add(line[(index + 1)..end]);
            index = end + 1;
        }

        return tokens;
    }

    [SupportedOSPlatform("windows")]
    private static string? ReadRegistryString(
        RegistryHive hive, RegistryView view, string subKey, string valueName) {
        try {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var key = baseKey.OpenSubKey(subKey);
            return key?.GetValue(valueName) as string;
        }
        catch (Exception) {
            // A missing key, a denied key, or an odd registry view is not an error here.
            return null;
        }
    }
}
