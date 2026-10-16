using ViewLight.Models;
using ViewLight.Services;
using ViewLight.ViewModels;
using Xunit;

namespace ViewLight.Tests;

/// <summary>
/// The one question about signing in - whose Google app does it - and the public-client workflow behind the
/// built-in answer. The workflow is complete and works today; what this build has not got is a client to name,
/// and that is what the greyed-out option and the guard here are about.
/// </summary>
public class CloudSignInChoiceTests {

    [Fact]
    public void ThisBuildShipsNoGoogleClientOfItsOwn() {
        // Not a defect and not a half-built state: a client id belongs to a Google Cloud project somebody
        // owns, and putting one in GoogleOAuth.BuiltInClientId is the whole of switching this on.
        Assert.False(GoogleOAuth.HasBuiltInClient);
        Assert.True(string.IsNullOrWhiteSpace(GoogleOAuth.BuiltInClientId));
    }

    [Fact]
    public void TheBuiltInSignInCannotBeChosenWhileThereIsNoClient() {
        using var workspace = new TempWorkspace();
        var launcher = Launcher(workspace);

        // The option is greyed out on screen; this is the other half, so a settings file edited by hand cannot
        // put the launcher on a client that does not exist.
        Assert.False(launcher.HasBuiltInGoogleClient);
        Assert.True(launcher.UsesOwnGoogleClient);

        launcher.CloudBackupUsesBuiltInClient = true;

        Assert.False(launcher.CloudBackupUsesBuiltInClient);
        Assert.True(launcher.UsesOwnGoogleClient);
        Assert.Contains("no Google client of its own", launcher.CloudBackupStatus);

        // And the notice under the greyed-out option says why, rather than leaving it a dead control.
        Assert.Contains("greyed out", launcher.BuiltInClientNotice);
    }

    [Fact]
    public void TheBringYourOwnOptionIsWritableSoTheRadioPairWorksBothWays() {
        // The bring-your-own radio is bound to a computed property, the way the patch server's two radios are.
        // A get-only one would leave that option impossible to re-select once the other had been chosen, and a
        // build does not catch it - a binding is compiled against the type, not against the setter. So the
        // setter is asserted here, where it is the thing under test rather than an implementation detail.
        var property = typeof(MainWindowViewModel).GetProperty(nameof(MainWindowViewModel.UsesOwnGoogleClient));

        Assert.NotNull(property);
        Assert.True(property!.CanWrite);
        Assert.True(property.CanRead);
    }

    [Fact]
    public void ChoosingTheBuiltInSideIsRefusedWhileThereIsNoClient() {
        using var workspace = new TempWorkspace();
        var launcher = Launcher(workspace);

        // The computed setter routes through the same guard as the real setting, so the refusal holds whichever
        // radio was pressed - and with no built-in client there is no way off the bring-your-own side, which is
        // the correct state for this build.
        launcher.UsesOwnGoogleClient = false;

        Assert.True(launcher.UsesOwnGoogleClient);
        Assert.False(launcher.CloudBackupUsesBuiltInClient);
    }

    [Fact]
    public void TheSignInNeedsAClientIdAndSaysSoWhenItHasNone() {
        using var workspace = new TempWorkspace();
        var launcher = Launcher(workspace);

        // Nothing to sign in with yet, so the button is disabled rather than failing when pressed.
        Assert.False(launcher.CanSignInToCloud);

        launcher.CloudBackupClientId = "mine.apps.googleusercontent.com";

        Assert.True(launcher.CanSignInToCloud);

        // And a box holding nothing but whitespace is an empty box: the id is sent without the spaces around it,
        // so one that is only spaces is no client id at all rather than a sign-in that fails at Google.
        launcher.CloudBackupClientId = "   ";

        Assert.False(launcher.CanSignInToCloud);
    }

    [Fact]
    public void TheChosenClientSurvivesOnDisk() {
        using var workspace = new TempWorkspace();
        var path = workspace.Path("launcher.json");

        var store = new LauncherSettingsStore(path);
        store.Settings.CloudBackupUsesBuiltInClient = true;
        store.Settings.CloudBackupClientId = "mine.apps.googleusercontent.com";
        store.Save();

        // The choice is remembered, so a launcher that was set up once does not ask again.
        var reloaded = new LauncherSettingsStore(path).Settings;

        Assert.True(reloaded.CloudBackupUsesBuiltInClient);
        Assert.Equal("mine.apps.googleusercontent.com", reloaded.CloudBackupClientId);
    }

    [Fact]
    public void ThePublicClientWorkflowAsksForTheRightThings() {
        var url = GoogleOAuth.AuthorizationUrl(
            "client.apps.googleusercontent.com", "http://127.0.0.1:51234/", "a-challenge");

        // The app's own folder and nothing else, offline so a refresh token comes back and the upload can run
        // while nobody is watching, consent every time so a repeat sign-in still returns one, and the PKCE
        // challenge - which is what stands in for the client secret a public client does not have.
        Assert.Contains("scope=https%3A%2F%2Fwww.googleapis.com%2Fauth%2Fdrive.appdata", url);
        Assert.Contains("access_type=offline", url);
        Assert.Contains("prompt=consent", url);
        Assert.Contains("code_challenge=a-challenge", url);
        Assert.Contains("code_challenge_method=S256", url);
        Assert.Contains("redirect_uri=http%3A%2F%2F127.0.0.1%3A51234%2F", url);

        // No secret anywhere in the sign-in URL: it is not a thing the browser is ever told.
        Assert.DoesNotContain("client_secret", url);
    }

    [Fact]
    public void APublicClientsCredentialsHaveNoSecret() {
        // What the built-in workflow builds: a client id and the refresh token, and no secret at all. Blank is
        // the signal to send none, rather than an empty secret Google would refuse.
        var credentials = new GoogleCredentials("client.apps.googleusercontent.com", null, "refresh");

        Assert.Null(credentials.ClientSecret);
    }

    /// <summary>
    /// A launcher pointed at two scratch folders and no Imlight, so nothing starts behind the test - the same
    /// shape the launcher's own database tests use.
    /// </summary>
    private static MainWindowViewModel Launcher(TempWorkspace workspace) {
        var store = new LauncherSettingsStore(workspace.Path("launcher.json"));
        store.Settings.ClientPath = workspace.CreateDirectory("game");
        store.Settings.ServerPath = workspace.CreateDirectory("server");
        store.Settings.CheckForUpdatesOnStartup = false;
        store.Save();

        return new MainWindowViewModel(store, fetchArt: false);
    }
}
