using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ViewLight.Services;

/// <summary>
/// Which host a framework-dependent application should be run with, and the runtime it should be given.
/// </summary>
/// <param name="Host">The host executable to start the application with.</param>
/// <param name="RuntimeRoot">
/// The root of a runtime of its own to point that host at, or <c>null</c> to leave the machine's own
/// resolution alone.
/// </param>
/// <param name="IsBundled">Whether this is the runtime the launcher ships with rather than the machine's.</param>
public sealed record DotnetHostChoice(string Host, string? RuntimeRoot, bool IsBundled) {

    /// <summary>
    /// The framework this host cannot offer, or <c>null</c> when it can offer everything asked for.
    /// </summary>
    /// <remarks>
    /// Set only when a host was asked and answered short - never for one that would not answer at all, whose
    /// answer is "unknown" rather than "not enough". A host with this set is still the host to use: it is the
    /// best there is, and what it cannot run is the application's to report.
    /// </remarks>
    public DotnetFramework? Unmet { get; init; }

    /// <summary>Whether this host offers everything the application asked for.</summary>
    public bool CanRun => Unmet is null;

    /// <summary>Which runtime this is, for the Log and for a status line.</summary>
    public string Description => IsBundled
        ? $"the .NET runtime ViewLight ships with ({Host})"
        : $"the .NET runtime installed on this machine ({Host})";
}

/// <summary>One shared framework a .NET host offers, or one an application asks it for.</summary>
public sealed record DotnetFramework(string Name, string Version) {

    /// <summary>The major version, which is as far as a framework-dependent application rolls forward.</summary>
    public int Major => System.Version.TryParse(Version, out var version) ? version.Major : 0;

    public override string ToString() => $"{Name} {Version}";
}

/// <summary>
/// The .NET host the server's RavenDB is run with, and what that host can offer.
/// </summary>
/// <remarks>
/// <para>
/// The launcher's own data lives in the RavenDB Imlight ships, and that database is a
/// <b>framework-dependent</b> application: its <c>Raven.Server.runtimeconfig.json</c> asks for a shared
/// runtime instead of carrying one. The Imlight install is self-contained - it brings its own runtime for
/// the server and nothing the database can borrow - so something has to provide one.
/// </para>
/// <para>
/// <b>The runtime that ships with the launcher comes first, and the machine's is the fallback.</b> A
/// <c>runtime</c> folder beside the executable holds a whole .NET install - the host and the shared
/// frameworks laid out the way an installer lays them out - and the database is run with that, so a machine
/// needs no runtime of its own and the same build behaves the same way wherever it is unpacked. Pointing a
/// host at a runtime of its own is what <see cref="UseRuntimeRoot"/> is for. The machine-wide install is
/// still looked for, in the order it used to be: a launcher that has been copied somewhere without its
/// runtime folder is a launcher that should still work where the machine can provide one.
/// </para>
/// <para>
/// The lookups are properties so that a test can decide the answer: whether the launcher ships a runtime,
/// whether the machine has one, and what either reports, are the facts the launcher's first screen turns on.
/// </para>
/// </remarks>
public static class DotnetHost {

    private static readonly Dictionary<string, IReadOnlyList<DotnetFramework>> s_asked = [];
    private static readonly Lock s_gate = new();

    /// <summary>
    /// The folder the launcher's own runtime is unpacked into, beside the executable.
    /// </summary>
    /// <remarks>
    /// Named for what it is rather than for what is in it, because what is in it is a whole .NET
    /// installation: <c>dotnet.exe</c>, <c>host/</c>, and <c>shared/Microsoft.NETCore.App</c> and
    /// <c>shared/Microsoft.AspNetCore.App</c> under it. That layout is the one an installer produces and the
    /// one the host resolves a shared framework from - a self-contained application's flat output is not it,
    /// and cannot be borrowed as if it were.
    /// </remarks>
    public const string BundledRuntimeFolderName = "runtime";

    /// <summary>Where that runtime is, or would be, in this build.</summary>
    public static string BundledRuntimeDirectory
        => Path.Combine(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory), BundledRuntimeFolderName);

    /// <summary>The host inside the launcher's own runtime, or null when it ships none.</summary>
    public static Func<string?> FindBundled { get; set; } = FindInstalledBundled;

    /// <summary>The dotnet executable this machine has, or null when it has none.</summary>
    public static Func<string?> Find { get; set; } = FindInstalled;

    /// <summary>
    /// What a host says it has, or <c>null</c> when it will not say - which is a different answer from
    /// saying it has nothing.
    /// </summary>
    public static Func<string, IReadOnlyList<DotnetFramework>?> ListFrameworks { get; set; } = Ask;

    /// <summary>
    /// What a host offers, asked once and remembered for the run - or <c>null</c> when it would not say.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Asked once because it costs a process launch (measured at about 60ms, which is why the answer is
    /// waited for on the way in rather than made asynchronous: the first screen has to know before it
    /// decides whether to show itself), and because it cannot change while the launcher runs - unless
    /// somebody installs a runtime and presses Check again, which is what <see cref="Forget"/> is for.
    /// </para>
    /// <para>
    /// <b>A host that would not answer is not remembered.</b> A probe that timed out is a fact about the
    /// moment - a machine busy enough to eat ten seconds - and caching it turned one bad moment into a
    /// launcher that claimed, for the rest of the run, that the runtime was not there.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<DotnetFramework>? FrameworksOf(string host) {
        lock (s_gate) {
            if (s_asked.TryGetValue(host, out var known)) {
                return known;
            }
        }

        // Outside the lock: this is the call that starts a process, and two screens asking at once is not a
        // reason for one of them to wait on it holding everybody else up.
        var asked = ListFrameworks(host);

        if (asked is null) {
            return null;
        }

        lock (s_gate) {
            s_asked[host] = asked;
        }

        return asked;
    }

    /// <summary>Forgets what was asked, so a runtime installed while the launcher runs can be found.</summary>
    public static void Forget() {
        lock (s_gate) {
            s_asked.Clear();
        }
    }

    /// <summary>Where a machine-wide .NET install usually is.</summary>
    private static IEnumerable<string> UsualHosts() {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        yield return Path.Combine(programFiles, "dotnet", ExecutableName);
        yield return Path.Combine(local, "Microsoft", "dotnet", ExecutableName);
        yield return Path.Combine("/usr/share/dotnet", ExecutableName);
        yield return Path.Combine("/usr/local/share/dotnet", ExecutableName);
        yield return Path.Combine(home, ".dotnet", ExecutableName);
    }

    private static string ExecutableName => OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";

    /// <summary>The host beside the executable, if this build was published with one.</summary>
    private static string? FindInstalledBundled() {
        try {
            var candidate = Path.Combine(BundledRuntimeDirectory, ExecutableName);
            return File.Exists(candidate) ? candidate : null;
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException) {
            // A folder that cannot even be named is a build with no runtime beside it.
            return null;
        }
    }

    /// <summary>
    /// The dotnet executable on this machine, or null when there is none.
    /// </summary>
    /// <remarks>
    /// PATH is searched as well as the places an installer puts it, because a host that is only on PATH is
    /// still a host. It is resolved to a real file rather than left as the name: what is wanted here is
    /// whether the machine has one at all, and "dotnet" fails at that question just as happily as it would
    /// fail to start.
    /// </remarks>
    public static string? FindInstalled() {
        foreach (var candidate in UsualHosts()) {
            if (File.Exists(candidate)) {
                return candidate;
            }
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;

        foreach (var folder in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
            try {
                var candidate = Path.Combine(folder, ExecutableName);

                if (File.Exists(candidate)) {
                    return candidate;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or PathTooLongException) {
                // A PATH entry that is not a path at all. The rest of them are still worth looking at.
            }
        }

        return null;
    }

    /// <summary>A host asked what it has, or <c>null</c> when it will not say.</summary>
    /// <remarks>
    /// Asked twice before giving up, because the two ways this fails - a process that will not start, and one
    /// that eats the whole of its wait - are both about the machine at that moment rather than about the
    /// runtime: a second launch a moment later answers normally. Null rather than an empty list is the whole
    /// point: the caller has to be able to tell "it has nothing" from "it would not say".
    /// </remarks>
    private static IReadOnlyList<DotnetFramework>? Ask(string host) {
        for (var attempt = 0; attempt < 2; attempt++) {
            var asked = AskOnce(host);

            if (asked is not null) {
                return asked;
            }
        }

        return null;
    }

    /// <summary>One question asked of the host, with the wait bounded so a stuck one cannot hold the screen.</summary>
    private static IReadOnlyList<DotnetFramework>? AskOnce(string host) {
        try {
            var start = new ProcessStartInfo(host) {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            start.ArgumentList.Add("--list-runtimes");

            using var process = Process.Start(start);
            if (process is null) {
                return null;
            }

            var listing = process.StandardOutput.ReadToEndAsync();

            // A host that will not answer must not hold the launcher's first screen up, so the wait is
            // bounded and a host that eats the whole of it is left behind.
            if (!listing.Wait(TimeSpan.FromSeconds(10))) {
                try {
                    process.Kill();
                }
                catch (Exception) {
                    // It may have finished between the two: nothing to do either way.
                }

                return null;
            }

            return ParseList(listing.Result);
        }
        catch (Exception) {
            return null;
        }
    }

    /// <summary>
    /// The frameworks a <c>--list-runtimes</c> listing names.
    /// </summary>
    /// <remarks>
    /// Each line is a name, a version and the folder it is in - and the folder contains spaces, so only the
    /// first two words are read. Anything that is not a name, a version is skipped rather than guessed at.
    /// </remarks>
    public static IReadOnlyList<DotnetFramework> ParseList(string listing) {
        var found = new List<DotnetFramework>();

        foreach (var line in listing.Split('\n', '\r')) {
            var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (words.Length >= 2 && words[0].Contains('.') && Version.TryParse(words[1], out _)) {
                found.Add(new DotnetFramework(words[0], words[1]));
            }
        }

        return found;
    }

    /// <summary>
    /// What the database server shipped by an Imlight install asks a host for, or nothing when it carries
    /// its own runtime.
    /// </summary>
    /// <remarks>
    /// Read from the file the install shipped rather than assumed. The two answers - a host with the
    /// frameworks named, or no host needed at all - are both decided there, and an Imlight that published
    /// the database self-contained would need nothing from this machine.
    /// </remarks>
    public static IReadOnlyList<DotnetFramework> RequiredBy(string ravenServerDirectory) {
        var path = RuntimeConfigPath(ravenServerDirectory);

        try {
            if (!File.Exists(path)) {
                return [];
            }

            var options = JsonNode.Parse(File.ReadAllText(path))?["runtimeOptions"];
            if (options is null) {
                return [];
            }

            // A self-contained build includes the framework it needs, and what it includes is not something
            // this machine has to have.
            if (options["includedFrameworks"] is JsonArray { Count: > 0 }) {
                return [];
            }

            var required = new List<DotnetFramework>();

            if (options["frameworks"] is JsonArray wanted) {
                foreach (var framework in wanted) {
                    var name = framework?["name"]?.GetValue<string>();
                    var version = framework?["version"]?.GetValue<string>();

                    if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(version)) {
                        required.Add(new DotnetFramework(name, version));
                    }
                }
            }

            return required;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                                      or InvalidOperationException or NotSupportedException or FormatException) {
            // A file that cannot be read asks for nothing, and the assembly being there at all is checked
            // separately: this is about what the database needs to run, not whether it exists.
            return [];
        }
    }

    /// <summary>The runtimeconfig beside the database server's own assembly.</summary>
    public static string RuntimeConfigPath(string ravenServerDirectory)
        => Path.ChangeExtension(ImlightConfig.RavenServerAssemblyFor(ravenServerDirectory), ".runtimeconfig.json");

    /// <summary>
    /// Which host to run a framework-dependent application with, or <c>null</c> when there is none.
    /// </summary>
    /// <param name="required">What the application asks for, from its own runtimeconfig file.</param>
    /// <remarks>
    /// <para>
    /// <b>The runtime shipped with the launcher is preferred, deliberately.</b> It is the one this build was
    /// tested against, it is the same on every machine, and - the whole point - it is there when the machine
    /// has nothing. A machine's own runtime is used when there is no bundle, or when the bundle's frameworks
    /// are not the ones being asked for: a database that has moved to a newer major than the shipped runtime
    /// is still better served by a machine that has it than by refusing to start.
    /// </para>
    /// <para>
    /// This is the one place the choice is made, so the launcher's first screen and the process that starts
    /// the database cannot disagree about it - the same reason the two used to share the machine-wide search.
    /// </para>
    /// </remarks>
    public static DotnetHostChoice? HostFor(IReadOnlyList<DotnetFramework> required) {
        var bundled = Candidate(FindBundled(), required, isBundled: true);

        // The bundle that fits - or that would not say - is the answer, and the machine is not even asked:
        // that the database runs the same way on every machine is what shipping a runtime is for.
        if (bundled is { CanRun: true }) {
            return bundled;
        }

        var machine = Candidate(Find(), required, isBundled: false);

        // A machine that fits beats a bundle that does not: better the runtime that is right for the job than
        // the one that happens to be in the folder.
        if (machine is { CanRun: true }) {
            return machine;
        }

        // Neither fits. The bundle is named when there is one, because it is the runtime that is sitting
        // right there and the one a user will otherwise not understand - and the machine-wide install is what
        // would fix it.
        return bundled ?? machine;
    }

    /// <summary>
    /// One of the two places a runtime can be, as a choice - or <c>null</c> when it is not there.
    /// </summary>
    /// <remarks>
    /// A host that would not say is given the benefit of the doubt rather than counted as short of
    /// something: the launcher is the one that can be wrong about a probe that timed out, and an application
    /// that genuinely cannot run says so itself.
    /// </remarks>
    private static DotnetHostChoice? Candidate(string? host, IReadOnlyList<DotnetFramework> required, bool isBundled) {
        if (host is not { Length: > 0 }) {
            return null;
        }

        var unmet = FrameworksOf(host) is { } offered ? Unmet(required, offered) : null;

        return new DotnetHostChoice(host, isBundled ? Path.GetDirectoryName(host) : null, isBundled) {
            Unmet = unmet,
        };
    }

    /// <summary>
    /// Points a start at a runtime of its own rather than at whatever this machine has installed.
    /// </summary>
    /// <remarks>
    /// <c>DOTNET_ROOT</c> is how the host is told where to resolve shared frameworks from, and the
    /// architecture-specific spelling of it is the one the host prefers - so both are set to the same folder,
    /// which is a whole .NET installation rather than a folder of assemblies.
    /// </remarks>
    public static void UseRuntimeRoot(ProcessStartInfo start, string runtimeRoot) {
        start.Environment["DOTNET_ROOT"] = runtimeRoot;
        start.Environment[RuntimeRootVariable()] = runtimeRoot;
    }

    /// <summary>The architecture-specific spelling of <c>DOTNET_ROOT</c> for this build.</summary>
    public static string RuntimeRootVariable() => RuntimeInformation.ProcessArchitecture switch {
        Architecture.X64 => "DOTNET_ROOT_X64",
        Architecture.X86 => "DOTNET_ROOT_X86",
        Architecture.Arm64 => "DOTNET_ROOT_ARM64",
        Architecture.Arm => "DOTNET_ROOT_ARM",
        _ => "DOTNET_ROOT",
    };

    /// <summary>
    /// The framework a host cannot offer, or null when it can offer all of them.
    /// </summary>
    /// <remarks>
    /// The rule the runtime itself uses: a framework-dependent application rolls forward to a higher
    /// version inside its own major and no further. So the same name and major with a version at least as
    /// high as the one asked for is a match, and a machine with a different major is a machine that cannot
    /// run it.
    /// </remarks>
    public static DotnetFramework? Unmet(
        IReadOnlyList<DotnetFramework> required, IReadOnlyList<DotnetFramework> installed)
        => required.FirstOrDefault(wanted => !installed.Any(have =>
            have.Name == wanted.Name && have.Major == wanted.Major && AtLeast(have.Version, wanted.Version)));

    private static bool AtLeast(string have, string wanted)
        => Version.TryParse(have, out var installed) && Version.TryParse(wanted, out var asked) && installed >= asked;
}
