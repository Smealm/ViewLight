using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ViewLight.Services;
using ViewLight.ViewModels;

namespace ViewLight.Views;

public partial class MainWindow : Window {

    private MainWindowViewModel? _viewModel;

    /// <summary>
    /// The window achievements are announced in. Its own window rather than a card here, because a badge
    /// is earned while the game is being played - so it has to be able to appear over whatever is on
    /// screen, in the corner of the monitor, with this window behind the game or not there at all.
    /// </summary>
    private readonly AchievementToastWindow _toast = new();

    /// <summary>Set once the close has been answered, so the second pass does not ask again.</summary>
    private bool _closeApproved;

    /// <summary>Stops a second close while the prompt is already up.</summary>
    private bool _promptIsOpen;

    public MainWindow() {
        InitializeComponent();

        _viewModel = new MainWindowViewModel {
            PickFolderAsync = PickFolderAsync,
            PickImageAsync = PickImageAsync,
            AskAboutPreloadAsync = AskAboutPreloadAsync,
        };

        DataContext = _viewModel;

        _toast.Attach(_viewModel);
    }

    /// <summary>
    /// Notes the pointer on the profile card, which is what decides whether it folds back up: leaving it
    /// starts the countdown, and coming back cancels it so a pointer merely passing over the card does not
    /// collapse it.
    /// </summary>
    private void OnProfileCardPointerEntered(object? sender, PointerEventArgs e)
        => _viewModel?.NoteProfileCardPointer(over: true);

    /// <summary>
    /// Whether the pointer is off the card, asked of the card's own bounds rather than trusted to a pair
    /// of enter/leave events.
    /// </summary>
    /// <remarks>
    /// The pointer moving from the card onto the button inside it - the same rectangle - is a move the two
    /// events do not agree about, and the countdown to folding the card back must not start while the
    /// pointer is still on it. The position is what actually answers the question.
    /// </remarks>
    private void OnProfileCardPointerExited(object? sender, PointerEventArgs e) {
        if (_viewModel is null) {
            return;
        }

        var at = e.GetPosition(ProfileCard);
        var onCard = at.X >= 0 && at.Y >= 0
                     && at.X <= ProfileCard.Bounds.Width && at.Y <= ProfileCard.Bounds.Height;

        _viewModel.NoteProfileCardPointer(over: onCard);
    }

    /// <summary>
    /// Picks a picture for a profile - its own or its banner - and hands the path back for the view model
    /// to copy into the launcher's folder.
    /// </summary>
    private async Task<string?> PickImageAsync(bool banner) {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions {
            Title = banner ? "Choose a banner" : "Choose a profile picture",
            AllowMultiple = false,
            FileTypeFilter = [
                new FilePickerFileType("Images") {
                    Patterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp", "*.gif", "*.webp"],
                },
            ],
        });

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    private async Task<string?> PickFolderAsync(string? startAt) {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions {
            Title = "Select a folder",
            AllowMultiple = false,
            SuggestedStartLocation = await TryGetFolderAsync(startAt),
        });

        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    /// <summary>
    /// Asks whether the patch server should be filled in from a Wizard101 install on this machine before
    /// this session's server is started.
    /// </summary>
    /// <remarks>
    /// Asked here rather than in the view model because a dialog needs a window to be owned by. Anything
    /// that goes wrong answers "play without it": the copy is an improvement to the session, not a
    /// condition of one.
    /// </remarks>
    private async Task<PreloadOfferAnswer> AskAboutPreloadAsync(PatchServerPreloadPlan plan) {
        if (_promptIsOpen) {
            return new PreloadOfferAnswer(Preload: false, DontAskAgain: false);
        }

        _promptIsOpen = true;

        try {
            var dialog = new PreloadPatchServerDialog {
                Headline = MainWindowViewModel.PreloadOfferHeadline(plan),
                Detail = MainWindowViewModel.PreloadOfferDetail(plan),
            };

            return await dialog.ShowDialog<PreloadOfferAnswer>(this);
        }
        catch (Exception ex) {
            _viewModel?.AppendLog(LogLevel.Error,
                $"Could not ask whether to fill the patch server in: {ex.Message}");

            return new PreloadOfferAnswer(Preload: false, DontAskAgain: false);
        }
        finally {
            _promptIsOpen = false;
        }
    }

    private async Task<IStorageFolder?> TryGetFolderAsync(string? path) {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) {
            return null;
        }

        try {
            return await StorageProvider.TryGetFolderFromPathAsync(path);
        }
        catch (Exception) {
            return null;
        }
    }

    protected override void OnClosing(WindowClosingEventArgs e) {
        if (_closeApproved || _viewModel?.ShouldWarnBeforeClosing != true) {
            // Never leave an orphaned Imlight.Director behind.
            _viewModel?.Shutdown();
            CloseToast();
            base.OnClosing(e);
            return;
        }

        // The server is still running and closing stops it, so ask first. The question has to be asked
        // before the window goes away, so this close is cancelled and re-issued once the answer is in.
        e.Cancel = true;

        // After this event, not during it: a modal dialog shown from inside a closing handler would be
        // opening while the framework is still unwinding the close.
        Dispatcher.UIThread.Post(() => _ = ConfirmThenCloseAsync(), DispatcherPriority.Background);
    }

    /// <summary>
    /// Takes the toast window down with the launcher. An always-on-top window with no owner would outlive
    /// this one, and a notification card left floating over the desktop with nothing behind it is exactly
    /// the kind of ghost this launcher is not allowed to leave.
    /// </summary>
    private void CloseToast() => _toast.CloseWithLauncher();

    private async Task ConfirmThenCloseAsync() {
        if (_viewModel is null || _promptIsOpen) {
            return;
        }

        _promptIsOpen = true;

        try {
            var dialog = new ConfirmCloseDialog { Detail = _viewModel.CloseWarningDetail };
            var confirmed = await dialog.ShowDialog<bool>(this);

            if (!confirmed) {
                return;
            }

            if (dialog.DontAskAgain) {
                _viewModel.ConfirmCloseWhenServerRunning = false;
            }

            _closeApproved = true;
            Close();
        }
        catch (Exception ex) {
            // The prompt failing must not trap the user in a window they cannot close: the server is
            // tied to this process, so letting the close through still stops it.
            _viewModel.AppendLog(LogLevel.Error, $"Could not ask before closing: {ex.Message}");
            _closeApproved = true;
            Close();
        }
        finally {
            _promptIsOpen = false;
        }
    }
}
