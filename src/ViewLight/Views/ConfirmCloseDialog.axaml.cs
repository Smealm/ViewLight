using Avalonia.Controls;
using Avalonia.Interactivity;

namespace ViewLight.Views;

/// <summary>
/// The "are you sure" shown when ViewLight is closed while the server it started is still running,
/// because closing stops that server.
/// </summary>
/// <remarks>
/// Shown with <c>ShowDialog&lt;bool&gt;</c>: <c>true</c> means "close anyway". Closing the dialog any
/// other way - Escape, the title-bar cross - answers <c>false</c>, so the safe outcome is the one that
/// needs no thought.
/// </remarks>
public partial class ConfirmCloseDialog : Window {

    public ConfirmCloseDialog() => InitializeComponent();

    /// <summary>One line about the running server, e.g. "It is currently ready."</summary>
    public string Detail {
        set => DetailText.Text = value;
    }

    /// <summary>Whether the user ticked "Don't ask me again".</summary>
    public bool DontAskAgain => DontAskAgainCheck.IsChecked == true;

    private void OnCloseAndStopClick(object? sender, RoutedEventArgs e) => Close(true);

    private void OnKeepRunningClick(object? sender, RoutedEventArgs e) => Close(false);
}
