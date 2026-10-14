using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ViewLight.Services;

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
/// the server and nothing the database can borrow - so running it is a machine-wide .NET install's job.
/// This is where that install is looked for, asked what it has, and what the database asks it for is read
/// from the database's own file rather than assumed.
/// </para>
/// <para>
/// The two lookups are properties so that a test can decide the answer: whether a machine has a host, and
/// what that host reports, are the two facts the launcher's first screen turns on.
/// </para>
/// </remarks>
public static class DotnetHost {

    private static readonly Dictionary<string, IReadOnlyList<DotnetFramework>> s_asked = [];
    private static readonly Lock s_gate = new();

    /// <summary>The dotnet executable this machine has, or null when it has none.</summary>
    public static Func<string?> Find { get; set; } = FindInstalled;

    /// <summary>What a host says it has, or nothing when it cannot be asked.</summary>
    public static Func<string, IReadOnlyList<DotnetFramework>> ListFrameworks { get; set; } = Ask;

    /// <summary>What a host offers, asked once and remembered for the run.</summary>
    /// <remarks>
    /// Asked once because it costs a process launch (measured at about 60ms, which is why the answer is
    /// waited for on the way in rather than made asynchronous: the first screen has to know before it
    /// decides whether to show itself), and because it cannot change while the launcher runs - unless
    /// somebody installs a runtime and presses Check again, which is what <see cref="Forget"/> is for.
    /// </remarks>
    public static IReadOnlyList<DotnetFramework> FrameworksOf(string host) {
        lock (s_gate) {
            if (s_asked.TryGetValue(host, out var known)) {
                return known;
            }
        }

        // Outside the lock: this is the call that starts a process, and two screens asking at once is not a
        // reason for one of them to wait on it holding everybody else up.
        var asked = ListFrameworks(host);

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

    /// <summary>A host asked what it has, or nothing when it will not say.</summary>
    private static IReadOnlyList<DotnetFramework> Ask(string host) {
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
                return [];
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

                return [];
            }

            return ParseList(listing.Result);
        }
        catch (Exception) {
            return [];
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
