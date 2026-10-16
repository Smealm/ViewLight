using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ViewLight.Services;

namespace ViewLight.ViewModels;

/// <summary>
/// The patch server question, asked once, on a fresh install, between the server being downloaded and
/// Home.
/// </summary>
/// <remarks>
/// <para>
/// Imlight is a thin client: it loads the game's files from a patch server as it runs, and it cannot boot
/// without one - so there is no "use a patch server or not" to ask, only <i>whose</i>. Left unasked, the
/// launcher's own default is a patch server on this machine and a fresh machine has none, which lands
/// every session on the public address without anybody choosing it. That is the state this screen exists
/// to end, because the two answers are not equivalent: a patch server here is what makes a session work
/// with no connection at all, while the public one stops the moment the game asks for something this
/// machine has never cached - a new zone, a creature, a single asset - and a session that has cached
/// nothing new can fail to start at all while that address is unreachable.
/// </para>
/// <para>
/// <b>Asked only where there is something to answer.</b> A machine that already has a patch server
/// installed has settled the question by installing one, so it is not asked; one that has been answered is
/// never asked again, because the answer is a setting - <see cref="UseLocalPatchServer"/> - that Settings
/// shows and can change at any time. What is remembered here is only that the question was put.
/// </para>
/// <para>
/// <b>Nothing is started from here.</b> The step installs a patch server if the local answer was chosen
/// and downloads nothing otherwise: starting it, and pointing Imlight at it, is what Play does, and the
/// first start is where its own fetch belongs - which is what Play then reports on, and the one place the
/// launcher watches a download it is not itself making.
/// </para>
/// </remarks>
public sealed partial class MainWindowViewModel {

    /// <summary>Whether the patch server question has been asked and answered on this install.</summary>
    public bool PatchServerChoiceAnswered {
        get => _store.Settings.PatchServerChoiceAnswered;
        set {
            if (_store.Settings.PatchServerChoiceAnswered == value) {
                return;
            }

            _store.Settings.PatchServerChoiceAnswered = value;
            _store.Save();

            OnPropertyChanged();
            RefreshDerived();
        }
    }

    /// <summary>
    /// Whether the launcher is stopped on the patch server question rather than showing itself.
    /// </summary>
    /// <remarks>
    /// After the other two first screens rather than instead of them: the folders have to be known before
    /// anything can be downloaded into them, and the install is what makes this a question about a stack
    /// that can work rather than about an empty folder. A machine that already has a patch server is not
    /// asked at all - installing one is an answer.
    /// </remarks>
    public bool RequiresPatchServerChoice
        => !RequiresSetup && !RequiresImlight && !PatchServerChoiceAnswered && !PatchServerInstalled;

    /// <summary>
    /// Whether the answer is enough to carry on with: the public patch server is a decision by itself,
    /// while this machine's own needs one actually here to be the patch server a session would be given.
    /// </summary>
    public bool CanCompletePatchServerSetup => UsesRemotePatchServer || PatchServerInstalled;

    /// <summary>
    /// The folder a patch server downloaded from this screen goes into: the one that is set, or the folder
    /// the launcher would put it in.
    /// </summary>
    /// <remarks>
    /// Shown rather than asked for, because this screen has no folder box and the answer is not in doubt -
    /// it is launcher-managed storage that travels with a portable folder, and Settings has the box for
    /// anyone who wants it somewhere else. What it is for is saying where several gigabytes are about to
    /// land before they do.
    /// </remarks>
    public string PatchServerSetupFolder => string.IsNullOrWhiteSpace(PatchServerPath)
        ? LauncherPaths.DefaultPatchServerPath
        : PatchServerPath;

    /// <summary>
    /// Records the answer and lets the launcher show itself.
    /// </summary>
    /// <remarks>
    /// The answer itself is written by the radio buttons - both sides of it are
    /// <see cref="UseLocalPatchServer"/> - so what is left here is saying so in the Log in the words of the
    /// thing that was decided, and the same update checks the folders screen leaves behind it.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanCompletePatchServerSetup))]
    private void CompletePatchServerSetup() {
        PatchServerChoiceAnswered = true;
        Section = LauncherSection.Home;

        AppendLog(LogLevel.Success, UseLocalPatchServer
            ? $"Patch server: this machine's own, at {LocalPatchServerUrl}. Play starts it before the server "
              + "and points Imlight at it, so the game's files come from here."
            : $"Patch server: the remote one — Imlight keeps {ImlightConfig.PublicPatchServerUrl} unless its "
              + "config says otherwise.");

        // Said on the way out because it is the one thing about the choice that is not visible from here:
        // what an empty patch server does on its first start is fetch a revision whole.
        if (PatchServerIsThisMachines) {
            AppendLog(LogLevel.Info,
                $"Its first start fetches the revision Wizard101 is serving into {PatchServerSetupFolder}, "
                + "which is thousands of files and tens of gigabytes. Play starts it and reports how far "
                + "along it is while it works; the ones your game already has can be copied in from Settings "
                + "if the download cannot finish them.");
        }

        if (_store.Settings.CheckForUpdatesOnStartup) {
            _ = CheckForUpdatesAsync();

            // The same as the folders screen does: a patch server that was just installed is one whose
            // newest release is worth knowing about before Home says anything about it.
            if (PatchServerInstalled) {
                _ = CheckForPatchServerUpdatesAsync();
            }
        }
    }

    private bool CanGetPatchServerForSetup() => UseLocalPatchServer && !IsPatchBusy;

    /// <summary>
    /// Downloads the patch server from the first-run screen.
    /// </summary>
    /// <remarks>
    /// Its own command rather than Settings' one, for the one difference between the two screens: this one
    /// is reached before any patch server folder has been chosen, and a download button that stays dead
    /// until a path is typed into a box this screen does not show is a first run that cannot be finished.
    /// The folder the launcher wants is filled in first and the download itself is Settings' own.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanGetPatchServerForSetup))]
    private async Task GetPatchServerForSetupAsync() {
        if (string.IsNullOrWhiteSpace(PatchServerPath)) {
            PatchServerPath = LauncherPaths.DefaultPatchServerPath;
            AppendLog(LogLevel.Info, $"Patch server folder set to {PatchServerPath}");
        }

        await DownloadPatchServerAsync().ConfigureAwait(true);
    }
}
