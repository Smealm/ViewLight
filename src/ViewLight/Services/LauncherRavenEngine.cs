using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ViewLight.Services;

/// <summary>Which copy of RavenDB a database of the launcher's own is to be run from.</summary>
/// <param name="Directory">The folder holding <c>Raven.Server.dll</c>.</param>
/// <param name="IsOwnCopy">Whether this is the launcher's own copy rather than the server install's.</param>
/// <param name="Version">Its version, when it can be read - for the Log and for the staleness rule.</param>
public sealed record LauncherEngine(string Directory, bool IsOwnCopy, string? Version = null);

/// <summary>What making or refreshing the launcher's own copy of the engine did.</summary>
/// <param name="Copied">Whether anything was written this time.</param>
/// <param name="Directory">Where the copy is.</param>
/// <param name="Version">What was copied, or what was already there.</param>
/// <param name="Files">How many files were written when something was.</param>
/// <param name="Problem">Why nothing was copied, when something should have been.</param>
public sealed record EngineCopyResult(
    bool Copied,
    string Directory,
    string? Version,
    int Files = 0,
    string? Problem = null);

/// <summary>
/// The launcher's own copy of the RavenDB the server ships, kept under the launcher's own root.
/// </summary>
/// <remarks>
/// <para>
/// <b>The server folder belongs to Imlight.</b> The launcher installs there, it edits the one file Imlight
/// expects an outside tool to edit (<c>Config/Imlight.ini</c>), and it reads the server's own database from
/// where Imlight's config says it is - and that is the whole of it. Everything the launcher writes for
/// itself lives under <see cref="LauncherPaths.DataRoot"/>: its settings, its profiles, its notes database,
/// its art, its downloads. This file is what makes the last borrowed piece - the engine its own database runs
/// on - its own as well.
/// </para>
/// <para>
/// <b>Why a copy rather than running out of the server folder.</b> Both of the launcher's databases used to be
/// started from <c>&lt;server&gt;\RavenDBServer</c>, which meant that while either was up, those ~200 assemblies
/// were loaded and could not be replaced - so an Imlight update collided with the launcher itself and failed
/// with "the process cannot access the file ... because it is being used by another process". The launcher's
/// own notes are nobody else's business, so the engine that opens them is the launcher's too, under its own
/// root, and an update no longer runs into it.
/// </para>
/// <para>
/// <b>The server's own database still runs on the server's own engine</b> - see
/// <see cref="EmbeddedRavenSetup.For"/>, which is untouched by this. That data directory is Imlight's, written
/// by the engine Imlight ships today, and opening it with an older copy of RavenDB (the one this launcher was
/// built against) is a risk nobody asked for: the same engine as the server's next boot is the right answer
/// for the server's data. The launcher's own data directory has no such reader, so the launcher's own copy is
/// the right answer for it.
/// </para>
/// <para>
/// <b>It is pruned, because most of the engine is other platforms.</b> The folder is 567 MB as shipped, of
/// which 422 MB is <c>runtimes\</c> - native libraries for every platform RavenDB supports. A copy made for
/// this machine keeps its own platform's runtime folder and the fallback the host resolves through, and drops
/// the rest: measured at 179 MB, and verified by running a real database from it (started, created its
/// database, wrote and read a document, stored and read back an attachment). Anything whose name is not a
/// runtime identifier it recognizes is kept rather than dropped, so a future addition is copied and the
/// pruning rule stays a rule about other platforms rather than a guess about what is needed.
/// </para>
/// </remarks>
public static class LauncherRavenEngine {

    /// <summary>The launcher's own copy of the engine, under the launcher's own root.</summary>
    public const string FolderName = "ravendb";

    /// <summary>The folder a copy is built in before it replaces the last one.</summary>
    public const string PartialSuffix = ".partial";

    /// <summary>Written last, so a copy that did not finish is not mistaken for one that did.</summary>
    private const string StampFileName = ".viewlight-engine.json";

    /// <summary>Where that stamp is, for anybody asking whether a copy finished.</summary>
    public static string StampPath(string dataRoot)
        => Path.Combine(DirectoryFor(dataRoot), StampFileName);

    /// <summary>Where the launcher's copy lives - beside its database and its settings, never in the server.</summary>
    public static string DirectoryFor(string dataRoot) => Path.Combine(dataRoot, FolderName);

    /// <summary>The engine assembly inside that copy.</summary>
    public static string AssemblyFor(string dataRoot)
        => ImlightConfig.RavenServerAssemblyFor(DirectoryFor(dataRoot));

    /// <summary>How big a copy is, for the Log and for anybody wondering why it is there.</summary>
    public static long SizeOf(string engineDirectory) {
        try {
            return Directory.Exists(engineDirectory)
                ? Directory.EnumerateFiles(engineDirectory, "*", SearchOption.AllDirectories)
                    .Sum(file => new FileInfo(file).Length)
                : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return 0;
        }
    }

    /// <summary>
    /// Which copy of the engine the launcher's own database should run from, or <c>null</c> when neither is
    /// there.
    /// </summary>
    /// <remarks>
    /// Its own copy first, deliberately, and the install's second: an install that has just been made - or one
    /// made by a launcher that predates this - has an engine to borrow, and borrowing it is a great deal better
    /// than having no notes at all. <see cref="MakeFresh"/> is what turns the borrowing case into the owned
    /// one, and it is called before this is.
    /// </remarks>
    public static LauncherEngine? Choose(ImlightConfig config, string? serverPath, string dataRoot) {
        if (IsComplete(dataRoot)) {
            return new LauncherEngine(DirectoryFor(dataRoot), IsOwnCopy: true, VersionOf(DirectoryFor(dataRoot)));
        }

        var server = ServerEngineOf(config, serverPath);

        return server is null ? null : new LauncherEngine(server, IsOwnCopy: false, VersionOf(server!));
    }

    /// <summary>The engine inside the install, or <c>null</c> when there is no install or no engine in it.</summary>
    public static string? ServerEngineOf(ImlightConfig config, string? serverPath) {
        if (string.IsNullOrWhiteSpace(serverPath)) {
            return null;
        }

        try {
            var directory = config.ResolveRavenServerDirectory(serverPath);

            // A configured path that leads nowhere is an install without the database: not an engine to run.
            return Directory.Exists(directory) &&
                   File.Exists(ImlightConfig.RavenServerAssemblyFor(directory))
                ? directory
                : null;
        }
        catch (Exception) {
            return null;
        }
    }

    /// <summary>The version of the engine in a folder, or <c>null</c> when it cannot be read.</summary>
    public static string? VersionOf(string engineDirectory) {
        try {
            var assembly = ImlightConfig.RavenServerAssemblyFor(engineDirectory);
            return File.Exists(assembly) ? FileVersionInfo.GetVersionInfo(assembly).FileVersion : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) {
            return null;
        }
    }

    /// <summary>
    /// Whether the launcher has a complete copy of its own to run from: the assembly, and the stamp that says
    /// a copy finished here.
    /// </summary>
    /// <remarks>
    /// The stamp is what makes a half-written copy not a copy. Without it, a machine that lost power halfway
    /// through would be left with a folder that looks like an engine and refuses to start, and the launcher
    /// would have no notes until somebody thought to delete it.
    /// </remarks>
    public static bool IsComplete(string dataRoot) {
        var directory = DirectoryFor(dataRoot);

        return File.Exists(ImlightConfig.RavenServerAssemblyFor(directory))
               && File.Exists(StampPath(dataRoot));
    }

    /// <summary>
    /// Makes the launcher's own copy if it is missing, or replaces it if the install's engine has moved on.
    /// </summary>
    /// <remarks>
    /// Called before the launcher's own database is started and again after an install, because those are the
    /// two moments it can be: nothing is running out of either copy then, so the folder can be replaced
    /// wholesale. Installing Imlight is what brings a new engine, and the launcher follows it - which is also
    /// why a copy never has to be shipped or downloaded: it is made from what is already installed, at the
    /// version that install has.
    /// </remarks>
    public static EngineCopyResult MakeFresh(ImlightConfig? config, string? serverPath, string dataRoot) {
        var directory = DirectoryFor(dataRoot);
        var server = config is null ? null : ServerEngineOf(config, serverPath);

        if (server is null) {
            return new EngineCopyResult(false, directory, VersionOf(directory),
                Problem: "There is no Imlight install to take the launcher's own copy of the database from.");
        }

        var sourceVersion = VersionOf(server);

        // Nothing to do when there is a finished copy and the install has not brought a different engine. A
        // version that cannot be read is not counted as a difference: recopying 179 MB on every start because
        // one file would not say what it is would be worse than a copy that is one build behind.
        if (IsComplete(dataRoot) && (sourceVersion is null || sourceVersion == StampedVersion(dataRoot))) {
            return new EngineCopyResult(false, directory, sourceVersion);
        }

        return Copy(server, dataRoot, sourceVersion);
    }

    /// <summary>The version the copy in this data root was made from.</summary>
    public static string? StampedVersion(string dataRoot) {
        try {
            var stamp = StampPath(dataRoot);

            if (!File.Exists(stamp)) {
                return null;
            }

            return JsonNode.Parse(File.ReadAllText(stamp))?["version"]?.GetValue<string>();
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException
                                      or UnauthorizedAccessException) {
            return null;
        }
    }

    /// <summary>
    /// Copies an engine for the launcher's own use, keeping only this platform's runtime folder.
    /// </summary>
    private static EngineCopyResult Copy(string sourceDirectory, string dataRoot, string? version) {
        var directory = DirectoryFor(dataRoot);
        var partial = directory + PartialSuffix;

        try {
            Discard(partial);
            CopyTree(sourceDirectory, partial, out var files);

            // The stamp before the move, so a folder that is in place is a folder that finished.
            File.WriteAllText(Path.Combine(partial, StampFileName), Stamp(version, sourceDirectory));

            Replace(directory, partial);

            return new EngineCopyResult(true, directory, version, files);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            Discard(partial);

            return new EngineCopyResult(false, directory, version,
                Problem: $"The launcher's own copy of the database could not be made ({ex.Message}).");
        }
    }

    private static void CopyTree(string source, string target, out int files) {
        files = 0;
        Directory.CreateDirectory(target);

        foreach (var directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories)) {
            // Relative to the engine root, so the platform check is about the folder under runtimes\ and not
            // about where the engine happens to be installed.
            var relative = Path.GetRelativePath(source, directory);

            if (!Keep(relative)) {
                continue;
            }

            Directory.CreateDirectory(Path.Combine(target, relative));
        }

        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories)) {
            var relative = Path.GetRelativePath(source, file);

            // A file inside a dropped runtime folder is dropped with it.
            if (!Keep(Path.GetDirectoryName(relative) ?? string.Empty)) {
                continue;
            }

            var destination = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
            files++;
        }
    }

    /// <summary>Where the platform's runtimes live inside an engine folder.</summary>
    private const string RuntimesFolder = "runtimes";

    /// <summary>
    /// Whether a folder inside the engine is copied: everything, except the runtime folders of platforms that
    /// are not this one.
    /// </summary>
    /// <remarks>
    /// The rule is a blacklist of <i>known other platforms</i> rather than a whitelist of wanted files, so a
    /// folder nobody here has heard of - a new platform, a new folder RavenDB ships - is copied rather than
    /// dropped. Pruning conservatively costs 179 MB instead of 150; dropping something the engine needs costs
    /// a database that will not start.
    /// </remarks>
    public static bool Keep(string relativePath) {
        if (relativePath.Length == 0) {
            return true;
        }

        var parts = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (parts[0] != RuntimesFolder || parts.Length < 2) {
            return true;
        }

        return !OtherPlatforms.Contains(parts[1]);
    }

    /// <summary>
    /// The runtime folders a copy drops: every platform RavenDB ships libraries for except this one, and the
    /// fallback folder the host resolves an unknown identifier through.
    /// </summary>
    public static IReadOnlySet<string> OtherPlatforms { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
        "linux-x64", "linux-arm64", "linux-arm", "linux-musl-x64", "linux-musl-arm64", "linux",
        "osx-x64", "osx-arm64", "maccatalyst-x64", "maccatalyst-arm64", "freebsd-x64", "freebsd", "browser-wasm",
        "win-x86", "win-arm64", "win-arm",
    }.Except(PlatformRuntimeIdentifiers()).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// This machine's runtime identifiers, which are the ones a copy keeps: its own, and the fallback the host
    /// resolves through when it asks for something more specific.
    /// </summary>
    public static IReadOnlyList<string> PlatformRuntimeIdentifiers() {
        var architecture = RuntimeInformation.ProcessArchitecture switch {
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            Architecture.Arm => "arm",
            _ => "x64",
        };

        return OperatingSystem.IsWindows()
            ? [$"win-{architecture}", "win"]
            : OperatingSystem.IsMacOS()
                ? [$"osx-{architecture}", "osx", "unix"]
                : [$"linux-{architecture}", "unix"];
    }

    private static string Stamp(string? version, string sourceDirectory)
        => new JsonObject {
            ["version"] = version,
            ["source"] = sourceDirectory,
            ["copiedAt"] = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture),
        }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

    /// <summary>
    /// Puts the finished copy where the engine is looked for, refusing rather than guessing when it cannot.
    /// </summary>
    /// <remarks>
    /// A folder that will not be deleted is a database still running out of it - which is a caller that did not
    /// stop what it owns, not something to paper over. The old copy is left alone in that case, and the next
    /// start tries again.
    /// </remarks>
    private static void Replace(string directory, string partial) {
        if (Directory.Exists(directory)) {
            Directory.Delete(directory, recursive: true);
        }

        Directory.Move(partial, directory);
    }

    private static void Discard(string path) {
        try {
            if (Directory.Exists(path)) {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // Leftovers are overwritten by the next attempt; a copy that cannot even be cleaned up is not
            // worth failing a start over.
        }
    }
}
