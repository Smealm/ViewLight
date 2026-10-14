namespace ViewLight.Services;

/// <summary>Which piece of what the launcher needs is missing.</summary>
public enum ImlightGap {

    /// <summary>Nothing: the launcher can do everything it does.</summary>
    None,

    /// <summary>No folder to look in, because none has been chosen yet.</summary>
    NoFolder,

    /// <summary>A folder with no Imlight in it.</summary>
    NoInstall,

    /// <summary>An install without the server's own executable.</summary>
    NoExecutable,

    /// <summary>An install without the RavenDB the launcher's own data lives in.</summary>
    NoDatabase,

    /// <summary>No .NET runtime on this machine to run that database with.</summary>
    NoDotnet,

    /// <summary>A .NET runtime without the framework the database asks it for.</summary>
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
/// framework-dependent application and the install is self-contained - a machine-wide .NET runtime able to
/// run it. What that runtime has to offer is read from the database's own
/// <c>Raven.Server.runtimeconfig.json</c> by <see cref="DotnetHost"/>, so an Imlight that ever ships the
/// database self-contained would stop asking for anything here.
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
    /// </remarks>
    public const string DotnetGuide =
        "1. Press Get the .NET runtime - it opens the page for the version the database asks for.\n"
        + "2. Take ASP.NET Core Runtime, Windows, x64, and run the installer. That one brings the .NET "
        + "runtime and the framework the database asks it for; the SDK is not needed.\n"
        + "3. Come back here and press Check again. Nothing has to be restarted, and nothing else has to be "
        + "installed.";

    /// <summary>What is missing from a launcher pointed at this folder, or <see cref="ImlightRequirement.Met"/>.</summary>
    public static ImlightRequirement For(string? serverPath) {
        if (string.IsNullOrWhiteSpace(serverPath)) {
            return new ImlightRequirement(ImlightGap.NoFolder,
                "No server folder is set, so there is no Imlight to run. Choose the folder Imlight is in, "
                + "or download it.");
        }

        var config = ImlightConfig.TryLoad(serverPath);

        if (config is null) {
            return new ImlightRequirement(ImlightGap.NoInstall,
                $"{serverPath} does not hold an Imlight install: {ImlightConfig.RelativeConfigPath} is missing "
                + "or cannot be read. Point ViewLight at the folder Imlight is in, or download it.");
        }

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
            // The database carries its own runtime: there is nothing for this machine to provide.
            return ImlightRequirement.Met;
        }

        var host = DotnetHost.Find();

        if (host is null) {
            // Every framework it asks for, not just the first: a machine about to install a runtime should
            // be told what the database needs rather than half of what it needs.
            return new ImlightRequirement(ImlightGap.NoDotnet,
                "ViewLight itself starts without .NET - it is the RavenDB the server ships that needs a "
                + "runtime, and this machine has none. The database asks for "
                + $"{string.Join(" and ", required)}.",
                DotnetDownloadUrl(required[0].Major), "Get the .NET runtime", DotnetGuide);
        }

        var unmet = DotnetHost.Unmet(required, DotnetHost.FrameworksOf(host));

        if (unmet is not null) {
            return new ImlightRequirement(ImlightGap.NoRuntime,
                $"The .NET runtime at {host} does not have {unmet}, which the RavenDB the server ships asks "
                + "it for, so the launcher's own data has no database to live in.",
                DotnetDownloadUrl(unmet.Major), "Get the .NET runtime", DotnetGuide);
        }

        return ImlightRequirement.Met;
    }

    /// <summary>Where to get the runtime a database of this major version needs.</summary>
    public static string DotnetDownloadUrl(int major)
        => $"https://dotnet.microsoft.com/download/dotnet/{Math.Max(major, 8)}.0";
}
