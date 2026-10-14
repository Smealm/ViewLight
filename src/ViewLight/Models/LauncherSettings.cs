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

    // ---------------------------------------------------------------- the patch server

    /// <summary>
    /// Folder the local patch server (Aurorium) is installed into. Empty until it has been downloaded,
    /// which is also how the launcher knows there is not one to run.
    /// </summary>
    public string PatchServerPath { get; set; } = string.Empty;

    /// <summary>Tag of the patch server release currently installed, if any.</summary>
    public string? PatchServerInstalledTag { get; set; }

    /// <summary>When it was written to disk.</summary>
    public DateTimeOffset? PatchServerInstalledAt { get; set; }

    /// <summary>Asset URL of the installed patch server (used to tell "same build" from "new build").</summary>
    public string? PatchServerInstalledAssetUrl { get; set; }

    /// <summary>
    /// Whether the patch server a session is given is this machine's own - the one in the folder below,
    /// run from here - rather than one somebody else runs.
    /// </summary>
    /// <remarks>
    /// On by default, because a local stack is what the launcher is for. It is deliberately not "whether to
    /// use a patch server": Imlight cannot boot without one, so the only thing being chosen is whose. Off
    /// means a remote address is the one in use and this machine's patch server is neither started nor
    /// pointed at, whatever else is configured here.
    /// </remarks>
    public bool UseLocalPatchServer { get; set; } = true;

    /// <summary>
    /// Whether the launcher runs the patch server: started with the server, stopped with the server. Only
    /// meaningful while <see cref="UseLocalPatchServer"/> is on.
    /// </summary>
    /// <remarks>
    /// On by default once one is installed, because a patch server that is not started before the server is
    /// exactly the case this exists to remove; it can be turned off so a patch server the user starts
    /// themselves is left alone.
    /// </remarks>
    public bool PatchServerManaged { get; set; } = true;

    /// <summary>
    /// Whether a patch server that holds nothing for the session's revision is filled in from the game
    /// folder on this machine without being asked.
    /// </summary>
    /// <remarks>
    /// Off by default: the copy is tens of gigabytes read and written, and when to spend that is the user's
    /// decision - which is what the offer before a session is for. On, the offer stops being a question and
    /// Play copies what it can, saying so in the Log.
    /// </remarks>
    public bool PatchServerPreloadAutomatically { get; set; }

    /// <summary>
    /// The port the patch server listens on, and the port Imlight is pointed at for it.
    /// </summary>
    /// <remarks>
    /// Kept in step with <see cref="Services.PatchServerConfig.DefaultPort"/> by a test, so the stock
    /// Imlight config's own <c>http://127.0.0.1:12369</c> is where a default install ends up.
    /// </remarks>
    public int PatchServerPort { get; set; } = 12369;

    /// <summary>
    /// Whether the launcher makes Imlight accept the revision the patch server holds, by writing that
    /// revision into <c>GameRevision</c>.
    /// </summary>
    /// <remarks>
    /// Off by default. A server build pins the revision it was published with, and a patch server can
    /// only ever hold what Wizard101 currently offers, so the two disagree the moment the game is patched
    /// past the server build - which shows up as a 404 during boot and a refused login. Turning this on
    /// is the deliberate act of saying the client that is here now is the one to accept.
    /// </remarks>
    public bool MirrorPatchServerRevision { get; set; }

    /// <summary>
    /// The <c>GameRevision</c> the config held when mirroring was turned on, so turning it off can put
    /// the release's own value back. <c>null</c> until the toggle has been used with a config to read.
    /// </summary>
    public string? RevisionBeforeMirror { get; set; }

    /// <summary>
    /// What the launcher last wrote into <c>GameRevision</c>, so a value that has moved on since - a
    /// newer server build, or an edit by hand - is not mistaken for the launcher's own to undo.
    /// </summary>
    public string? MirroredRevision { get; set; }

    /// <summary>
    /// The Wizard101 install the patch server's files are copied from, or empty to use whichever install on
    /// this machine is the revision the patch server holds.
    /// </summary>
    public string PatchServerPreloadSource { get; set; } = string.Empty;

    /// <summary>
    /// The revision whose preload offer was turned down with "don't ask again".
    /// </summary>
    /// <remarks>
    /// A revision rather than a switch, because a new revision is a new question: the files that cannot be
    /// downloaded at all are per-revision, and somebody who said no to one has said nothing about the next.
    /// </remarks>
    public string? PatchServerPreloadDeclinedRevision { get; set; }

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
