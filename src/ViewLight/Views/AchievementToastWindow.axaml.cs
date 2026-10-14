using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using ViewLight.Services;
using ViewLight.ViewModels;

namespace ViewLight.Views;

/// <summary>
/// The window an achievement is announced in.
/// </summary>
/// <remarks>
/// <para>
/// A window of its own, and not the launcher's, because the announcement has to reach the player wherever
/// they are: badges are earned from the server while the game is running, with the launcher behind it,
/// behind a full-screen game, or out of the way entirely. So this one is always on top, takes no focus, and
/// appears in the corner of the monitor rather than the corner of the launcher.
/// </para>
/// <para>
/// It is shown only while there is something to say. Everything below is the small amount of plumbing that
/// takes: follow the view model's toast state, animate the card in and out, and keep the window sitting in
/// the corner whatever size the card turns out to be.
/// </para>
/// </remarks>
public partial class AchievementToastWindow : Window {

    /// <summary>
    /// How long the slide-out takes before the window is taken off the screen.
    /// </summary>
    /// <remarks>
    /// Matched to the transition in <c>LauncherStyles</c>, with a little room over it. Hiding a window
    /// cancels whatever it was animating, so the card has to be left alone until it has finished leaving.
    /// </remarks>
    private static readonly TimeSpan SlideOut = TimeSpan.FromMilliseconds(450);

    private MainWindowViewModel? _viewModel;
    private DispatcherTimer? _hideTimer;

    public AchievementToastWindow() {
        InitializeComponent();

        Opened += (_, _) => {
            MoveToCorner();
            Apply(_viewModel?.IsToastShown == true);
        };

        // A card's height depends on how long the evidence line is, and the window is sized to it - so its
        // own resize is the moment to re-place it. Without this a two-line toast would hang off the bottom
        // edge of the monitor instead of sitting in the corner.
        SizeChanged += (_, _) => MoveToCorner();
    }

    /// <summary>
    /// Starts following a view model's toast state.
    /// </summary>
    /// <remarks>
    /// The same view model the launcher window uses rather than a copy of it, so the words on the toast are
    /// the words the profile already recorded - there is no second place for them to come from or to drift.
    /// </remarks>
    public void Attach(MainWindowViewModel viewModel) {
        _viewModel = viewModel;
        DataContext = viewModel;
        viewModel.PropertyChanged += OnViewModelChanged;
    }

    /// <summary>Whether the toast has ever been on screen, and so is something that has to be taken down.</summary>
    public bool HasAppeared { get; private set; }

    /// <summary>
    /// Takes the toast down with the launcher, if it ever came up.
    /// </summary>
    /// <remarks>
    /// An always-on-top window with no owner outlives the window that made it, so it is closed explicitly
    /// rather than left to the process - a notification card floating over the desktop with nothing behind
    /// it is exactly the sort of ghost this launcher is not allowed to leave. A toast that never appeared
    /// is simply detached: closing a window that was never shown is not a thing to do.
    /// </remarks>
    public void CloseWithLauncher() {
        Detach();

        if (HasAppeared) {
            Close();
        }
    }

    /// <summary>Stops following, so a closed launcher leaves nothing listening.</summary>
    public void Detach() {
        if (_viewModel is not null) {
            _viewModel.PropertyChanged -= OnViewModelChanged;
            _viewModel = null;
        }

        _hideTimer?.Stop();
        _hideTimer = null;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e) {
        if (e.PropertyName == nameof(MainWindowViewModel.IsToastShown)) {
            Apply(_viewModel?.IsToastShown == true);
        }
    }

    /// <summary>Takes the window on or off the screen to match the toast state.</summary>
    private void Apply(bool shown) {
        if (_viewModel is null) {
            return;
        }

        if (shown) {
            _hideTimer?.Stop();
            _hideTimer = null;

            if (!IsVisible) {
                HasAppeared = true;
                Show();
            }

            MoveToCorner();

            // The class goes on one beat later, so the transition has a state to animate *from*: applied in
            // the same pass the window is shown in, the card would simply appear with nothing to animate.
            Dispatcher.UIThread.Post(() => ToastCard.Classes.Add("shown"), DispatcherPriority.Background);
            return;
        }

        ToastCard.Classes.Remove("shown");

        _hideTimer = new DispatcherTimer { Interval = SlideOut };
        _hideTimer.Tick += (_, _) => {
            _hideTimer?.Stop();
            _hideTimer = null;

            // Only if nothing new arrived while the card was sliding away.
            if (_viewModel?.IsToastShown != true) {
                Hide();
            }
        };

        _hideTimer.Start();
    }

    /// <summary>Puts the window in the bottom-right corner of the monitor it is on.</summary>
    private void MoveToCorner() {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null) {
            return;
        }

        var area = screen.WorkingArea;

        var (x, y) = ToastPlacement.BottomRight(
            area.X,
            area.Y,
            area.Width,
            area.Height,
            Bounds.Width,
            Bounds.Height,
            ToastPlacement.DefaultMargin,
            screen.Scaling);

        Position = new PixelPoint(x, y);
    }
}
