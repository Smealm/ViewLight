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

/// <summary>
/// How the launcher deals with the revisions a patch server has fetched.
/// </summary>
/// <remarks>
/// Spelled as strings rather than as an enum, because this is written into the settings file and a value a
/// later build no longer recognises must fall back rather than fail to load - see <see cref="ThemeChoices"/>,
/// which is here for the same reason.
/// </remarks>
public static class PatchServerCleanupChoices {

    /// <summary>
    /// The launcher keeps the revision that is in use - the newest the patch server has, which is the one
    /// the server is kept in step with - and deletes every older one. The recommended answer, and the one a
    /// launcher that has never been asked starts on.
    /// </summary>
    public const string Automatic = "Automatic";

    /// <summary>
    /// The launcher deletes nothing on its own: the revisions on disk are listed in Settings where they can
    /// be kept or deleted one by one.
    /// </summary>
    public const string Manual = "Manual";

    /// <summary>Every choice, in the order the radio buttons list them.</summary>
    public static IReadOnlyList<string> All { get; } = [Automatic, Manual];
}

/// <summary>
/// Which Imlight builds the launcher installs.
/// </summary>
/// <remarks>
/// <para>
/// Spelled as strings rather than as an enum for the same reason as <see cref="ThemeChoices"/>: this is
/// written into the settings file, and a value a later build no longer recognises must fall back rather
/// than fail to load.
/// </para>
/// <para>
/// The first two are release channels - GitHub builds somebody else made. The third is not a build at
/// all: <see cref="BleedingEdge"/> is the project's current source, cloned, built on this machine, and
/// installed from the result. It exists for the case neither release channel can serve - a fix or a
/// feature that has been committed and not yet built - and it is the one option that can fail on a
/// machine that cannot build .NET projects.
/// </para>
/// </remarks>
public static class ServerChannelChoices {

    /// <summary>A build published as a GitHub release: what Imlight calls stable.</summary>
    public const string Stable = "Stable";

    /// <summary>A build published as a GitHub <i>prerelease</i>, which is every Imlight build so far.</summary>
    public const string Prerelease = "Prerelease";

    /// <summary>
    /// The project's current source, built here. Not a published build at all, and the only channel that
    /// needs git and the .NET SDK on this machine.
    /// </summary>
    public const string BleedingEdge = "BleedingEdge";

    /// <summary>Every choice, in the order the radio buttons list them.</summary>
    public static IReadOnlyList<string> All { get; } = [Stable, Prerelease, BleedingEdge];

    /// <summary>The channel a stored value names, falling back to <see cref="Prerelease"/>.</summary>
    /// <remarks>
    /// Prerelease rather than Stable is the fallback because that is what every Imlight build is today:
    /// a launcher that has never been asked, or one whose settings name a channel this build does not
    /// know, must still be able to find a server. Choosing Stable before there is one is a deliberate
    /// act, and it is answered with the sentence that says so rather than silence.
    /// </remarks>
    public static string Parse(string? stored)
        => stored is Stable or BleedingEdge ? stored : Prerelease;
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

    /// <summary>
    /// The install the game in the launcher's own folder was copied in from, for portable mode.
    /// </summary>
    /// <remarks>
    /// Remembered because it is the only thing the copy can be compared against: the launcher's own folder holds
    /// a complete game, so nothing about it says whether the machine's install has moved on since. Written when
    /// a copy finishes and read when the launcher asks whether the copy is still the build this machine has;
    /// stored through the launcher's own path rules, so a portable folder carried elsewhere keeps it.
    /// </remarks>
    public string PortableGameSourcePath { get; set; } = string.Empty;

    /// <summary>Folder the Imlight server is installed into.</summary>
    public string ServerPath { get; set; } = string.Empty;

    /// <summary>Tag of the release currently installed, if any.</summary>
    public string? InstalledTag { get; set; }

    /// <summary>When the installed release was written to disk.</summary>
    public DateTimeOffset? InstalledAt { get; set; }

    /// <summary>Asset URL of the installed release (used to tell "same build" from "new build").</summary>
    public string? InstalledAssetUrl { get; set; }

    /// <summary>
    /// Which channel <see cref="InstalledTag"/> came from, so a build installed from source is not
    /// mistaken for one of the same name published on GitHub.
    /// </summary>
    public string InstalledChannel { get; set; } = ServerChannelChoices.Prerelease;

    /// <summary>
    /// The commit a bleeding-edge build was made from, or <c>null</c> for a published build.
    /// </summary>
    /// <remarks>
    /// The tag alone is not enough to answer "is there newer source?": an edge tag carries only the
    /// short commit, and the launcher needs the full one to compare against the checkout's HEAD. It is
    /// also what says whether the build in the folder is still the one the local source describes.
    /// </remarks>
    public string? InstalledEdgeCommit { get; set; }

    /// <summary>Which builds the launcher installs: see <see cref="ServerChannelChoices"/>.</summary>
    public string ServerChannel { get; set; } = ServerChannelChoices.Prerelease;

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
    /// The port the patch server listens on, and the port Imlight is pointed at for it.
    /// </summary>
    /// <remarks>
    /// Kept in step with <see cref="Services.PatchServerConfig.DefaultPort"/> by a test, so the stock
    /// Imlight config's own <c>http://127.0.0.1:12369</c> is where a default install ends up.
    /// </remarks>
    public int PatchServerPort { get; set; } = 12369;

    /// <summary>
    /// Whether the launcher keeps the revision the server accepts clients on in step with the revision
    /// the patch server actually holds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// On by default, and it is the arrangement the whole stack wants: a patch server fetches whatever
    /// revision Wizard101 is serving and nothing can ask it for a different one, so the value that has to
    /// move is the server's. Imlight asks its patch server for <c>V_{GameRevision}</c> and accepts clients
    /// on that same revision, so writing the patch server's revision into <c>GameRevision</c> is what makes
    /// the server's own files reachable <i>and</i> what lets the client that just updated get in - both
    /// values say the same thing, which is the only state either of them works in.
    /// </para>
    /// <para>
    /// Off leaves <c>GameRevision</c> exactly as the server's own config has it, pinned the way Imlight was
    /// published. That is right for a server whose build is deliberately held at one revision, and it means
    /// the patch server is the side left needing something it cannot fetch: the launcher can hand it that
    /// revision's files, and only when this machine's client build is exactly that revision with no file of
    /// it at the wrong length. See <see cref="Services.PatchServerPreload"/>.
    /// </para>
    /// </remarks>
    public bool PatchServerRevisionSynced { get; set; } = true;

    /// <summary>
    /// The <c>GameRevision</c> the config held before the launcher set it from the patch server, so the
    /// launcher's own writing can be undone. <c>null</c> until it has written one.
    /// </summary>
    /// <remarks>
    /// Only used by the sync above. A launcher that never ran it leaves this alone, and switching the sync
    /// off uses it to put back the value the release pinned its config to.
    /// </remarks>
    public string? RevisionBeforeMirror { get; set; }

    /// <summary>
    /// What the launcher last wrote into <c>GameRevision</c>, so a value that has moved on since - a
    /// newer server build, or an edit by hand - is not mistaken for the launcher's own to undo.
    /// </summary>
    public string? MirroredRevision { get; set; }

    /// <summary>
    /// How the revisions the patch server has fetched are looked after: <see cref="PatchServerCleanupChoices.Automatic"/>
    /// or <see cref="PatchServerCleanupChoices.Manual"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A revision of this game is around 18 GB, and a patch server fetches a new one whenever Wizard101
    /// publishes one - so left alone, the folder only ever grows. Automatic keeps the revision that is in
    /// use - the newest one, which is the one the server's own revision is kept in step with - and deletes
    /// the older ones; manual leaves every one of them alone for the user to manage.
    /// </para>
    /// <para>
    /// Automatic is the recommended answer and the default, and choosing it <i>is</i> the permission the
    /// launcher needs to act on the user's behalf: it is stated before anything is downloaded, changeable
    /// at any time, and every deletion is written to the Log with what was deleted and why. Manual is there
    /// for a data hoarder, or for somebody running a server arrangement where a second revision is the
    /// point.
    /// </para>
    /// </remarks>
    public string PatchServerRevisionCleanup { get; set; } = PatchServerCleanupChoices.Automatic;

    /// <summary>
    /// The Wizard101 install the patch server's files are copied from, or empty to use whichever install on
    /// this machine is the revision the patch server holds.
    /// </summary>
    /// <remarks>
    /// Used by the button in Settings and by nothing else: Play does not copy anything into a patch server,
    /// because a patch server is expected to fetch its own revision and the launcher copying files into it
    /// is a shortcut around the workflow the patch server was built around. It is kept because it is the
    /// only route there is for the files Aurorium gives up on - they run past its own download limit - and
    /// pressing it is a deliberate act rather than something Play does on the way past.
    /// </remarks>
    public string PatchServerPreloadSource { get; set; } = string.Empty;

    /// <summary>
    /// Whether the launcher has asked whose patch server to use and been answered.
    /// </summary>
    /// <remarks>
    /// Asked once, on a fresh install, between the server being downloaded and Home. It is a separate
    /// setting from <see cref="UseLocalPatchServer"/> because the two are different things: that one is
    /// which patch server is in use, and this is whether anybody was ever asked. Without it a fresh
    /// machine keeps the default below, has nothing installed to answer with, and quietly ends up on the
    /// public patch server - which is the state this exists to end.
    /// </remarks>
    public bool PatchServerChoiceAnswered { get; set; }

    // ---------------------------------------------------------------- the cloud backup

    /// <summary>
    /// Whether the launcher keeps a copy of the databases that matter in the user's cloud drive, and restores
    /// them when they have gone.
    /// </summary>
    /// <remarks>
    /// Off by default: it is the user's Drive being written to, and it is only ever turned on from Settings.
    /// On, the launcher syncs while it is idle - signing in is what authorises it, and nothing is uploaded or
    /// restored before then.
    /// </remarks>
    public bool BackUpToCloud { get; set; }

    /// <summary>
    /// Whether the sign-in uses ViewLight's own Google client rather than one the user registered.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Off by default, which is the <i>bring your own</i> side: a Desktop-app OAuth client the user registers
    /// themselves, with its id and secret pasted in below. On, the launcher uses
    /// <see cref="Services.GoogleOAuth.BuiltInClientId"/> - a client id only, because the PKCE verifier stands
    /// in for a secret and a secret shipped in an application is not a secret - so signing in needs no setup
    /// beyond pressing the button.
    /// </para>
    /// <para>
    /// The built-in side is a choice that exists and works and is deliberately not offered until this build
    /// has a client to name: <see cref="Services.GoogleOAuth.HasBuiltInClient"/> is what the Settings screen
    /// greys it out on, and it is the only thing standing between the workflow that is written and a sign-in
    /// that needs no setup at all.
    /// </para>
    /// </remarks>
    public bool CloudBackupUsesBuiltInClient { get; set; }

    /// <summary>
    /// The user's own Google OAuth client id, for the <i>bring your own</i> workflow.
    /// </summary>
    /// <remarks>
    /// Only read while <see cref="CloudBackupUsesBuiltInClient"/> is off. A Desktop-app OAuth client is
    /// registered once and its id pasted here; see the readme for the two minutes that takes.
    /// </remarks>
    public string CloudBackupClientId { get; set; } = string.Empty;

    /// <summary>
    /// That client's secret, when it has one.
    /// </summary>
    /// <remarks>
    /// A desktop client that sends a PKCE verifier does not need a secret, and Google's own guidance says a
    /// secret shipped in an installed application is not secret - so this may be left blank, and blank means
    /// no secret is sent rather than an empty one.
    /// </remarks>
    public string CloudBackupClientSecret { get; set; } = string.Empty;

    /// <summary>
    /// Which theme to use: <see cref="ThemeChoices.System"/>, Light or Dark. Stored as text so a value
    /// written by a future version does not fail to load.
    /// </summary>
    public string Theme { get; set; } = ThemeChoices.System;

    /// <summary>
    /// Whether the explanation under each control is shown.
    /// </summary>
    /// <remarks>
    /// Off by default, because a launcher is used rather than read: the sentence under a setting is worth
    /// having the first time and is in the way every time after that. It is kept as a preference rather
    /// than a hover tip so a person who wants the launcher to explain itself can have all of it at once,
    /// and one who does not is never shown any of it.
    /// </remarks>
    public bool VerboseMode { get; set; }

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
