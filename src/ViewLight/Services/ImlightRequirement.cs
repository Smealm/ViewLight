namespace ViewLight.Services;

/// <summary>Which piece of what the launcher needs is missing.</summary>
public enum ImlightGap {

    /// <summary>Nothing: the launcher can do everything it does.</summary>
    None,

    /// <summary>No folder to look in, because none has been chosen yet.</summary>
    NoFolder,

    /// <summary>A folder with no Imlight in it.</summary>
    NoInstall,

    /// <summary>
    /// A config that is there and could not be read: something else had it open for the moment, or this
    /// account cannot read it. Deliberately not the same gap as <see cref="NoInstall"/>, which is what a
    /// read that failed was reported as - and an install that is plainly in the folder being announced as
    /// missing is the bug this gap exists to end.
    /// </summary>
    ConfigUnreadable,

    /// <summary>An install without the server's own executable.</summary>
    NoExecutable,

    /// <summary>An install without the RavenDB the launcher's own data lives in.</summary>
    NoDatabase,

    /// <summary>No .NET runtime anywhere to run that database with: none beside the launcher, none on the machine.</summary>
    NoDotnet,

    /// <summary>
    /// A .NET runtime without the framework the database asks it for - the one the launcher ships with,
    /// when it has one, and the machine's when it does not.
    /// </summary>
    NoRuntime,
}

/// <summary>What the launcher needs an Imlight install for, and what this machine is missing.</summary>
/// <param name="Gap">Which piece is missing, for anything that has to react rather than explain.</param>
/// <param name="Missing">The sentence the first screen says: which piece, and what it is for.</param>
/// <param name="ActionUrl">Where to go about it, when there is somewhere to go.</param>
/// <param name="ActionLabel">What that link is called.</param>
/// <param name="Guide">How to fix it in steps, when the link alone does not say what to do.</param>
public sealed record ImlightRequirement(
    ImlightGap Gap,
    string Missing,
    string? ActionUrl = null,
    string? ActionLabel = null,
    string? Guide = null) {

    /// <summary>Everything the launcher needs is here.</summary>
    public static ImlightRequirement Met { get; } = new(ImlightGap.None, string.Empty);

    public bool IsMet => Gap == ImlightGap.None;
}

/// <summary>
/// Imlight is a requirement of this launcher rather than an optional extra, and this is the check that
/// says so.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it is a requirement.</b> ViewLight is a launcher for Imlight: it installs it, configures it,
/// runs it, and plays it - so a launcher with no Imlight has nothing to configure and nothing to start.
/// The second half is newer: the launcher's own notes about an account - its profiles, playtime, sessions
/// and badges - live in the RavenDB an Imlight install ships, so the install is also where the launcher's
/// own data is kept. Both halves mean the same thing for the user: until there is an Imlight to point at,
/// there is nothing this launcher can correctly do, and saying so is more honest than a Home screen whose
/// only button downloads one.
/// </para>
/// <para>
/// <b>What it checks, in the order it has to be fixed.</b> A folder; an install in it; the server's own
/// executable; the RavenDB the launcher's data lives in; and - because that database is a
/// framework-dependent application and the install is self-contained - a .NET runtime able to run it. That
/// runtime is the one the launcher ships with, and the machine's install is what is used when there is no
/// bundle or the bundle is not what the database asked for; <see cref="DotnetHost.HostFor"/> is the one
/// place that choice is made. What the database asks a runtime for is read from its own
/// <c>Raven.Server.runtimeconfig.json</c>, so an Imlight that ever ships the database self-contained would
/// stop asking for anything here.
/// </para>
/// </remarks>
public static class ImlightRequirements {

    /// <summary>
    /// How to install the runtime, in the steps somebody actually has to take.
    /// </summary>
    /// <remarks>
    /// The link is a version's download page rather than an installer, and nothing on that page says which
    /// of its several downloads this launcher wants - so the screen says it. The last step is there because
    /// it is true: the runtime is noticed when the launcher is asked again, not on the next start.
    /// <para>
    /// The first line says where the runtime being asked for goes - this machine - because that is no longer
    /// the only place one can be: a released build carries its own, and the question this screen answers is
    /// what to do when there is nothing that can run the database, not where .NET normally comes from.
    /// </para>
    /// </remarks>
    public const string DotnetGuide =
        "A released ViewLight brings a runtime of its own for this database, so this screen means there is "
        + "none it can use - and the one to install is here, on this machine.\n"
        + "1. Press Get the .NET runtime - it opens the page for the version the database asks for.\n"
        + "2. Take ASP.NET Core Runtime, Windows, x64, and run the installer. That one brings the .NET "
        + "runtime and the framework the database asks it for; the SDK is not needed.\n"
        + "3. Come back here and press Check again. Nothing has to be restarted, and nothing else has to be "
        + "installed.";

    /// <summary>What is missing from a launcher pointed at this folder, or <see cref="ImlightRequirement.Met"/>.</summary>
    public static ImlightRequirement For(string? serverPath) => For(serverPath, ImlightConfig.Read(serverPath));

    /// <summary>
    /// The same, from a config that has already been read.
    /// </summary>
    /// <remarks>
    /// The launcher reads the config to load its own settings and then asks this what is missing from the same
    /// folder - two reads of one file, and when the file is not there each of them spends the read window
    /// waiting for it. On a first run that is the whole of the wait, twice, before the first screen is drawn.
    /// </remarks>
    public static ImlightRequirement For(string? serverPath, ImlightConfigRead read) {
        if (string.IsNullOrWhiteSpace(serverPath)) {
            return new ImlightRequirement(ImlightGap.NoFolder,
                "No server folder is set, so there is no Imlight to run. Choose the folder Imlight is in, "
                + "or download it.");
        }

        if (read.Config is null) {
            // A config that is there and would not read is not an install that is gone. It is a file an install
            // was unpacking, or the server itself was writing, at the moment the launcher looked - and saying
            // "no server installed here yet" about it sends somebody to download a server they already have.
            return read.FileIsThere
                ? new ImlightRequirement(ImlightGap.ConfigUnreadable,
                    $"{ImlightConfig.ConfigPathFor(serverPath)} is there, but it could not be read just now: "
                    + "something else has it open for the moment. Nothing else about the install is in "
                    + "question. Press Check again.")
                : new ImlightRequirement(ImlightGap.NoInstall,
                    $"{serverPath} does not hold an Imlight install: {ImlightConfig.RelativeConfigPath} is "
                    + "missing. Point ViewLight at the folder Imlight is in, or download it.");
        }

        var config = read.Config;

        if (!File.Exists(ImlightConfig.ExecutablePathFor(serverPath))) {
            return new ImlightRequirement(ImlightGap.NoExecutable,
                $"{ImlightConfig.ServerExecutableName} is not in {serverPath}, so the server itself cannot be "
                + "started. Download it again, or point ViewLight at another folder.");
        }

        var ravenDirectory = config.ResolveRavenServerDirectory(serverPath);

        if (!File.Exists(ImlightConfig.RavenServerAssemblyFor(ravenDirectory))) {
            return new ImlightRequirement(ImlightGap.NoDatabase,
                $"The RavenDB Imlight ships is not in {ravenDirectory}. ViewLight keeps its own profiles, "
                + "playtime and badges in it, so they cannot be kept at all without it. Download the server "
                + "again to replace it.");
        }

        var required = DotnetHost.RequiredBy(ravenDirectory);

        if (required.Count == 0) {
            // The database carries its own runtime: there is nothing for anything here to provide.
            return ImlightRequirement.Met;
        }

        var host = DotnetHost.HostFor(required);

        if (host is null) {
            // Every framework it asks for, not just the first: something about to be installed should be told
            // what the database needs rather than half of what it needs. Both places are named because both
            // were looked in, and which of them to fill is the user's to decide.
            return new ImlightRequirement(ImlightGap.NoDotnet,
                "ViewLight itself starts without .NET - it is the RavenDB the server ships that asks for a "
                + "shared runtime, and there is none to give it: this build ships none beside the launcher, "
                + $"and this machine has none installed. The database asks for {string.Join(" and ", required)}.",
                DotnetDownloadUrl(required[0].Major), "Get the .NET runtime", DotnetGuide);
        }

        // A host that would not say is not a host without the framework, so it is not reported as one: the
        // launcher is the one that can be wrong here - a machine busy enough to time a probe out - and a
        // database that genuinely cannot run says so itself, with the port and the exit code in the Log,
        // rather than by way of a screen telling somebody to install a runtime they already have.
        if (host.Unmet is { } unmet) {
            // Which runtime is named, because it matters to what a user does about it: the one in the
            // launcher's own folder is not something an installer will change, and a runtime they cannot see
            // being wrong is where a machine-wide install becomes the answer.
            var where = host.IsBundled
                ? "The .NET runtime ViewLight ships with"
                : $"The .NET runtime at {host.Host}";

            var fallback = host.IsBundled
                ? " Installing one that has it gives the launcher a machine-wide runtime to use instead."
                : string.Empty;

            return new ImlightRequirement(ImlightGap.NoRuntime,
                $"{where} does not have {unmet}, which the RavenDB the server ships asks it for, so the "
                + "launcher's own data has no database to live in." + fallback,
                DotnetDownloadUrl(unmet.Major), "Get the .NET runtime", DotnetGuide);
        }

        return ImlightRequirement.Met;
    }

    /// <summary>Where to get the runtime a database of this major version needs.</summary>
    public static string DotnetDownloadUrl(int major)
        => $"https://dotnet.microsoft.com/download/dotnet/{Math.Max(major, 8)}.0";
}
