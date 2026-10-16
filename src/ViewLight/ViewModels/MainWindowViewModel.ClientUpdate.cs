using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ViewLight.Services;

namespace ViewLight.ViewModels;

/// <summary>
/// The game in the launcher's own folder, and whether it is still the build the machine has.
/// </summary>
/// <remarks>
/// <para>
/// <b>Portable mode makes a copy once, and copies do not patch themselves.</b> The launcher plays the game out
/// of its own folder, so a Steam update or a reinstall elsewhere leaves it playing a build the machine has
/// moved past - and nothing else in the launcher would ever say so, because what is in that folder is a
/// complete, valid game. So the comparison is made when the launcher starts (see
/// <see cref="PortableClientUpdate"/>), and the answer is a card that offers to close the gap.
/// </para>
/// <para>
/// <b>Two ways to close it, chosen rather than decided.</b> Writing the whole game again is the simplest thing
/// that cannot be wrong; reading both copies and writing only what differs costs processor time instead of
/// writes. The launcher cannot know which of those this machine would rather spend, so the card offers both and
/// remembers nothing: the choice belongs to the person making it, at the moment they make it.
/// </para>
/// <para>
/// <b>Nothing is updated without being asked.</b> The check is automatic and the update is not: a multi-gigabyte
/// write is not something a launcher does to somebody's disk on a start.
/// </para>
/// </remarks>
public sealed partial class MainWindowViewModel {

    /// <summary>The update that runs when one is asked for, and the one the card is showing.</summary>
    private ClientUpdateMethod _portableUpdateMethod = ClientUpdateMethod.Mirror;

    /// <summary>What the last check found. Nothing to say until one has run.</summary>
    private PortableClientUpdateCheck _portableUpdate =
        PortableClientUpdateCheck.NothingToSay("not asked yet");

    /// <summary>
    /// Whether the user has said not now to the update on offer.
    /// </summary>
    /// <remarks>
    /// For the run and no longer: saying no to a patch is not saying no to the next one, and the card is the
    /// only thing that would ever mention it again.
    /// </remarks>
    private bool _portableUpdateDismissed;

    private CancellationTokenSource? _portableUpdateCancellation;

    [ObservableProperty] private bool _isUpdatingPortableClient;
    [ObservableProperty] private double _portableUpdateProgress;
    [ObservableProperty] private string _portableUpdateStage = "Idle";
    [ObservableProperty] private string _portableUpdateMessage = string.Empty;

    public bool HasPortableUpdateMessage => !string.IsNullOrWhiteSpace(PortableUpdateMessage);

    partial void OnIsUpdatingPortableClientChanged(bool value) => RefreshDerived();

    partial void OnPortableUpdateMessageChanged(string value) => RefreshDerived();

    /// <summary>
    /// Whether the launcher should be saying that the game in its own folder is an older build.
    /// </summary>
    /// <remarks>
    /// False while an update is running: the offer has been taken, and what the card shows then is the progress
    /// rather than the question again.
    /// </remarks>
    public bool HasPortableClientUpdate
        => IsPortable && _portableUpdate.Available && !_portableUpdateDismissed && !IsUpdatingPortableClient;

    /// <summary>The card on Home appears while there is something to offer, do, or report.</summary>
    public bool ShowPortableClientUpdateCard
        => IsPortable && (HasPortableClientUpdate || IsUpdatingPortableClient || HasPortableUpdateMessage);

    /// <summary>Which two builds are being compared, in a sentence, for the card.</summary>
    public string PortableUpdateSummary => PortableClientUpdate.Describe(_portableUpdate);

    /// <summary>The install the newer build would come from, for the card to name.</summary>
    public string PortableUpdateSource => _portableUpdate.SourceFolder ?? string.Empty;

    /// <summary>Whether the update would write the whole game again.</summary>
    public bool PortableUpdateReplacesEverything {
        get => _portableUpdateMethod == ClientUpdateMethod.Replace;
        set {
            if (value) {
                ChoosePortableUpdateMethod(ClientUpdateMethod.Replace);
            }
        }
    }

    /// <summary>Whether the update would read both copies and write only what differs - the other side of the choice.</summary>
    public bool PortableUpdateOnlyChanges {
        get => _portableUpdateMethod == ClientUpdateMethod.Mirror;
        set {
            if (value) {
                ChoosePortableUpdateMethod(ClientUpdateMethod.Mirror);
            }
        }
    }

    /// <summary>What the chosen method costs, said on the card rather than left to be discovered.</summary>
    public string PortableUpdateMethodNote => _portableUpdateMethod == ClientUpdateMethod.Replace
        ? "Every file is written again. The simplest of the two, and the one with nothing to work out - it "
          + "costs the whole size of the game in writes, however little of it actually changed."
        : "Both folders are read and only the files that are not already the same are written. It costs "
          + "processor time instead of disk writes, which is what to pick on a slow drive.";

    private void ChoosePortableUpdateMethod(ClientUpdateMethod method) {
        if (_portableUpdateMethod == method) {
            return;
        }

        _portableUpdateMethod = method;

        OnPropertyChanged(nameof(PortableUpdateReplacesEverything));
        OnPropertyChanged(nameof(PortableUpdateOnlyChanges));
        OnPropertyChanged(nameof(PortableUpdateMethodNote));
    }

    /// <summary>
    /// Asks whether the game in the launcher's own folder is older than the one installed on this machine.
    /// </summary>
    /// <remarks>
    /// Asked again rather than remembered, and asked on the client state refreshing rather than in
    /// <see cref="RefreshDerived"/>: it reads registry keys and walks the usual install locations, and it is
    /// only ever interesting when the game folder is what changed.
    /// </remarks>
    private void RefreshPortableClientUpdate() {
        if (!IsPortable) {
            return;
        }

        _portableUpdate = PortableClientUpdate.Check(ClientPath, _store.Settings.PortableGameSourcePath);

        if (_portableUpdate.Available) {
            // Once per start at most, because a check that finds the same thing on every client refresh would
            // be the same line in the Log over and over.
            if (!_portableUpdateDismissed) {
                AppendLog(LogLevel.Info,
                    $"The game in {ClientPath} is {_portableUpdate.CopyBuild}, and {_portableUpdate.SourceFolder} "
                    + $"has {_portableUpdate.SourceBuild}. Settings → the game folder can bring the copy up to "
                    + "the installed build.");
            }
        }

        RefreshDerived();
    }

    /// <summary>Whether an update can be started: there is one on offer, and none is running.</summary>
    private bool CanUpdatePortableClient() => HasPortableClientUpdate;

    /// <summary>
    /// Brings the game in the launcher's own folder up to the build installed on this machine.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanUpdatePortableClient))]
    private async Task UpdatePortableClientAsync() {
        var source = _portableUpdate.SourceFolder;
        var destination = ClientPath;

        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(destination)) {
            PortableUpdateMessage = "There is nothing to update: either the launcher's own copy or the install "
                                    + "to update from is not there any more.";
            AppendLog(LogLevel.Warning, PortableUpdateMessage);
            return;
        }

        IsUpdatingPortableClient = true;
        PortableUpdateMessage = string.Empty;
        _portableUpdateCancellation = new CancellationTokenSource();

        var progress = new Progress<ClientUpdateProgress>(p => {
            PortableUpdateProgress = p.Percent;
            PortableUpdateStage = $"{p.Stage} · {p.Detail}";
        });

        AppendLog(LogLevel.Info, $"Updating the game in {destination} from {source} "
                                 + $"({(_portableUpdateMethod == ClientUpdateMethod.Replace ? "every file written again" : "only what differs")})…");

        try {
            var result = await GameClientUpdateService
                .UpdateAsync(source, destination, _portableUpdateMethod, progress, _portableUpdateCancellation.Token)
                .ConfigureAwait(true);

            PortableUpdateMessage = result.Warning is { Length: > 0 } ? result.Warning : result.Message;

            AppendLog(result.Success ? LogLevel.Success : LogLevel.Error, result.Message);

            if (result.Warning is { Length: > 0 }) {
                AppendLog(LogLevel.Warning, result.Warning);
            }
        }
        catch (Exception ex) {
            PortableUpdateMessage = $"Updating the game failed: {ex.Message}";
            AppendLog(LogLevel.Error, PortableUpdateMessage);
        }
        finally {
            _portableUpdateCancellation?.Dispose();
            _portableUpdateCancellation = null;
            IsUpdatingPortableClient = false;
            PortableUpdateStage = "Idle";
            PortableUpdateProgress = 0;

            // The folder that was written is the one the launcher plays, so its state is read again - which
            // also asks the update question once more, and that is what makes the card go away when it worked.
            RefreshClientState();
        }
    }

    [RelayCommand]
    private void CancelPortableClientUpdate() => _portableUpdateCancellation?.Cancel();

    /// <summary>Puts the offer away for this run, leaving the copy exactly as it is.</summary>
    [RelayCommand]
    private void DismissPortableClientUpdate() {
        _portableUpdateDismissed = true;
        RefreshDerived();
    }
}
