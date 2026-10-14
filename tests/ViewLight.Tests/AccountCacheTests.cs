using ViewLight.Models;
using ViewLight.Services;
using ViewLight.ViewModels;
using Xunit;

namespace ViewLight.Tests;

/// <summary>
/// The account picker used to be empty on every launcher start, because the player database only answers
/// while the server is running. What is remembered, and when that is replaced, is pinned down here.
/// </summary>
public class AccountCacheTests {

    private static PlayerAccount Account(ulong id, string name) => new(id, name);

    private static PlayerDatabaseContents Read(params PlayerAccount[] accounts)
        => new(accounts, [], null);

    private static PlayerDatabaseContents Unreadable(string problem)
        => PlayerDatabaseContents.Unreadable(problem);

    // ------------------------------------------------------------------ what gets remembered

    [Fact]
    public void AGoodReadReplacesWhatWasRemembered() {
        var kept = MainWindowViewModel.AccountsToRemember(
            Read(Account(2, "new")), [Account(1, "old")]);

        Assert.Equal(new[] { "new" }, kept.Select(account => account.Username));
    }

    [Fact]
    public void AGoodReadThatFindsNoAccountsIsTakenAtItsWord() {
        // The accounts really are gone, and a stale list would offer a sign-in that cannot work.
        var kept = MainWindowViewModel.AccountsToRemember(Read(), [Account(1, "gone")]);

        Assert.Empty(kept);
    }

    [Fact]
    public void ABadReadLeavesWhatWasRememberedAlone() {
        // A database that did not answer is not evidence that the accounts are gone - and an empty picker
        // is exactly the "it forgot my account" behaviour this cache exists to remove.
        var kept = MainWindowViewModel.AccountsToRemember(
            Unreadable("nothing answered"), [Account(1, "kept")]);

        Assert.Equal(new[] { "kept" }, kept.Select(account => account.Username));
    }

    // ------------------------------------------------------------------ whose accounts they are

    [Fact]
    public void ANameRememberedFromAnotherServerIsNotShown() {
        // The bug this pins down: a launcher pointed at a different server - or a portable folder that had
        // been moved - listed the accounts of the server it used to know, so a name nobody here had ever
        // created appeared to exist. An account belongs to a server, and this one has never heard of it.
        Assert.False(MainWindowViewModel.RememberedAccountsAreUsable(
            @"E:\ViewLight\Server", @"C:\Somewhere Else\Server", "admin", "hunter2"));
    }

    [Fact]
    public void AListWrittenBeforeServerPathsWereKeptIsNotShown() {
        // An older launcher.json says nothing about which server its accounts came from, so it cannot be
        // shown against any of them. The next read writes the list - and the server - down again.
        Assert.False(MainWindowViewModel.RememberedAccountsAreUsable(
            @"E:\ViewLight\Server", null, "admin", "hunter2"));
        Assert.False(MainWindowViewModel.RememberedAccountsAreUsable(
            @"E:\ViewLight\Server", string.Empty, "admin", "hunter2"));
    }

    [Fact]
    public void NothingIsRememberedUntilTheServerNamesItsAdminAccount() {
        // No server at all: no account exists yet, whatever an earlier launcher left behind.
        Assert.False(MainWindowViewModel.RememberedAccountsAreUsable(string.Empty, string.Empty, null, null));

        // A server here, but its config names no admin account: Imlight has never created one, so there is
        // no account for a remembered name to be.
        Assert.False(MainWindowViewModel.RememberedAccountsAreUsable(
            @"E:\ViewLight\Server", @"E:\ViewLight\Server", "admin", string.Empty));
        Assert.False(MainWindowViewModel.RememberedAccountsAreUsable(
            @"E:\ViewLight\Server", @"E:\ViewLight\Server", string.Empty, "hunter2"));
    }

    [Fact]
    public void TheSameServerStillShowsWhatItRemembered() {
        // Case and a trailing separator are the same folder, which is what keeps the cache useful when the
        // path was written by a different code path than the one reading it.
        Assert.True(MainWindowViewModel.RememberedAccountsAreUsable(
            @"E:\ViewLight\Server", @"e:\viewlight\server\", "admin", "hunter2"));
    }

    // ------------------------------------------------------------------ surviving a restart

    [Fact]
    public void RemembersTheAccountsAcrossARestart() {
        using var workspace = new TempWorkspace();
        var file = workspace.Path("launcher.json");

        var store = new LauncherSettingsStore(file);
        store.Settings.CachedAccounts = [Account(42, "exampleadmin"), Account(7, "second")];
        store.Save();

        var reopened = new LauncherSettingsStore(file).Settings.CachedAccounts;

        Assert.Equal(2, reopened.Count);
        Assert.Contains(reopened, account => account.AccountId == 42 && account.Username == "exampleadmin");
        Assert.Contains(reopened, account => account.AccountId == 7 && account.Username == "second");
    }

    [Fact]
    public void StartsEmptyWhenTheStoredSettingsPredateTheCache() {
        using var workspace = new TempWorkspace();

        // What an older ViewLight leaves behind: no CachedAccounts key at all.
        var file = workspace.WriteFile("launcher.json", """
            {
              "ClientPath": "",
              "ServerPath": "",
              "CheckForUpdatesOnStartup": false,
              "LastSection": "Home"
            }
            """);

        Assert.Empty(new LauncherSettingsStore(file).Settings.CachedAccounts);
    }

    [Fact]
    public void RemembersTheAccountIdItSignsInAsAlongsideTheNames() {
        // The id is what the client is signed in with - the cached username is only ever presented - so
        // both have to survive together or the picker comes back showing the wrong account.
        using var workspace = new TempWorkspace();
        var file = workspace.Path("launcher.json");

        var store = new LauncherSettingsStore(file);
        store.Settings.AutoLoginAccountId = 42;
        store.Settings.CachedAccounts = [Account(42, "exampleadmin")];
        store.Save();

        var reopened = new LauncherSettingsStore(file).Settings;

        Assert.Equal(42UL, reopened.AutoLoginAccountId);
        Assert.Equal("exampleadmin", reopened.CachedAccounts.Single().Username);
    }

    [Fact]
    public void RemembersWhichServerTheAccountsCameFrom() {
        // The name of this field is what stops a stale list from being shown: rename it and every cache on
        // disk silently stops matching, which is the bug it was added for. So it is pinned down in the file.
        using var workspace = new TempWorkspace();
        var file = workspace.Path("launcher.json");

        var store = new LauncherSettingsStore(file);
        store.Settings.CachedAccounts = [Account(42, "exampleadmin")];
        store.Settings.CachedAccountsServerPath = @"C:\Imlight";
        store.Save();

        Assert.Contains("CachedAccountsServerPath", File.ReadAllText(file));
        Assert.Equal(@"C:\Imlight", new LauncherSettingsStore(file).Settings.CachedAccountsServerPath);
    }

    [Fact]
    public void AnOlderFileWithoutTheCacheStillLoadsTheRest() {
        // A settings file written before this existed must not stop the launcher from starting.
        using var workspace = new TempWorkspace();
        var file = workspace.WriteFile("launcher.json", """
            { "AutoLoginAccountId": 99, "MachineId": 1234567890123456789 }
            """);

        var settings = new LauncherSettingsStore(file).Settings;

        Assert.Equal(99UL, settings.AutoLoginAccountId);
        Assert.Empty(settings.CachedAccounts);
    }
}
