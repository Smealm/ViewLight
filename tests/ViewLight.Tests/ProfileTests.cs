using System.Globalization;
using ViewLight.Models;
using ViewLight.Services;
using ViewLight.ViewModels;
using Xunit;

namespace ViewLight.Tests;

/// <summary>The launcher's own profile file: what it keeps, and what it does when that file makes no sense.</summary>
public class ProfileStoreTests : IDisposable {

    private readonly TempWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    private ProfileStore NewStore() => new(_workspace.Path("profiles.json"));

    [Fact]
    public void ProfilesSurviveARestart() {
        var store = NewStore();
        var profile = store.GetOrCreate(42, "exampleuser");
        profile.PlaytimeSeconds = 3600;
        profile.ZonesVisited.Add("WizardZone_TheCommons");
        profile.Achievements.Add(new EarnedAchievement("first-steps", DateTimeOffset.UnixEpoch, "a line"));
        store.Save();

        var reloaded = NewStore().GetOrCreate(42, "exampleuser");

        Assert.Equal(3600, reloaded.PlaytimeSeconds);
        Assert.Contains("WizardZone_TheCommons", reloaded.ZonesVisited);
        Assert.True(reloaded.Has("first-steps"));
    }

    [Fact]
    public void AProfileIsTheSameProfileWhenTheAccountIsRenamed() {
        // The account id is the key, so a rename is not a new profile with no history.
        var store = NewStore();
        store.GetOrCreate(42, "oldname").PlaytimeSeconds = 60;
        store.Save();

        var renamed = NewStore().GetOrCreate(42, "newname");

        Assert.Single(NewStore().Profiles);
        Assert.Equal(60, renamed.PlaytimeSeconds);
        Assert.Equal("newname", renamed.Username);
    }

    [Fact]
    public void AProfileFileThatMakesNoSenseStartsEmptyRatherThanStoppingTheLauncher() {
        _workspace.WriteFile("profiles.json", "{ this is not json");

        Assert.Empty(NewStore().Profiles);
    }

    [Fact]
    public void NothingWrittenByAnotherVersionIsActedOn() {
        // A file carrying a field this version does not know about - which is what a newer launcher writes.
        _workspace.WriteFile("profiles.json",
            """{"Profiles":[{"AccountId":7,"Username":"someone","PlaytimeSeconds":90,"SomethingNew":true}]}""");

        var profile = Assert.Single(NewStore().Profiles);

        Assert.Equal(90, profile.PlaytimeSeconds);
        Assert.Equal("someone", profile.Username);
    }

    [Fact]
    public async Task APictureIsKeptSoTheProfileKeepsIt() {
        // A picture picked out of a downloads folder has to survive that folder being cleaned out, which is
        // also what keeps a portable launcher's pictures portable.
        var store = NewStore();
        var profile = store.GetOrCreate(42, "exampleuser");
        var source = _workspace.WriteFile("downloads/pick.png", new byte[] { 1, 2, 3 });

        var name = await store.AdoptImageAsync(profile, source, banner: false);

        Assert.Equal("avatar.png", name);
        Assert.Equal("avatar.png", profile.AvatarFile);

        var kept = JsonProfilePersistence.ImagePath(profile.AccountId, profile.AvatarFile);
        Assert.NotNull(kept);
        Assert.True(File.Exists(kept));

        // And the bytes are the ones picked, not a name that leads nowhere.
        var keptPicture = await store.ReadImageAsync(profile.AccountId, profile.AvatarFile);
        Assert.Equal(new byte[] { 1, 2, 3 }, keptPicture.Bytes);
        Assert.Null(keptPicture.Problem);

        // The banner is a separate slot: replacing one must not overwrite the other.
        var banner = await store.AdoptImageAsync(profile,
            _workspace.WriteFile("downloads/wide.jpg", new byte[] { 4, 5 }), banner: true);

        Assert.Equal("banner.jpg", banner);
        Assert.Equal("avatar.png", profile.AvatarFile);
    }

    [Fact]
    public async Task ReplacingAPictureKeepsItsName() {
        // What the screen reads a picture by is its name, and a replacement is under the same one - which is why
        // the name alone cannot say whether the picture on screen is the current one. The screen's own count of
        // replacements is what says so: see ProfilePictureCacheTests.
        var store = NewStore();
        var profile = store.GetOrCreate(42, "exampleuser");

        var first = await store.AdoptImageAsync(
            profile, _workspace.WriteFile("downloads/one.png", new byte[] { 1 }), banner: false);

        var second = await store.AdoptImageAsync(
            profile, _workspace.WriteFile("downloads/two.png", new byte[] { 2 }), banner: false);

        Assert.Equal(first, second);
        Assert.Equal("avatar.png", second);

        // And the bytes behind that name are the new ones rather than the first ones left there.
        Assert.Equal(new byte[] { 2 }, (await store.ReadImageAsync(42, "avatar.png")).Bytes);
    }

    [Fact]
    public void AProfileWithNoPictureHasNothingToShow() {
        Assert.Null(JsonProfilePersistence.ImagePath(42, null));
        Assert.Null(JsonProfilePersistence.ImagePath(42, "  "));
    }
}

/// <summary>
/// The ledger: what the running server's log and database do to a profile.
/// </summary>
/// <remarks>
/// These are the rules that decide who a badge belongs to, and most of them are about refusing to give one
/// out - the log names wizards and duels, not accounts, so the interesting cases are the ones where it
/// cannot be told whose something was.
/// </remarks>
public class ProfileLedgerTests : IDisposable {

    private const ulong AccountId = 5479226295515708948;
    private const ulong OtherAccountId = 5114222425397376613;
    private const ulong CharId = 7715317;

    private static readonly DateTimeOffset Start = new(2026, 10, 13, 16, 33, 36, TimeSpan.FromHours(-4));

    private readonly TempWorkspace _workspace = new();
    private readonly ProfileStore _store;
    private readonly ProfileLedger _ledger;

    public ProfileLedgerTests() {
        // The launcher's data root is redirected into the scratch folder by TempWorkspace, so an image or a
        // saved profile never lands in the real user profile.
        _store = new ProfileStore(_workspace.Path("profiles.json"));
        _ledger = new ProfileLedger(_store);
        _ledger.NoteAccounts([new PlayerAccount(AccountId, "jordan")]);
    }

    public void Dispose() => _workspace.Dispose();

    private LauncherProfile Profile => _store.GetOrCreate(AccountId, "jordan");

    private static string Stamp(DateTimeOffset at)
        => at.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture);

    private static string SignIn(string wizard = "Jordan DeathBane")
        => $"{Stamp(Start)} [INF] Sending wizard {wizard} to game server 127.0.0.1:12333.";

    private static string Zone(string zone, DateTimeOffset at, string wizard = "Jordan DeathBane")
        => $"{Stamp(at)} [DBG] {wizard} added to zone {zone}.";

    private static string ConnectionLost(DateTimeOffset at)
        => $"{Stamp(at)} [INF] Imlight.Game.Centaur lost connection to 10.0.0.246";

    private static string DuelWon(DateTimeOffset at)
        => $"{Stamp(at)} [DBG] Duel 1 | Duel ended. Players win.";

    private static string Command(string text, DateTimeOffset at)
        => $"{Stamp(at)} [INF] jordan Uses command: {text}";

    private static PlayerProfileData NobodyOnline => new([], [], [], null);

    private static PlayerProfileData Online(
        DateTimeOffset at, ulong accountId = AccountId, ulong charId = CharId,
        IReadOnlyList<PlayerWizardRecord>? wizards = null)
        => new(wizards ?? [], [new PlayerOnlineRecord(accountId, charId, "WizardZone_UnicornWay", "Centaur")], [],
            null);

    private static PlayerProfileData Wizards(params PlayerWizardRecord[] records)
        => new(records, [], [], null);

    private static PlayerWizardRecord Wizard(int level = 1, long gold = 0, int quests = 0,
        ulong accountId = AccountId, ulong charId = CharId)
        => new(accountId, charId, level, gold, [.. Enumerable.Range(0, quests).Select(i => $"Quest-{i}")]);

    // ------------------------------------------------------------------ playtime

    [Fact]
    public void PlaytimeIsCountedFromTheSessionPlayStarted() {
        _ledger.BeginSession(AccountId, Start);

        // The sign-in is counted at the moment Play starts the game, before a second of it has been played.
        Assert.Equal(1, Profile.SessionCount);
        Assert.Equal(0, Profile.PlaytimeSeconds);
        Assert.Equal(Start, Profile.FirstPlayedAt);

        _ledger.CreditSessionTime(Start.AddSeconds(10));

        Assert.Equal(10, Profile.PlaytimeSeconds);
        Assert.Equal(1, Profile.SessionCount);
    }

    [Fact]
    public void NothingIsCountedWhileNobodyIsPlaying() {
        // The launcher open on the profile screen, its own read-only view of the server's database being
        // read, the account still listed as in the game: none of that is anybody playing, and none of it
        // may count as a sign-in or as a second of playtime.
        _ledger.ApplyDatabase(Online(Start), Start);
        _ledger.ApplyDatabase(Online(Start.AddSeconds(10)), Start.AddSeconds(10));
        _ledger.CreditSessionTime(Start.AddSeconds(10));

        Assert.Equal(0, Profile.SessionCount);
        Assert.Equal(0, Profile.PlaytimeSeconds);
        Assert.Null(Profile.FirstPlayedAt);
        Assert.Null(Profile.LastPlayedAt);
    }

    [Fact]
    public void TimeNobodyWasWatchingIsNotCountedAsPlaytime() {
        // A session the launcher stopped watching - it was closed, or the machine slept - is credited one
        // look's worth at most: the game was running at both ends, and nothing observed the two hours
        // between them. One sign-in, not two: the session never ended, the launcher was simply not there.
        _ledger.BeginSession(AccountId, Start);
        _ledger.CreditSessionTime(Start.AddHours(2));

        Assert.Equal(ProfileLedger.MaxCreditedGap.TotalSeconds, Profile.PlaytimeSeconds);
        Assert.Equal(1, Profile.SessionCount);
    }

    [Fact]
    public void EndingTheSessionStopsTheClock() {
        _ledger.BeginSession(AccountId, Start);
        _ledger.CreditSessionTime(Start.AddSeconds(5));
        _ledger.EndSession(Start.AddSeconds(8));

        Assert.Equal(8, Profile.PlaytimeSeconds);
        Assert.Null(Profile.LastSeenOnlineAt);
        Assert.Equal(Start.AddSeconds(8), Profile.LastPlayedAt);
        Assert.False(_ledger.SessionIsOn);

        // And nothing more is counted while it is over.
        _ledger.CreditSessionTime(Start.AddSeconds(80));

        Assert.Equal(8, Profile.PlaytimeSeconds);
    }

    [Fact]
    public void TheGameConnectionEndingStopsCountingThroughTheGap() {
        _ledger.BeginSession(AccountId, Start);
        _ledger.CreditSessionTime(Start.AddSeconds(5));

        // The server's own line, to the second - and it is not the end of the session, because the game's
        // client is still running: somebody is at the character screen, or coming back in.
        _ledger.ApplyLogLines([ConnectionLost(Start.AddSeconds(8))]);

        Assert.Equal(8, Profile.PlaytimeSeconds);
        Assert.Null(Profile.LastSeenOnlineAt);
        Assert.True(_ledger.SessionIsOn);

        // Back in the game: the minutes spent out of it are not claimed, and counting resumes from here.
        _ledger.CreditSessionTime(Start.AddMinutes(2));
        Assert.Equal(8, Profile.PlaytimeSeconds);

        _ledger.CreditSessionTime(Start.AddMinutes(2).AddSeconds(10));

        Assert.Equal(18, Profile.PlaytimeSeconds);
        Assert.Equal(1, Profile.SessionCount);
    }

    [Fact]
    public void ADatabaseThatDidNotAnswerChangesNothing() {
        _ledger.BeginSession(AccountId, Start);
        _ledger.CreditSessionTime(Start.AddSeconds(10));

        _ledger.ApplyDatabase(PlayerProfileData.Unreadable("the database did not answer"), Start.AddSeconds(20));

        // A read that failed says nothing about the session either way: what was credited stays credited,
        // and the session is still the one Play started.
        Assert.Equal(10, Profile.PlaytimeSeconds);
        Assert.True(_ledger.SessionIsOn);
    }

    // ------------------------------------------------------------------ who gets credited

    [Fact]
    public void AWizardNameIsMatchedToTheAccountThatSignedIn() {
        // The name arrives in the log, the account and character in the database, and neither side has the
        // other's half. Signing in is what makes them the same event.
        _ledger.ApplyLogLines([SignIn()]);
        _ledger.ApplyLogLines([Zone("WizardZone_UnicornWay", Start.AddSeconds(6))]);

        // Nothing is credited while the character behind the name is unknown.
        Assert.Empty(Profile.ZonesVisited);

        _ledger.ApplyDatabase(Online(Start.AddSeconds(5), wizards: [Wizard()]), Start.AddSeconds(5));

        var wizard = Assert.Single(Profile.Wizards);
        Assert.Equal("Jordan DeathBane", wizard.Name);

        // And what the wizard did in those first seconds - which is exactly when a new character walks out
        // of the tutorial - is folded in rather than thrown away.
        Assert.Contains("WizardZone_UnicornWay", Profile.ZonesVisited);
        Assert.True(Profile.Has("unicorn-way"));
    }

    [Fact]
    public void ZonesAreCountedOnceEach() {
        _ledger.ApplyLogLines([SignIn()]);
        _ledger.ApplyDatabase(Online(Start.AddSeconds(1), wizards: [Wizard()]), Start.AddSeconds(1));

        _ledger.ApplyLogLines([
            Zone("WizardZone_TheCommons", Start.AddSeconds(6)),
            Zone("WizardZone_TheCommons", Start.AddSeconds(20)),
            Zone("WizardZone_GolemCourt", Start.AddSeconds(40)),
        ]);

        Assert.Equal(2, Profile.ZonesVisited.Count);
        Assert.True(Profile.Has("the-commons"));
        Assert.False(Profile.Has("ravenwood"));
    }

    [Fact]
    public void AWizardNobodyKnowsIsNotCreditedToAnyone() {
        // A name this launcher has never seen an account for - someone else's wizard, or their server.
        _ledger.ApplyLogLines([Zone("WizardZone_UnicornWay", Start, wizard: "Somebody Else")]);

        Assert.Empty(Profile.ZonesVisited);
        Assert.Equal(0, Profile.SessionCount);
    }

    [Fact]
    public void ADuelIsCreditedOnlyWhenOneProfileCouldHaveFoughtIt() {
        _ledger.ApplyDatabase(Online(Start), Start);

        _ledger.ApplyLogLines([DuelWon(Start.AddMinutes(2))]);

        Assert.Equal(1, Profile.DuelsWon);
        Assert.True(Profile.Has("first-victory"));
        Assert.Equal(0, _ledger.UncreditedDuels);
    }

    [Fact]
    public void TwoProfilesSignedInAtOnceMeansTheDuelGoesToNeither() {
        // The log says the players' side won, never which player. Splitting it, or giving it to whoever was
        // seen last, would be inventing an achievement.
        _ledger.NoteAccounts([new PlayerAccount(OtherAccountId, "adam")]);
        var both = new PlayerProfileData(
            [],
            [new PlayerOnlineRecord(AccountId, CharId, "z", "Centaur"),
             new PlayerOnlineRecord(OtherAccountId, 999, "z", "Centaur")],
            [],
            null);

        _ledger.ApplyDatabase(both, Start);
        _ledger.ApplyLogLines([DuelWon(Start.AddMinutes(2))]);

        Assert.Equal(0, Profile.DuelsWon);
        Assert.Equal(0, _store.GetOrCreate(OtherAccountId, "adam").DuelsWon);
        Assert.Equal(1, _ledger.UncreditedDuels);
    }

    [Fact]
    public void ACommandCountsOnceEvenWhenBothTheLogAndTheServerHaveIt() {
        _ledger.ApplyLogLines([Command("account info jordan", Start), Command("level 60", Start.AddSeconds(5))]);

        Assert.Equal(2, Profile.CommandsSeenHere);

        // The server's own record holds more than this launcher watched - it goes back further - and the two
        // are the same commands seen twice, so the larger count is the true one rather than their sum.
        _ledger.ApplyDatabase(
            new PlayerProfileData([], [], [.. Enumerable.Range(0, 6).Select(
                i => new PlayerCommandRecord(AccountId, CharId, Start.AddMinutes(i), $"command {i}"))], null),
            Start.AddMinutes(10));

        Assert.Equal(6, Profile.CommandsOnRecord);
        Assert.Equal(6, Profile.CommandsUsed);
        Assert.Equal(ProfileStanding.CommandsUsed, Profile.Standing);
        Assert.Equal("command 5", Profile.LastCommand);
    }

    [Fact]
    public void AProfileNothingHasBeenUsedOnStaysClean() {
        _ledger.ApplyDatabase(Online(Start), Start);
        _ledger.ApplyLogLines([SignIn(), Zone("WizardZone_TheCommons", Start.AddSeconds(6))]);

        Assert.True(Profile.IsClean);
        Assert.Equal(0, Profile.CommandsUsed);
        Assert.Null(Profile.LastCommand);
    }

    // ------------------------------------------------------------------ achievements

    [Fact]
    public void LevelQuestsAndGoldComeFromTheDatabaseWithoutBorrowingALine() {
        // One pass over the database earns several badges at once, so there is no line that is about any
        // one of them: nothing is quoted, and a badge with nothing of its own to quote says what it takes
        // rather than being handed a sentence about the account as a whole.
        _ledger.ApplyDatabase(Wizards(Wizard(level: 12, gold: 12_345, quests: 3)), Start);

        Assert.True(Profile.Has("level-10"));
        Assert.False(Profile.Has("level-25"));
        Assert.True(Profile.Has("quest-complete"));
        Assert.True(Profile.Has("gold-10000"));
        Assert.False(Profile.Has("gold-100000"));

        var badge = Profile.Achievements.First(a => a.AchievementId == "level-10");
        Assert.Equal(string.Empty, badge.Evidence);
        Assert.Equal(Start, badge.EarnedAt);
    }

    [Fact]
    public void ABadgeIsNotTakenAwayByWhatComesAfterIt() {
        // Levels are read as the highest ever seen and gold as the most ever held, so a re-read that has not
        // caught up - or a shopping trip - cannot empty a profile of what it did.
        _ledger.ApplyDatabase(Wizards(Wizard(level: 30, gold: 250_000)), Start);
        Assert.True(Profile.Has("level-25"));

        _ledger.ApplyDatabase(Wizards(Wizard(level: 3, gold: 10)), Start.AddMinutes(5));

        Assert.True(Profile.Has("level-25"));
        Assert.Equal(30, Profile.HighestLevel);
        Assert.Equal(250_000, Profile.HighestGold);
        Assert.Single(Profile.Wizards);
    }

    [Fact]
    public void TwoWizardsOnOneAccountCountAsTwo() {
        _ledger.ApplyDatabase(Wizards(Wizard(charId: 1), Wizard(charId: 2)), Start);

        Assert.Equal(2, Profile.Wizards.Count);
        Assert.True(Profile.Has("second-wizard"));
    }

    [Fact]
    public void ByTheBookNeedsBothTheTimeAndAFaultlessRecord() {
        _ledger.NoteAccounts([new PlayerAccount(OtherAccountId, "adam")]);

        Profile.PlaytimeSeconds = (long) TimeSpan.FromHours(10).TotalSeconds;
        _store.GetOrCreate(OtherAccountId, "adam").PlaytimeSeconds = Profile.PlaytimeSeconds;
        _store.GetOrCreate(OtherAccountId, "adam").CommandsOnRecord = 1;

        _ledger.ApplyDatabase(
            new PlayerProfileData([], [
                new PlayerOnlineRecord(AccountId, CharId, "z", "Centaur"),
                new PlayerOnlineRecord(OtherAccountId, 999, "z", "Centaur")], [], null),
            Start);

        // Ten hours without a command is the badge. Ten hours with one is just ten hours.
        Assert.True(Profile.Has("by-the-book"));
        Assert.False(_store.GetOrCreate(OtherAccountId, "adam").Has("by-the-book"));
    }

    [Fact]
    public void AchievementsAreNeverGivenTwice() {
        _ledger.ApplyDatabase(Wizards(Wizard(level: 12)), Start);
        var again = _ledger.ApplyDatabase(Wizards(Wizard(level: 12)), Start.AddMinutes(1));

        Assert.DoesNotContain(again, a => a.Achievement.AchievementId == "level-10");
        Assert.Single(Profile.Achievements, a => a.AchievementId == "level-10");
    }

    [Fact]
    public void PlaytimeEarnsBadgesAsItAddsUp() {
        var at = Start;
        _ledger.BeginSession(AccountId, at);

        Assert.True(Profile.Has("first-steps"));
        Assert.False(Profile.Has("playtime-60"));

        // Ten seconds at a time, which is what the launcher does: an hour of play is an hour of looks, each
        // of them crediting the time and then judging the badges against what the profile now holds.
        for (var i = 0; i < 360; i++) {
            at = at.AddSeconds(10);
            _ledger.CreditSessionTime(at);
            _ledger.ApplyDatabase(Online(at), at);
        }

        Assert.Equal(3600, Profile.PlaytimeSeconds);
        Assert.Equal(1, Profile.SessionCount);
        Assert.True(Profile.Has("playtime-60"));
        Assert.False(Profile.Has("playtime-600"));
    }
}

/// <summary>
/// Which profiles the picker may offer. A profile is only ever a profile of an account this server has.
/// </summary>
/// <remarks>
/// The launcher used to list every profile it had ever made, which is how profiles of an account belonging
/// to a server it was no longer pointed at appeared as accounts of this one.
/// </remarks>
public class ProfileListTests {

    private static LauncherProfile Profile(ulong id, string name) => new() { AccountId = id, Username = name };

    private static PlayerAccount Account(ulong id, string name) => new(id, name);

    [Fact]
    public void OnlyAccountsThisServerHasAreOffered() {
        var remembered = new[] { Profile(1, "adam"), Profile(2, "jordan"), Profile(3, "admin") };

        var offered = MainWindowViewModel.OfferedProfiles(remembered, [Account(3, "admin")]);

        Assert.Equal(new[] { "admin" }, offered.Select(p => p.Name));
    }

    [Fact]
    public void WhatIsNotOfferedIsSaidRatherThanLost() {
        var remembered = new[] { Profile(1, "adam"), Profile(2, "jordan") };

        var unlisted = MainWindowViewModel.UnlistedProfiles(remembered, [Account(2, "jordan")]);

        Assert.Equal(new[] { "adam" }, unlisted.Select(p => p.Name));

        var text = MainWindowViewModel.DescribeUnlistedProfiles(unlisted);
        Assert.Contains("adam", text);
        Assert.Contains("this server has no such account", text);

        Assert.Equal(string.Empty, MainWindowViewModel.DescribeUnlistedProfiles([]));
    }

    [Fact]
    public void NothingIsOfferedWhileNoAccountIsKnown() {
        // No server here, or none read yet: there is nothing to sign in as, whatever is remembered.
        var remembered = new[] { Profile(1, "adam"), Profile(2, "jordan") };

        Assert.Empty(MainWindowViewModel.OfferedProfiles(remembered, []));

        var unlisted = MainWindowViewModel.UnlistedProfiles(remembered, []);
        Assert.Equal(2, unlisted.Count);
        Assert.Contains("2 profiles", MainWindowViewModel.DescribeUnlistedProfiles(unlisted));
    }
}

/// <summary>
/// What counts as playtime, as one rule: a session the launcher started, its own server still up, and its
/// own game still running.
/// </summary>
/// <remarks>
/// All three are things the launcher did rather than things it read, which is the whole point of the rule.
/// What it replaced - presence in the server's own online list - said an account was playing whenever the
/// database said it was in the game, which is a fact about the last session rather than about this one.
/// </remarks>
public class PlaytimeRuleTests {

    [Fact]
    public void TimeCountsOnlyWhileTheGameAndTheServerAreBothRunning() {
        Assert.True(MainWindowViewModel.ShouldCreditPlaytime(
            sessionIsOn: true, serverIsRunning: true, ClientState.Running));

        // The game is starting up, which is still part of the session.
        Assert.True(MainWindowViewModel.ShouldCreditPlaytime(
            sessionIsOn: true, serverIsRunning: true, ClientState.Starting));
    }

    [Fact]
    public void IdlingOnTheLauncherCountsForNothing() {
        // No session: nothing Play started, so the launcher being open - or a profile being looked at, or
        // the server's database being read - is not anybody playing.
        Assert.False(MainWindowViewModel.ShouldCreditPlaytime(
            sessionIsOn: false, serverIsRunning: true, ClientState.Running));

        // A session, but the game is not running: the client was never started, or it has exited.
        Assert.False(MainWindowViewModel.ShouldCreditPlaytime(
            sessionIsOn: true, serverIsRunning: true, ClientState.NotRunning));
        Assert.False(MainWindowViewModel.ShouldCreditPlaytime(
            sessionIsOn: true, serverIsRunning: true, ClientState.Closed));

        // A session and a game, but the server is gone: there is no world to be in.
        Assert.False(MainWindowViewModel.ShouldCreditPlaytime(
            sessionIsOn: true, serverIsRunning: false, ClientState.Running));
    }
}

/// <summary>
/// What the profile screen says when there is no profile to show: either no account at all, or the one
/// account a configured server starts with.
/// </summary>
/// <remarks>
/// This is the whole of what a launcher may claim before it has read a player database, so the words matter:
/// an account exists only once a server <i>here</i> has made one, and Imlight makes its first from the two
/// admin values in its config.
/// </remarks>
public class StarterAccountTests {

    [Fact]
    public void ALauncherWithNoServerHasNoAccountToOffer() {
        Assert.Null(MainWindowViewModel.StarterAccountName(null, null));

        var text = MainWindowViewModel.StarterAccountText(serverInstalled: false, null, null);

        Assert.Contains("no server here yet", text);
        Assert.Contains("Download the server", text);
    }

    [Fact]
    public void AServerThatNamesNoAdminAccountHasNothingToStartWith() {
        // Something is installed - the config was read - but Imlight creates its first account from these
        // two values, so half of a pair is no account rather than half of one.
        Assert.Null(MainWindowViewModel.StarterAccountName("admin", string.Empty));
        Assert.Null(MainWindowViewModel.StarterAccountName(string.Empty, "hunter2"));
        Assert.Null(MainWindowViewModel.StarterAccountName("  ", "hunter2"));

        Assert.Contains(
            "does not name an admin account",
            MainWindowViewModel.StarterAccountText(serverInstalled: true, "admin", string.Empty));
    }

    [Fact]
    public void AConfiguredServerNamesTheAccountItStartsWith() {
        Assert.Equal("admin", MainWindowViewModel.StarterAccountName(" admin ", "hunter2"));

        var text = MainWindowViewModel.StarterAccountText(serverInstalled: true, "admin", "hunter2");

        Assert.Contains("admin", text);
        Assert.Contains("first time the server boots", text);

        // One way of making another is named, and it is the server's own in-game command. The launcher
        // used to write accounts into the player database from Settings, which is an overstep it no longer
        // takes, so nothing points at a form that is not there.
        Assert.Contains(".account create $username $password", text);
        Assert.DoesNotContain("under Settings", text);
    }
}
