using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ViewLight.ViewModels;

namespace ViewLight.Views;

/// <summary>
/// The screen a launcher with nobody signed in shows: the accounts as a row of cards, and the way back in.
/// </summary>
/// <remarks>
/// The cards are buttons and the click is handled here rather than by a command on the tile, because the tile
/// is a value that knows nothing about the launcher - see <see cref="Services.AccountCard"/>. The arrows are
/// here for the same reason: scrolling a strip is a gesture, not a state, and a page-at-a-time slide is what
/// makes the row feel walked along rather than jumped in.
/// </remarks>
public partial class SignInView : UserControl {

    /// <summary>How long one press of an arrow takes to travel, in seconds.</summary>
    private const double SlideSeconds = 0.22;

    /// <summary>The timer the current slide is running on, or null when the strip is at rest.</summary>
    private DispatcherTimer? _slide;

    public SignInView() => InitializeComponent();

    /// <summary>Signs in as the account whose card was clicked.</summary>
    private void OnAccountCardClicked(object? sender, RoutedEventArgs e) {
        if (DataContext is MainWindowViewModel launcher && sender is Control { Tag: ulong accountId }) {
            launcher.SignInAs(accountId);
        }
    }

    private void OnScrollLeft(object? sender, RoutedEventArgs e) => SlideBy(-PageWidth());

    private void OnScrollRight(object? sender, RoutedEventArgs e) => SlideBy(PageWidth());

    /// <summary>How far one press of an arrow should travel: most of what is on screen, but never less than a card.</summary>
    private double PageWidth() => Math.Max(252, AccountStrip.Viewport.Width * 0.8);

    /// <summary>
    /// Slides the strip by a distance, easing out over <see cref="SlideSeconds"/>.
    /// </summary>
    /// <remarks>
    /// A timer rather than a jump because the strip is what says there are more accounts: a row that teleports
    /// halfway across the screen reads as a redraw, and a row that travels reads as a row. Any slide already
    /// running is stopped first, so pressing an arrow twice quickly ends where the second press asked rather
    /// than fighting the first.
    /// </remarks>
    private void SlideBy(double delta) {
        _slide?.Stop();
        _slide = null;

        var from = AccountStrip.Offset.X;
        var limit = Math.Max(0, AccountStrip.Extent.Width - AccountStrip.Viewport.Width);
        var to = Math.Clamp(from + delta, 0, limit);

        if (Math.Abs(to - from) < 1) {
            return;
        }

        var started = DateTime.UtcNow;

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        timer.Tick += (_, _) => {
            var travelled = Math.Clamp((DateTime.UtcNow - started).TotalSeconds / SlideSeconds, 0, 1);

            // Cubic ease-out: quick to start, settling at the end, so the strip arrives rather than stopping.
            var eased = 1 - Math.Pow(1 - travelled, 3);
            AccountStrip.Offset = new Vector(from + ((to - from) * eased), AccountStrip.Offset.Y);

            if (travelled >= 1) {
                timer.Stop();

                if (ReferenceEquals(_slide, timer)) {
                    _slide = null;
                }
            }
        };

        _slide = timer;
        timer.Start();
    }
}
