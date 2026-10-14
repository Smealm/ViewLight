namespace ViewLight.Models;

/// <summary>
/// The names of the launcher's navigation sections, as they are written into the settings file.
/// </summary>
/// <remarks>
/// Spelled here rather than taken from the view model's enum so the model does not depend on the view
/// model - the settings file outlives any given screen, and a value it no longer recognises simply falls
/// back to <see cref="Home"/>. Kept in step with <c>LauncherSection</c> by a test.
/// </remarks>
public static class LauncherSections {

    public const string Home = "Home";
    public const string Profile = "Profile";
    public const string Settings = "Settings";
    public const string Logs = "Logs";
    public const string About = "About";

    /// <summary>Every section, in the order the sidebar lists them, Home first.</summary>
    public static IReadOnlyList<string> All { get; } = [Home, Profile, Settings, Logs, About];
}

/// <summary>The themes the launcher offers.</summary>
public static class ThemeChoices {

    /// <summary>Follow the desktop, which is the default because it is the least surprising.</summary>
    public const string System = "System";

    public const string Light = "Light";

    public const string Dark = "Dark";
}

/// <summary>
/// Everything the launcher persists about itself. Deliberately tiny: all *server* settings live in
/// the server's own <c>Config/Imlight.ini</c>, which stays the single source of truth.
/// </summary>
public sealed class LauncherSettings {

    /// <summary>Folder containing the Wizard101 client (the one with <c>Root.wad</c>).</summary>
    public string ClientPath { get; set; } = string.Empty;

    /// <summary>Folder the Imlight server is installed into.</summary>
    public string ServerPath { get; set; } = string.Empty;

    /// <summary>Tag of the release currently installed, if any.</summary>
    public string? InstalledTag { get; set; }

    /// <summary>When the installed release was written to disk.</summary>
    public DateTimeOffset? InstalledAt { get; set; }

    /// <summary>Asset URL of the installed release (used to tell "same build" from "new build").</summary>
    public string? InstalledAssetUrl { get; set; }

    /// <summary>Check GitHub for a newer build when the launcher starts.</summary>
    public bool CheckForUpdatesOnStartup { get; set; } = true;

    /// <summary>
    /// Which theme to use: <see cref="ThemeChoices.System"/>, Light or Dark. Stored as text so a value
    /// written by a future version does not fail to load.
    /// </summary>
    public string Theme { get; set; } = ThemeChoices.System;

    /// <summary>
    /// Ask before closing while a server the launcher started is running, because closing stops it.
    /// Turned off by the close dialog's "Don't ask me again", and back on in Settings.
    /// </summary>
    public bool ConfirmCloseWhenServerRunning { get; set; } = true;

    /// <summary>
    /// Whether a milestone is announced in a toast when it is earned.
    /// </summary>
    /// <remarks>
    /// On by default, because being told is the point of a milestone for most players. It exists to be
    /// turned off because a milestone is a rung of a ladder: somebody levelling a wizard to 200, or farming
    /// duels, earns dozens of them in a session and a card over the game every few minutes is noise rather
    /// than news. Turning it off stops the announcement only - the badge is still earned, still worth its
    /// points, and still on the profile.
    /// </remarks>
    public bool ShowMilestoneToasts { get; set; } = true;

    /// <summary>
    /// Which navigation section the launcher opens on.
    /// </summary>
    /// <remarks>
    /// A choice rather than a memory, and Home unless it is changed: opening on whatever page was last
    /// looked at means a launcher that was left on Settings comes up on Settings, which is never what
    /// starting the launcher is for. The everyday act is playing the game, so that is what the launcher
    /// lands on unless it is told otherwise under Settings.
    /// </remarks>
    public string DefaultSection { get; set; } = LauncherSections.Home;

    /// <summary>
    /// The address of the last server the player joined, so the join box is filled in with it rather
    /// than empty. Blank until they have joined one.
    /// </summary>
    public string LastRemoteHost { get; set; } = string.Empty;

    /// <summary>The port that goes with <see cref="LastRemoteHost"/>.</summary>
    public int LastRemotePort { get; set; } = ServerEndpoint.DefaultPort;

    /// <summary>
    /// Whether the last official-server sign-in used a Steam account. Remembered because it is a
    /// property of the player's Wizard101 account rather than of the server, and having to answer it
    /// again every time is exactly the confusion the choice exists to avoid.
    /// </summary>
    public bool OfficialUsesSteam { get; set; }

    /// <summary>
    /// The account Play signs in as, if the player has picked one. Held as the account id because that
    /// is what the client is signed in with; the username is only ever presented.
    /// </summary>
    public ulong? AutoLoginAccountId { get; set; }

    /// <summary>
    /// This machine's id, as the server records it against a session key. Learned once from a session
    /// key that appeared while the player was signing in themselves, and remembered because it is what
    /// says whether a stored key can be used here.
    /// </summary>
    public ulong? MachineId { get; set; }

    /// <summary>
    /// The accounts the player database held when it was last read.
    /// </summary>
    /// <remarks>
    /// The database only answers while the server is running, which made the account picker empty on
    /// every launcher start until Play had been pressed once - and an empty picker reads as the
    /// launcher having forgotten who you sign in as. This is the last known answer, so the choice is
    /// still there on opening. It is only ever a display and a default: signing in still re-reads the
    /// database, because only that says whether the remembered key is still good.
    /// </remarks>
    public List<PlayerAccount> CachedAccounts { get; set; } = [];

    /// <summary>
    /// The server folder <see cref="CachedAccounts"/> was read from.
    /// </summary>
    /// <remarks>
    /// An account belongs to a server rather than to the launcher, so the remembered list is only ever
    /// shown when the launcher is pointed back at the server it came from. Without this, a portable
    /// launcher that moved - or one pointed at a different server - came up offering accounts this server
    /// has never heard of, which is how a name nobody here had ever created appeared to exist.
    /// </remarks>
    public string CachedAccountsServerPath { get; set; } = string.Empty;
}
