using Avalonia.Controls;
using Avalonia.Interactivity;

namespace ViewLight.Views;

public partial class ProfileView : UserControl {

    public ProfileView() => InitializeComponent();

    /// <summary>
    /// Folds the account list away once one of its rows has been clicked. Which account that made current is
    /// the two-way binding's business, not this.
    /// </summary>
    /// <remarks>
    /// Both a change of selection and a click answer the same question, because the launcher has one account
    /// most of the time: clicking the account already signed in changes nothing, so waiting for a change
    /// would leave the list hanging open over the profile it is not changing.
    /// </remarks>
    private void OnProfileChoiceMade(object? sender, RoutedEventArgs e) {
        if (SwitchAccountButton.Flyout is { } flyout) {
            flyout.Hide();
        }
    }
}
