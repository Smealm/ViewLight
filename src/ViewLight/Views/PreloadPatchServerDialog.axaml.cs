using Avalonia.Controls;
using Avalonia.Interactivity;
using ViewLight.Services;

namespace ViewLight.Views;

/// <summary>
/// The question Play asks before starting the server, when the patch server is missing files that a
/// Wizard101 install on this machine already has.
/// </summary>
/// <remarks>
/// Shown with <c>ShowDialog&lt;PreloadOfferAnswer&gt;</c>. Closing it any other way - Escape, the title-bar
/// cross - answers "play without it", so the safe outcome is the one that needs no thought: nothing is
/// copied and the session starts as it always would have.
/// </remarks>
public partial class PreloadPatchServerDialog : Window {

    public PreloadPatchServerDialog() => InitializeComponent();

    /// <summary>What the offer is, in one line, e.g. "Copy 1,158 files (17.4 GB) into the patch server?".</summary>
    public string Headline {
        set => HeadlineText.Text = value;
    }

    /// <summary>Why those files, and why a copy rather than a download.</summary>
    public string Detail {
        set => DetailText.Text = value;
    }

    private void OnCopyThemClick(object? sender, RoutedEventArgs e) => Close(Answer(preload: true));

    private void OnPlayWithoutItClick(object? sender, RoutedEventArgs e) => Close(Answer(preload: false));

    private PreloadOfferAnswer Answer(bool preload)
        => new(preload, DontAskAgainCheck.IsChecked == true);
}
