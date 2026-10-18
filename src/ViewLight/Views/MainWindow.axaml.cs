using System.ComponentModel;
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
    /// The window achievements are announced in, once there has been an achievement to announce. Its own
    /// window rather than a card here, because a badge is earned while the game is being played - so it has to
    /// be able to appear over whatever is on screen, in the corner of the monitor, with this window behind the
    /// game or not there at all. Built on the first toast rather than with this window: it is a second XAML
    /// tree, and most starts never show it.
    /// </summary>
    private AchievementToastWindow? _toast;

    /// <summary>The pages built so far, by the name of the view they are.</summary>
    private readonly Dictionary<string, Control> _pages = [];

    /// <summary>Set once the close has been answered, so the second pass does not ask again.</summary>
    private bool _closeApproved;

    /// <summary>Stops a second close while the prompt is already up.</summary>
    private bool _promptIsOpen;

    public MainWindow() {
        InitializeComponent();

        _viewModel = new MainWindowViewModel {
            PickFolderAsync = PickFolderAsync,
            PickImageAsync = PickImageAsync,
            BrowseImageAsync = BrowseImageAsync,
            ConfirmResetAsync = ConfirmResetAsync,
        };

        DataContext = _viewModel;

        // Only the page the launcher opens on is built now; the rest are made when they are first asked for.
        ShowPage();

        _viewModel.PropertyChanged += OnViewModelChanged;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e) {
        switch (e.PropertyName) {
            case nameof(MainWindowViewModel.IsToastShown) when _viewModel?.IsToastShown == true:
                Toast.Attach(_viewModel);
                break;

            // A page is built by being asked for, so a change of section or of the profile's own page is the
            // moment to hand the host the view that section is.
            case nameof(MainWindowViewModel.Section):
            case nameof(MainWindowViewModel.ProfilePage):
                ShowPage();
                break;
        }
    }

    /// <summary>
    /// Puts the view for the section on screen, building it the first time it is needed.
    /// </summary>
    /// <remarks>
    /// The window used to declare every page at once and let the view model's visibility flags choose one,
    /// which builds all six control trees before the first frame - Settings alone is hundreds of controls.
    /// Nothing about the pages is conditional any more: the one on screen is in the host, and the others do
    /// not exist until somebody goes to them.
    /// </remarks>
    private void ShowPage() {
        if (_viewModel is null) {
            return;
        }

        if (PageFor(_viewModel) is not { } name) {
            PageHost.Content = null;
            return;
        }

        if (!_pages.TryGetValue(name, out var page)) {
            page = name switch {
                nameof(HomeView) => new HomeView(),
                nameof(ProfileView) => new ProfileView(),
                nameof(AchievementsView) => new AchievementsView(),
                nameof(SettingsView) => new SettingsView(),
                nameof(LogsView) => new LogsView(),
                _ => new AboutView(),
            };

            _pages[name] = page;
        }

        if (!ReferenceEquals(PageHost.Content, page)) {
            PageHost.Content = page;
        }
    }

    /// <summary>The view a launcher in this state is showing, or <c>null</c> when it is showing none.</summary>
    private static string? PageFor(MainWindowViewModel viewModel) {
        if (viewModel.IsHomeSelected) {
            return nameof(HomeView);
        }

        if (viewModel.ShowProfilePage) {
            return nameof(ProfileView);
        }

        if (viewModel.ShowAchievementsPage) {
            return nameof(AchievementsView);
        }

        if (viewModel.IsSettingsSelected) {
            return nameof(SettingsView);
        }

        if (viewModel.IsLogsSelected) {
            return nameof(LogsView);
        }

        return viewModel.IsAboutSelected ? nameof(AboutView) : null;
    }

    /// <summary>The toast window, made the first time there is a badge to announce.</summary>
    private AchievementToastWindow Toast => _toast ??= new AchievementToastWindow();

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

    /// <summary>
    /// Opens the picture browser for one of the two collections and hands back whatever was chosen.
    /// </summary>
    /// <remarks>
    /// The window is built here rather than in the view model, which is the same arrangement the file picker
    /// uses: the view model asks for a picture and knows nothing about where the answer comes from. A close
    /// that answered nothing - Cancel, Escape, the title-bar cross - comes back as <c>null</c>, which the
    /// caller reads as "nothing was chosen".
    /// </remarks>
    private async Task<ChosenImage?> BrowseImageAsync(ImageBrowserSource source) {
        var window = new ImageBrowserWindow(new ImageBrowserViewModel(source));

        return await window.ShowDialog<ChosenImage?>(this);
    }

    /// <summary>
    /// Asks for a full reset, in two steps, and hands back what was answered.
    /// </summary>
    /// <param name="targets">What is about to be deleted, read out on the first step.</param>
    /// <param name="cloudPossible">Whether there is a cloud copy this launcher can delete.</param>
    /// <remarks>
    /// The window is told the list rather than deriving it, so what is read out is exactly what the reset will
    /// act on - a list written twice is a list that can disagree with itself.
    /// </remarks>
    private async Task<WipeRequest?> ConfirmResetAsync(IReadOnlyList<string> targets, bool cloudPossible) {
        var dialog = new WipeDialog(
            targets, _viewModel?.ResetKeepsText ?? string.Empty, cloudPossible,
            _viewModel?.BackUpToCloud == true);

        return await dialog.ShowDialog<WipeRequest?>(this);
    }

    private async Task<string?> PickFolderAsync(string? startAt) {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions {
            Title = "Select a folder",
            AllowMultiple = false,
            SuggestedStartLocation = await TryGetFolderAsync(startAt),
        });

        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
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
    private void CloseToast() => _toast?.CloseWithLauncher();

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
