using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using ViewLight.ViewModels;

namespace ViewLight.Views;

/// <summary>
/// The window a profile picture or a banner is browsed for in: the gamerpic archive for a picture, and
/// wallhaven's weekly toplist for a banner.
/// </summary>
/// <remarks>
/// <para>
/// Shown with <c>ShowDialog&lt;ChosenImage?&gt;</c>, and it answers with the picture itself rather than a
/// path: both collections are somebody else's pictures on somebody else's server, so what the launcher keeps
/// is what it downloaded, and the view model that asked for it does not have to know where it came from.
/// </para>
/// <para>
/// Closing any other way - Escape, the title-bar cross - answers <c>null</c>, which means "nothing was
/// chosen". That is deliberately the same answer as pressing Cancel: the safe outcome is the one that needs
/// no thought.
/// </para>
/// </remarks>
public partial class ImageBrowserWindow : Window {

    private readonly ImageBrowserViewModel _viewModel;

    /// <summary>Set once a picture has been taken, so a close that follows does not answer with nothing.</summary>
    private ChosenImage? _chosen;

    /// <summary>
    /// The window with nothing but the archive to look at.
    /// </summary>
    /// <remarks>
    /// Here for the XAML loader, which can only build a window it can construct for itself - and it is not a
    /// stub: it is the gamerpic archive, which is the collection that is browsed for a profile picture and the
    /// one a window opened without being told anything should show.
    /// </remarks>
    public ImageBrowserWindow() : this(new ImageBrowserViewModel(ImageBrowserSource.Gamerpics)) {
    }

    public ImageBrowserWindow(ImageBrowserViewModel viewModel) {
        InitializeComponent();

        _viewModel = viewModel;
        DataContext = viewModel;

        // The listing is asked for when the window is up rather than when it is built, so the window appears
        // at once and its own status line is where the wait is visible.
        Opened += (_, _) => _ = viewModel.StartAsync();
    }

    /// <summary>Enter in the search box runs the search, which is what a search box is for.</summary>
    private void OnSearchKeyDown(object? sender, KeyEventArgs e) {
        if (e.Key is Key.Enter or Key.Return) {
            _viewModel.SearchCommand.Execute(null);
            e.Handled = true;
        }
    }

    private async void OnUseClick(object? sender, RoutedEventArgs e) {
        // Disabled while the picture is fetched, so this cannot be pressed twice into two downloads.
        UseButton.IsEnabled = false;

        try {
            var chosen = await _viewModel.TakeSelectedAsync();

            if (chosen is null) {
                // The failure is on the window's own status line, so the window stays open to say it.
                UseButton.IsEnabled = _viewModel.CanChoose;
                return;
            }

            _chosen = chosen;
            Close(chosen);
        }
        catch (Exception) {
            UseButton.IsEnabled = _viewModel.CanChoose;
        }
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(_chosen);
}
