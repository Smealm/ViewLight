using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ViewLight.Services;

namespace ViewLight.ViewModels;

/// <summary>
/// Which page of Settings is on screen.
/// </summary>
/// <remarks>
/// Settings is one long list of cards and it was getting to be the whole problem: the values are grouped by
/// what they belong to - the server, the client, the launcher, the data - and scrolling past three groups to
/// reach the fourth is how a page stops being usable. So the groups are pages, and the page you are on is
/// named rather than found by scrolling.
/// </remarks>
public enum SettingsTab {

    /// <summary>Everything about the Imlight server and the patch server it loads its files from.</summary>
    Server,

    /// <summary>Where the game is, and what a brand new character starts with.</summary>
    Client,

    /// <summary>ViewLight itself: how it looks, how it behaves, and where it keeps its things.</summary>
    Launcher,

    /// <summary>The data: the cloud backup that keeps it, and the reset that deletes all of it.</summary>
    Data,
}

/// <summary>Which half of the server page is on screen.</summary>
public enum ServerSettingsTab {

    /// <summary>The server's own config - its folder, its build, and every value written into Imlight.ini.</summary>
    Imlight,

    /// <summary>The patch server: whose it is, what it has fetched, and what is kept.</summary>
    Aurorium,
}

/// <summary>Which half of the client page is on screen.</summary>
public enum ClientSettingsTab {

    /// <summary>Where Wizard101 is installed.</summary>
    GameFolder,

    /// <summary>What a new character is given, and what the tutorial does.</summary>
    Characters,
}

/// <summary>
/// The Settings page's own navigation.
/// </summary>
/// <remarks>
/// Two levels, because one of the groups is itself two things: the server's own settings and the patch
/// server's are both "the server" to anybody looking for them, and both are long enough to be worth a page
/// each. The sub-tabs are only drawn for the group they belong to, so the second level never appears where it
/// would mean nothing.
/// </remarks>
public sealed partial class MainWindowViewModel {

    [ObservableProperty] private SettingsTab _settingsTab = SettingsTab.Server;

    [ObservableProperty] private ServerSettingsTab _serverSettingsTab = ServerSettingsTab.Imlight;

    [ObservableProperty] private ClientSettingsTab _clientSettingsTab = ClientSettingsTab.GameFolder;

    // ---------------------------------------------------------------- which tab is on screen

    public bool IsServerSettings => SettingsTab == SettingsTab.Server;

    public bool IsClientSettings => SettingsTab == SettingsTab.Client;

    public bool IsLauncherSettings => SettingsTab == SettingsTab.Launcher;

    public bool IsDataSettings => SettingsTab == SettingsTab.Data;

    // The two levels together, never the sub-page on its own: a sub-page is a heading *of* the page above it,
    // and one that answered only for itself would be true on every page - which is how the patch server's
    // cards came to sit under the client's and the game folder's card under all four.
    public bool IsImlightSettings => IsServerSettings && ServerSettingsTab == ServerSettingsTab.Imlight;

    public bool IsAuroriumSettings => IsServerSettings && ServerSettingsTab == ServerSettingsTab.Aurorium;

    public bool IsGameFolderSettings => IsClientSettings && ClientSettingsTab == ClientSettingsTab.GameFolder;

    public bool IsCharacterSettings => IsClientSettings && ClientSettingsTab == ClientSettingsTab.Characters;

    /// <summary>Whether the second row of tabs is drawn at all: it belongs to the server and client groups.</summary>
    public bool HasSettingsSubTabs => IsServerSettings || IsClientSettings;

    partial void OnSettingsTabChanged(SettingsTab value) => NotifySettingsTabs();

    partial void OnServerSettingsTabChanged(ServerSettingsTab value) {
        OnPropertyChanged(nameof(IsImlightSettings));
        OnPropertyChanged(nameof(IsAuroriumSettings));
    }

    partial void OnClientSettingsTabChanged(ClientSettingsTab value) {
        OnPropertyChanged(nameof(IsGameFolderSettings));
        OnPropertyChanged(nameof(IsCharacterSettings));
    }

    private void NotifySettingsTabs() {
        OnPropertyChanged(nameof(IsServerSettings));
        OnPropertyChanged(nameof(IsClientSettings));
        OnPropertyChanged(nameof(IsLauncherSettings));
        OnPropertyChanged(nameof(IsDataSettings));
        OnPropertyChanged(nameof(HasSettingsSubTabs));

        // A sub-page answers for itself *and* for the page it belongs to, so changing the page can change
        // every one of them even though the sub-page the user picked has not moved.
        OnPropertyChanged(nameof(IsImlightSettings));
        OnPropertyChanged(nameof(IsAuroriumSettings));
        OnPropertyChanged(nameof(IsGameFolderSettings));
        OnPropertyChanged(nameof(IsCharacterSettings));
    }

    // ---------------------------------------------------------------- moving between them

    /// <summary>Opens one of the four pages of Settings.</summary>
    /// <param name="tab">The page's name, as the settings file and the buttons spell it.</param>
    [RelayCommand]
    private void OpenSettingsTab(string? tab) {
        if (Enum.TryParse<SettingsTab>(tab, ignoreCase: true, out var parsed)) {
            SettingsTab = parsed;
        }
    }

    /// <summary>Opens one of the two halves of the server page.</summary>
    [RelayCommand]
    private void OpenServerSettingsTab(string? tab) {
        if (Enum.TryParse<ServerSettingsTab>(tab, ignoreCase: true, out var parsed)) {
            ServerSettingsTab = parsed;
        }
    }

    /// <summary>Opens one of the two halves of the client page.</summary>
    [RelayCommand]
    private void OpenClientSettingsTab(string? tab) {
        if (Enum.TryParse<ClientSettingsTab>(tab, ignoreCase: true, out var parsed)) {
            ClientSettingsTab = parsed;
        }
    }
}
