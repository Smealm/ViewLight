using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ViewLight.Services;

namespace ViewLight.ViewModels;

/// <summary>
/// The launcher's first screen when it cannot work: Imlight is a requirement, and this is where the
/// launcher stops rather than pretending otherwise.
/// </summary>
/// <remarks>
/// <para>
/// Two things need an Imlight install and neither is optional. The first is the launcher's whole job - it
/// installs the server, configures it, runs it and plays it. The second is the launcher's own data: the
/// profiles, playtime, sessions and badges live in the RavenDB an Imlight install ships, so the install is
/// also where this machine's notes about an account are kept. With no install there is nothing to
/// configure, nothing to start, and nowhere to keep a profile - which is a thing to say plainly instead of
/// showing a Home screen whose only button is a download.
/// </para>
/// <para>
/// What is missing is worked out by <see cref="ImlightRequirements.For"/>. This file is only where the
/// answer is kept, when it is asked for, and what the screen binds. It is asked when the folder changes,
/// when a config is loaded, and when the user asks again - never from <see cref="RefreshDerived"/>, which
/// is called far too often to be asking a machine what runtimes it has.
/// </para>
/// </remarks>
public sealed partial class MainWindowViewModel {

    private ImlightRequirement _imlightRequirement = ImlightRequirement.Met;

    /// <summary>Which piece is missing, for anything that has to react rather than explain.</summary>
    public ImlightGap ImlightGap => _imlightRequirement.Gap;

    /// <summary>The sentence the first screen says: which piece is missing, and what it is for.</summary>
    [ObservableProperty] private string _imlightProblem = string.Empty;

    /// <summary>Where to go about it, when there is somewhere to go - the .NET runtime download, mainly.</summary>
    [ObservableProperty] private string _imlightActionUrl = string.Empty;

    [ObservableProperty] private string _imlightActionLabel = string.Empty;

    public bool HasImlightAction => ImlightActionUrl.Length > 0;

    partial void OnImlightActionUrlChanged(string value) => OnPropertyChanged(nameof(HasImlightAction));

    /// <summary>
    /// How to fix it in steps, when there is a right way and a wrong way to do it - installing the .NET
    /// runtime, where the download page has several downloads and only one of them is the one wanted.
    /// </summary>
    [ObservableProperty] private string _imlightGuide = string.Empty;

    public bool HasImlightGuide => ImlightGuide.Length > 0;

    partial void OnImlightGuideChanged(string value) => OnPropertyChanged(nameof(HasImlightGuide));

    /// <summary>
    /// Whether the launcher is stopped on this screen rather than showing itself.
    /// </summary>
    /// <remarks>
    /// It comes after the folders question rather than instead of it: the setup screen is where Imlight
    /// would be, and this one is about what is in the folder that came out of it.
    /// </remarks>
    public bool RequiresImlight => !RequiresSetup && !_imlightRequirement.IsMet;

    /// <summary>Works out what is missing, and says so on the two screens that depend on it.</summary>
    private void RefreshImlightRequirement() {
        _imlightRequirement = ImlightRequirements.For(ServerPath);

        ImlightProblem = _imlightRequirement.Missing;
        ImlightActionUrl = _imlightRequirement.ActionUrl ?? string.Empty;
        ImlightActionLabel = _imlightRequirement.ActionLabel ?? string.Empty;
        ImlightGuide = _imlightRequirement.Guide ?? string.Empty;

        OnPropertyChanged(nameof(ImlightGap));
        OnPropertyChanged(nameof(RequiresImlight));
        OnPropertyChanged(nameof(ShowMainInterface));
    }

    /// <summary>
    /// Downloads an install, or replaces one that is not usable.
    /// </summary>
    /// <remarks>
    /// The release is checked first rather than assumed, exactly as Home's one button does: the tag to
    /// fetch is GitHub's answer and not something the launcher remembers between releases. It is shared
    /// with Home's button - pressing it there with no server installed lands here - and it is deliberately
    /// *not* Home's button, which would play instead of downloading once a config file is there and only
    /// the database is missing.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanPrimaryAction))]
    private async Task InstallImlightAsync() {
        if (_latestRelease is null) {
            await CheckForUpdatesAsync();
        }

        if (_latestRelease is not null) {
            await InstallOrUpdateAsync();
        }
    }

    /// <summary>
    /// Asks again, for an install or a runtime that arrived while the launcher was open.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The runtime answer is remembered for the run because it costs a process launch, so this is the one
    /// place that forgets it: somebody who has just installed .NET and come back to the launcher should not
    /// have to restart it to be believed.
    /// </para>
    /// <para>
    /// Once nothing is missing, the launcher settles in exactly as it would have on a start with the
    /// dependency already there - starting the server's own RavenDB, so profiles and accounts are read
    /// without starting the server. Without that, somebody who installed .NET would get the launcher back
    /// and its own database would stay shut until the next start.
    /// </para>
    /// </remarks>
    [RelayCommand]
    private void CheckImlightAgain() {
        DotnetHost.Forget();
        LoadServerConfig();

        if (!_imlightRequirement.IsMet) {
            AppendLog(LogLevel.Warning, _imlightRequirement.Missing);
            return;
        }

        AppendLog(LogLevel.Success, $"Imlight is usable from {ServerPath}.");
        _ = EnsureIdleDatabaseAsync();
        _ = EnsureLauncherDatabaseAsync();
    }
}
