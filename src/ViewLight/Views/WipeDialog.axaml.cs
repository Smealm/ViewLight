using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ViewLight.Services;
using ViewLight.ViewModels;

namespace ViewLight.Views;

/// <summary>
/// Starting over, asked for twice: what it costs, read out and waited through, and then the sentence typed.
/// </summary>
/// <remarks>
/// <para>
/// Two steps because one is not enough for the only irreversible thing in the launcher. The first says what
/// is about to be gone and cannot be gone past until a clock has run - not as a security measure, but because
/// reading a list and waiting fifteen seconds are the same act, and a button that cannot be pressed yet is
/// what makes somebody read. The second asks for the sentence the issue behind this screen asked for, which
/// says in one line that the local data and the cloud copy of it both go.
/// </para>
/// <para>
/// Shown with <c>ShowDialog&lt;WipeRequest?&gt;</c>. Closing it any other way - Escape, the title-bar cross -
/// answers <c>null</c>, which the launcher reads as "nothing was asked for": the safe outcome is the one that
/// needs no thought, and it is the same answer as pressing Cancel.
/// </para>
/// </remarks>
public partial class WipeDialog : Window {

    private readonly DispatcherTimer _countdown;
    private int _secondsLeft;

    /// <summary>
    /// The window with nothing to say.
    /// </summary>
    /// <remarks>
    /// Here for the XAML loader, which can only build a window it can construct for itself. A window with an
    /// empty list is not a stub that lies: it names nothing to delete, and the launcher never opens one this
    /// way.
    /// </remarks>
    public WipeDialog() : this([], string.Empty, cloudPossible: false, cloudOn: false) {
    }

    /// <param name="targets">What is about to be deleted, read out on the first step.</param>
    /// <param name="keepsText">What is not deleted, which is the other half of what the first step says.</param>
    /// <param name="cloudPossible">Whether there is a cloud copy this launcher could delete.</param>
    /// <param name="cloudOn">Whether that copy is what the user has been keeping up to date.</param>
    public WipeDialog(
        IReadOnlyList<string> targets, string keepsText, bool cloudPossible, bool cloudOn) {

        InitializeComponent();

        TargetList.ItemsSource = targets;
        KeepsText.Text = keepsText;
        PhraseText.Text = LauncherReset.ConfirmationPhrase;

        CloudCheck.IsChecked = cloudPossible && cloudOn;
        CloudCheck.IsVisible = cloudPossible;

        CloudExplanation.Text = cloudPossible
            ? "Left unticked, the copy in your Drive is not touched - and because a launcher with no data of "
              + "its own and a backup up there takes the backup, the next sync would bring it back."
            : "No Google account is signed in, so there is no cloud copy this launcher can delete.";

        // The countdown is the first step's own clock, and it is shown rather than hidden: a button that is
        // simply dead reads as a broken window, while a number going down reads as a thing to wait for.
        _secondsLeft = (int) Math.Ceiling(LauncherReset.ReadingTime.TotalSeconds);
        ShowCountdown();

        _countdown = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _countdown.Tick += (_, _) => ShowCountdown();
        _countdown.Start();
    }

    private void ShowCountdown() {
        if (_secondsLeft <= 0) {
            _countdown.Stop();
            NextButton.Content = "Next";
            NextButton.IsEnabled = true;
            return;
        }

        NextButton.Content = $"Next ({_secondsLeft}s)";
        _secondsLeft--;
    }

    private void OnNextClick(object? sender, RoutedEventArgs e) {
        _countdown.Stop();

        ReadingStep.IsVisible = false;
        TypingStep.IsVisible = true;
        ConfirmBox.Focus();
    }

    private void OnPhraseChanged(object? sender, TextChangedEventArgs e) {
        var matches = LauncherReset.PhraseMatches(ConfirmBox.Text);

        WipeButton.IsEnabled = matches;
        TypedProblem.IsVisible = ConfirmBox.Text?.Length > 0 && !matches;
    }

    private void OnWipeClick(object? sender, RoutedEventArgs e) {
        // Checked again rather than trusted to the button's own state: this is the one press in the launcher
        // that cannot be taken back, and the rule belongs to the service that owns it.
        if (!LauncherReset.PhraseMatches(ConfirmBox.Text)) {
            return;
        }

        Close(new WipeRequest(CloudCheck.IsVisible && CloudCheck.IsChecked == true));
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);
}
