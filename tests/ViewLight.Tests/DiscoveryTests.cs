using System.Collections.ObjectModel;
using System.Globalization;
using ViewLight.Models;
using ViewLight.Services;
using Xunit;

namespace ViewLight.Tests;

/// <summary>
/// Discovery: which zones a wizard has been in, and which worlds those zones belong to.
/// </summary>
/// <remarks>
/// The log names a zone twice over as the server loads it - once by its path, which begins with the world,
/// and once by the name the game calls it - and nothing else the launcher can read joins the two. These
/// tests are about that pairing: that it is learned from the server's own lines, that it is remembered, and
/// that nothing is attributed to a world the server never said.
/// </remarks>
public class DiscoveryTests {

    private static readonly DateTimeOffset At = new(2026, 10, 14, 20, 0, 0, 0, TimeSpan.FromHours(-4));

    private const ulong AccountId = 42;
    private const ulong CharId = 1;
    private const string Wizard = "Jordan DeathBane";

    private static string Stamp(DateTimeOffset at)
        => at.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture);

    // ------------------------------------------------------------------ the lines the server writes

    private static string SignIn()
        => $"{Stamp(At)} [INF] Sending wizard {Wizard} to game server 127.0.0.1:12333.";

    private static string LoadStarted(string path)
        => $"{Stamp(At)} [INF] Game world starting zone load: {path}";

    private static string ZoneCreated(string path)
        => $"{Stamp(At)} [INF] Game world created new zone: {path}";

    private static string Loaded(string zone)
        => $"{Stamp(At)} [INF] Zone {zone} loaded in 170ms.";

    private static string Entered(string zone, string wizard = Wizard)
        => $"{Stamp(At)} [DBG] {wizard} added to zone {zone}.";

    private const string UnicornPath = "WizardCity/WC_Streets/WC_Unicorn";
    private const string Unicorn = "WizardZone_UnicornWay";
    private const string Commons = "WizardZone_TheCommons";

    // ------------------------------------------------------------------ reading them

    [Fact]
    public void AZoneLoadIsReadFromEitherWayTheServerSaysIt() {
        var starting = Assert.IsType<ZoneLoadStarted>(Assert.Single(ServerLogFacts.Parse(LoadStarted(UnicornPath))));
        Assert.Equal(UnicornPath, starting.Path);
        Assert.Equal("WizardCity", starting.World);

        var created = Assert.IsType<ZoneLoadStarted>(Assert.Single(ServerLogFacts.Parse(ZoneCreated(UnicornPath))));
        Assert.Equal(UnicornPath, created.Path);
    }

    [Fact]
    public void TheZoneALoadIsForIsReadFromTheLineThatEndsIt() {
        var named = Assert.IsType<ZoneNamed>(Assert.Single(ServerLogFacts.Parse(Loaded(Unicorn))));

        Assert.Equal(Unicorn, named.Zone);
        Assert.Equal(At, named.At);

        // The zone's own loading lines are not a name: they are the path again, which says nothing new.
        Assert.Empty(ServerLogFacts.Parse($"{Stamp(At)} [DBG] Zone {UnicornPath} begins load."));
        Assert.Empty(ServerLogFacts.Parse($"{Stamp(At)} [DBG] Zone {UnicornPath} client data gathered."));
    }

    [Fact]
    public void AWorldIsTheFirstSegmentOfAZonePath() {
        Assert.Equal("WizardCity", ZoneLoadStarted.WorldOf(UnicornPath));
        Assert.Equal("Krokotopia", ZoneLoadStarted.WorldOf("Krokotopia/KT_Hub"));
        Assert.Equal(string.Empty, ZoneLoadStarted.WorldOf("NoWorldHere"));
        Assert.Equal(string.Empty, ZoneLoadStarted.WorldOf("/LeadingSlash"));
    }

    [Fact]
    public void AZoneNameIsSaidTheWayTheGameSaysIt() {
        Assert.True(ZoneNames.IsNamed(Unicorn));
        Assert.Equal("Unicorn Way", ZoneNames.NameOf(Unicorn));
        Assert.Equal("The Commons", ZoneNames.NameOf(Commons));
        Assert.Equal("Golem Court", ZoneNames.NameOf("WizardZone_GolemCourt"));
        Assert.Equal("Olde Town", ZoneNames.NameOf("WizardZone_OldeTown"));
        Assert.Equal("Firecat Alley", ZoneNames.NameOf("WizardZone_FirecatAlley"));

        // A zone the server only numbered is an instance rather than a place, and has no name to give.
        Assert.False(ZoneNames.IsNamed("WizardZone_00000878"));
        Assert.Equal(string.Empty, ZoneNames.NameOf("WizardZone_00000878"));

        Assert.False(ZoneNames.IsNamed("WizardCity/WC_Hub"));
        Assert.False(ZoneNames.IsNamed("WizardZone_"));
        Assert.False(ZoneNames.IsNamed(null));
    }

    [Fact]
    public void AWorldLabelIsSpelledTheSameWay() {
        Assert.Equal("Wizard City", AchievementCatalog.WorldName("WizardCity"));
        Assert.Equal("Krokotopia", AchievementCatalog.WorldName("Krokotopia"));
        Assert.Equal("Dragon Spire", AchievementCatalog.WorldName("DragonSpire"));
        Assert.Equal("Moo Shu", AchievementCatalog.WorldName("MooShu"));
        Assert.Equal("Housing Wizard Commons Apt", AchievementCatalog.WorldName("Housing_WizardCommonsApt"));
    }

    // ------------------------------------------------------------------ learning it, and what it credits

    private sealed class Bench : IDisposable {

        private readonly TempWorkspace _workspace = new();

        public Bench() {
            Path = _workspace.Path("profiles.json");
            Store = new ProfileStore(Path);
            Ledger = new ProfileLedger(Store);
            Ledger.NoteAccounts([new PlayerAccount(AccountId, "jordan")]);
        }

        public string Path { get; }

        public ProfileStore Store { get; }

        public ProfileLedger Ledger { get; }

        public LauncherProfile Profile => Store.GetOrCreate(AccountId, "jordan");

        /// <summary>The database's answer for the account being signed in, which is what matches the name.</summary>
        public void SignIn()
            => Ledger.ApplyDatabase(
                new PlayerProfileData(
                    [new PlayerWizardRecord(AccountId, CharId, 1, 0, [])],
                    [new PlayerOnlineRecord(AccountId, CharId, "WizardZone_TheCommons", "Centaur")],
                    [],
                    null),
                At);

        public void Apply(params string[] lines) => Ledger.ApplyLogLines(lines);

        /// <summary>The same store re-read from disk, as a later launcher run would see it.</summary>
        public ProfileLedger AfterRestart()
            => new(new ProfileStore(Path));

        public void Dispose() => _workspace.Dispose();
    }

    /// <summary>A sign-in, the database's answer, and one zone walked into.</summary>
    private static void WalkInto(Bench bench, string zone, string path) {
        bench.Apply(SignIn());
        bench.SignIn();
        bench.Apply(LoadStarted(path), Loaded(zone), Entered(zone));
    }

    [Fact]
    public void WalkingIntoAZoneDiscoversItsWorld() {
        using var bench = new Bench();

        WalkInto(bench, Unicorn, UnicornPath);

        Assert.Equal([Unicorn], bench.Profile.ZonesVisited);
        Assert.Equal(["WizardCity"], bench.Profile.WorldsVisited);
        Assert.True(bench.Profile.HasVisitedWorld("WizardCity"));

        // And the pairing is learned rather than looked up: the launcher was told which world that zone is
        // in by the server's own lines.
        Assert.Equal("WizardCity", bench.Ledger.ZoneWorlds[Unicorn]);
        Assert.Equal(["WizardCity"], bench.Ledger.LearnedWorlds);
    }

    [Fact]
    public void AWorldLearnedLateIsCreditedToTheZoneAlreadyVisited() {
        // The zone visit and the load that explains it are separate lines, and either can be read first - a
        // launcher started mid-session sees the visit with no idea which world it was in. The badge must not
        // hang on which of the two it happened to read first.
        using var bench = new Bench();

        bench.Apply(SignIn());
        bench.SignIn();
        bench.Apply(Entered(Unicorn));

        Assert.Equal([Unicorn], bench.Profile.ZonesVisited);
        Assert.Empty(bench.Profile.WorldsVisited);

        bench.Apply(LoadStarted(UnicornPath), Loaded(Unicorn));

        Assert.Equal(["WizardCity"], bench.Profile.WorldsVisited);
    }

    [Fact]
    public void AZoneTheServerOnlyNumberedIsNotAWorld() {
        // A house, a gauntlet run, an instance: somewhere somebody really was, and not a place with a name or
        // a world of its own.
        using var bench = new Bench();

        WalkInto(bench, "WizardZone_00000878", "WizardCity/Interiors/WC_Headmistress_House");

        Assert.Equal(["WizardZone_00000878"], bench.Profile.ZonesVisited);
        Assert.Empty(bench.Profile.WorldsVisited);
        Assert.Empty(bench.Ledger.LearnedWorlds);
    }

    [Fact]
    public void TwoZonesOfTheSameWorldAreOneDiscovery() {
        using var bench = new Bench();

        WalkInto(bench, Unicorn, UnicornPath);
        bench.Apply(LoadStarted("WizardCity/WC_Hub"), Loaded(Commons), Entered(Commons));

        Assert.Equal(2, bench.Profile.ZonesVisited.Count);
        Assert.Equal(["WizardCity"], bench.Profile.WorldsVisited);
    }

    [Fact]
    public void WhatWasLearnedIsRememberedForTheNextRun() {
        using var bench = new Bench();

        WalkInto(bench, Unicorn, UnicornPath);

        var later = bench.AfterRestart();

        Assert.Equal("WizardCity", later.ZoneWorlds[Unicorn]);
        Assert.Equal(["WizardCity"], later.LearnedWorlds);
    }

    [Fact]
    public void TheWorldBadgeIsEarnedByBeingInOne() {
        try {
            using var bench = new Bench();

            // The badge only exists once the world is known, which is the point: it is one of this server's
            // worlds rather than one of a list written down in the launcher.
            Assert.Null(AchievementCatalog.ById("world-wizardcity"));

            WalkInto(bench, Unicorn, UnicornPath);

            var badge = AchievementCatalog.ById("world-wizardcity");

            Assert.NotNull(badge);
            Assert.Equal("Discover Wizard City", badge!.Name);
            Assert.Equal(AchievementCategory.Exploration, badge.Category);
            Assert.True(badge.Earned(bench.Profile));
            Assert.True(bench.Profile.Has("world-wizardcity"));

            // A world nobody has been in is not earned, and the zone badge beside it still works the way it
            // always did.
            Assert.False(badge.Earned(new LauncherProfile { AccountId = 1 }));
        }
        finally {
            // The catalog is process-wide, so the worlds it was taught here are taken back out again.
            AchievementCatalog.UseWorlds([]);
        }
    }

    // ------------------------------------------------------------------ what the profile screen shows

    [Fact]
    public void TheDiscoveryGroupShowsItsWorldsAndTheZonesFoundInThem() {
        using var bench = new Bench();

        WalkInto(bench, Unicorn, UnicornPath);
        bench.Apply(LoadStarted("WizardCity/WC_Hub"), Loaded(Commons), Entered(Commons));
        bench.Apply(LoadStarted("Krokotopia/KT_Hub"), Loaded("WizardZone_TheOasis"), Entered("WizardZone_TheOasis"));

        var badges = ProfileBadge.For(bench.Profile, debugLogging: true, databaseAnswers: true);
        var discovery = ProfileBadgeGroup.For(bench.Profile, badges, bench.Ledger.ZoneWorlds)
            .Single(g => g.Category == AchievementCategory.Exploration);

        Assert.Equal("Discovery", discovery.Name);
        Assert.Equal(["WizardCity", "Krokotopia"], discovery.Worlds.Select(w => w.World));

        var wizardCity = discovery.Worlds[0];

        Assert.Equal("Wizard City", wizardCity.Name);
        Assert.Equal(["Unicorn Way", "The Commons"], wizardCity.Zones);
        Assert.Equal("2 zones", wizardCity.ZonesText);
        Assert.Equal("Unicorn Way · The Commons", wizardCity.ZoneList);
        Assert.True(wizardCity.HasZones);
    }

    [Fact]
    public void AWorldNothingCanBeNamedInSaysSoRatherThanListingNothing() {
        var world = new ProfileWorld("WizardCity", []);

        Assert.Equal("Wizard City", world.Name);
        Assert.Equal("no zones named yet", world.ZonesText);
        Assert.False(world.HasZones);
        Assert.Equal(string.Empty, world.ZoneList);

        Assert.Equal("1 zone", new ProfileWorld("WizardCity", ["Unicorn Way"]).ZonesText);
    }

    [Fact]
    public void OnlyDiscoveryCarriesWorlds() {
        using var bench = new Bench();

        WalkInto(bench, Unicorn, UnicornPath);

        var badges = ProfileBadge.For(bench.Profile, debugLogging: true, databaseAnswers: true);

        foreach (var group in ProfileBadgeGroup.For(bench.Profile, badges, bench.Ledger.ZoneWorlds)) {
            Assert.Equal(group.Category == AchievementCategory.Exploration, group.HasWorlds);
        }
    }

    [Fact]
    public void TheWorldsAreShownInTheOrderTheyWereDiscovered() {
        using var bench = new Bench();

        WalkInto(bench, "WizardZone_TheOasis", "Krokotopia/KT_Hub");
        bench.Apply(LoadStarted(UnicornPath), Loaded(Unicorn), Entered(Unicorn));

        Assert.Equal(["Krokotopia", "WizardCity"], bench.Profile.WorldsVisited);
    }

    [Fact]
    public void LookingAtTheScreenAgainLeavesTheWorldsWhereTheyWere() {
        using var bench = new Bench();

        WalkInto(bench, Unicorn, UnicornPath);

        var badges = ProfileBadge.For(bench.Profile, debugLogging: true, databaseAnswers: true);
        var groups = new ObservableCollection<ProfileBadgeGroup>(
            ProfileBadgeGroup.For(bench.Profile, badges, bench.Ledger.ZoneWorlds));

        var discovery = groups.Single(g => g.Category == AchievementCategory.Exploration);

        ProfileBadgeGroup.Sync(groups, ProfileBadgeGroup.For(bench.Profile, badges, bench.Ledger.ZoneWorlds));

        Assert.Same(discovery, groups.Single(g => g.Category == AchievementCategory.Exploration));
        Assert.Single(discovery.Worlds);
    }
}
