using System.Text.Json;
using ViewLight.Models;
using ViewLight.Services;
using Xunit;

namespace ViewLight.Tests;

/// <summary>
/// The launcher's own notes as documents: one per profile, keyed by account id, and one for the zone-to-world
/// map. What matters here is that nothing is lost on the way in or out - these used to be one JSON file, and a
/// field that stops surviving the trip is a badge or an hour of playtime quietly dropped.
/// </summary>
public class LauncherDocumentTests {

    [Fact]
    public void AProfileIsOneDocumentNamedByItsAccount() {
        var profile = Profile();

        var document = LauncherProfileDocument.Build(profile);

        Assert.Equal("Profiles/41771983423143937", LauncherProfileDocument.DocumentIdFor(profile.AccountId));
        Assert.Equal("Profiles", LauncherProfileDocument.Collection);
        Assert.Equal("Profiles", document["@metadata"]!["@collection"]!.GetValue<string>());

        // One profile, one document: what makes it fetchable without reading every other profile, which is
        // the whole reason this is not a file any more.
        Assert.Equal(profile.AccountId, document["AccountId"]!.GetValue<ulong>());
    }

    [Fact]
    public void EveryFieldOfAProfileSurvivesTheDocument() {
        var profile = Profile();

        var document = LauncherProfileDocument.Build(profile);
        var read = LauncherProfileDocument.Inspect(Json(document));

        Assert.NotNull(read);

        // Compared as the model serializes itself rather than field by field: the claim being checked is that
        // the document holds everything the profile holds, and a list of fields in a test is exactly the thing
        // that would be forgotten when a field is added.
        Assert.Equal(JsonSerializer.Serialize(profile), JsonSerializer.Serialize(read));

        // And the parts a list would forget are checked anyway, because they are the ones a lost field breaks.
        Assert.Equal(profile.AccountId, read!.AccountId);
        Assert.Equal(profile.PlaytimeSeconds, read.PlaytimeSeconds);
        Assert.Equal(profile.CreaturesDefeated, read.CreaturesDefeated);
        Assert.Equal(profile.ZonesVisited, read.ZonesVisited);
        Assert.Equal(profile.WorldsVisited, read.WorldsVisited);
        Assert.Equal(profile.Wizards.Select(w => (w.Name, w.Level, w.School)), read.Wizards.Select(w => (w.Name, w.Level, w.School)));
        Assert.Equal(profile.Achievements, read.Achievements);
    }

    [Fact]
    public void AProfileWrittenByTheOldFileReadsAsAProfileDocument() {
        // What profiles.json held for one profile, which is what an existing launcher arrives with: the
        // document shape is the model's own, so importing is a copy rather than a translation - and this is
        // the check that says so.
        var profile = Profile();
        var fromFile = JsonSerializer.Serialize(profile);

        var read = LauncherProfileDocument.Inspect(JsonDocument.Parse(fromFile).RootElement);

        Assert.NotNull(read);
        Assert.Equal(JsonSerializer.Serialize(profile), JsonSerializer.Serialize(read));
    }

    [Fact]
    public void ADocumentThatWillNotReadIsSkippedRatherThanGuessedAt() {
        Assert.Null(LauncherProfileDocument.Inspect(JsonDocument.Parse("""{"AccountId":"not a number"}""").RootElement));
        Assert.Null(LauncherProfileDocument.Inspect(JsonDocument.Parse("\"a string, not a profile\"").RootElement));

        // An empty document is a profile that has said nothing yet, which is not the same thing as one that
        // could not be read.
        var empty = LauncherProfileDocument.Inspect(JsonDocument.Parse("{}").RootElement);
        Assert.NotNull(empty);
        Assert.Equal(0UL, empty!.AccountId);
    }

    [Fact]
    public void TheZoneMapIsOneDocumentOfItsOwn() {
        var document = ZoneWorldsDocument.Build(new Dictionary<string, string> { ["WizardCity/WC_Hub"] = "WizardCity" });

        Assert.Equal("ZoneWorlds/all", ZoneWorldsDocument.DocumentId);
        Assert.Equal("ZoneWorlds", ZoneWorldsDocument.Collection);
        Assert.Equal("ZoneWorlds", document["@metadata"]!["@collection"]!.GetValue<string>());
        Assert.Equal("WizardCity", document["ZoneWorlds"]!["WizardCity/WC_Hub"]!.GetValue<string>());
    }

    [Fact]
    public void TheZoneMapSurvivesTheDocument() {
        var learned = new Dictionary<string, string> {
            ["WizardCity/WC_Hub"] = "WizardCity",
            ["WizardCity/UnicornWay"] = "WizardCity",
            ["Krokotopia/KRO_Hub"] = "Krokotopia",
        };

        var read = ZoneWorldsDocument.Inspect(Json(ZoneWorldsDocument.Build(learned)));

        Assert.Equal(learned, read);

        // Compared without regard to case, the way the file it replaces was: the server's own zone names are
        // not consistently cased, and two spellings of one zone are one zone.
        Assert.Equal("Krokotopia", read["krokotopia/kro_hub"]);
    }

    [Fact]
    public void TheZoneMapIsWrittenTheSameWayHoweverItWasLearned() {
        // A backup diff should show what was learned, not the order the server happened to load it in.
        var one = ZoneWorldsDocument.Build(new Dictionary<string, string> {
            ["B/B"] = "B", ["A/A"] = "A", ["C/C"] = "C",
        }).ToJsonString();

        var other = ZoneWorldsDocument.Build(new Dictionary<string, string> {
            ["C/C"] = "C", ["A/A"] = "A", ["B/B"] = "B",
        }).ToJsonString();

        Assert.Equal(one, other);
    }

    [Fact]
    public void ADocumentWithNoZoneMapInItReadsAsNothingLearned() {
        // A missing map is a launcher that has not watched a zone load yet, which is not an error: it learns
        // them again from the server, so refusing to read would be refusing over something that fixes itself.
        Assert.Empty(ZoneWorldsDocument.Inspect(JsonDocument.Parse("{}").RootElement));
        Assert.Empty(ZoneWorldsDocument.Inspect(JsonDocument.Parse("""{"ZoneWorlds":"not a map"}""").RootElement));

        // A half-written entry is skipped rather than turned into a world called "null".
        Assert.Empty(ZoneWorldsDocument.Inspect(
            JsonDocument.Parse("""{"ZoneWorlds":{"A/A":null}}""").RootElement));
    }

    // ------------------------------------------------------------------ helpers

    private static JsonElement Json(System.Text.Json.Nodes.JsonNode node)
        => JsonDocument.Parse(node.ToJsonString()).RootElement;

    private static LauncherProfile Profile() => new() {
        AccountId = 41771983423143937,
        Username = "jordan",
        DisplayName = "Jordan",
        AvatarFile = "avatar.png",
        BannerFile = "banner.jpg",
        FirstPlayedAt = new DateTimeOffset(2026, 2, 3, 18, 4, 0, TimeSpan.Zero),
        LastPlayedAt = new DateTimeOffset(2026, 3, 9, 21, 30, 0, TimeSpan.Zero),
        PlaytimeSeconds = 7 * 3600 + 15 * 60,
        SessionCount = 12,
        LastSeenOnlineAt = new DateTimeOffset(2026, 3, 9, 21, 30, 0, TimeSpan.Zero),
        ZonesVisited = ["WizardCity/WC_Hub", "WizardCity/UnicornWay"],
        WorldsVisited = ["WizardCity", "Krokotopia"],
        DuelsWon = 9,
        DuelsLost = 2,
        CreaturesDefeated = [1234, 5678, 9012],
        BossesDefeated = 1,
        MobsDefeated = 2,
        CommandsSeenHere = 0,
        CommandsOnRecord = 0,
        LastCommand = "gold 100",
        LastCommandAt = new DateTimeOffset(2026, 3, 9, 20, 0, 0, TimeSpan.Zero),
        Wizards = [
            new ProfileWizard {
                CharId = 99,
                Name = "Jordan Stormheart",
                Level = 27,
                Gold = 5600,
                QuestsCompleted = 14,
                School = "Storm",
                LearnedSpells = [2201, 2202],
                LastSeenAt = new DateTimeOffset(2026, 3, 9, 21, 29, 0, TimeSpan.Zero),
            },
        ],
        Achievements = [
            new EarnedAchievement("first-steps", new DateTimeOffset(2026, 2, 3, 18, 9, 0, TimeSpan.Zero),
                "logged in"),
        ],
    };
}
