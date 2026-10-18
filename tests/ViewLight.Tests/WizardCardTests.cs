using ViewLight.Models;
using ViewLight.Services;
using Xunit;

namespace ViewLight.Tests;

/// <summary>
/// The cards the wizards on a profile are shown as: what each one says, and which school's element logo
/// it is drawn with.
/// </summary>
/// <remarks>
/// The logo itself is Avalonia's business - a picture cannot be decoded into a test process with no
/// platform behind it - so what is pinned down here is the part that can go wrong quietly: which school
/// each card asks for its logo by, that a school the launcher cannot name is given nothing rather than
/// somebody else's symbol, and the words on the card, which are the wizard's own and must not be invented.
/// </remarks>
public class WizardCardTests {

    [Fact]
    public void ACardCarriesTheSchoolTheLevelThePurseAndTheSpellbook() {
        var profile = new LauncherProfile {
            AccountId = 7,
            Username = "Jordan",
            Wizards = [
                new ProfileWizard {
                    CharId = 11,
                    Name = "Rowan Stormheart",
                    School = "Storm",
                    Level = 42,
                    Gold = 12345,
                    QuestsCompleted = 63,
                    LearnedSpells = [1, 2, 3, 4, 5],
                    LastSeenAt = new DateTimeOffset(2026, 10, 14, 20, 15, 0, TimeSpan.Zero),
                },
            ],
        };

        var row = Assert.Single(ProfileWizardRow.For(profile));

        Assert.Equal("Rowan Stormheart", row.DisplayName);
        Assert.Equal("Storm", row.School);
        Assert.Equal("Storm", row.SchoolText);
        Assert.Equal("42", row.LevelBadgeText);

        // The purse is written out with its digits grouped, and the spellbook is counted rather than
        // listed: five spells is what the card has room for.
        Assert.Equal($"{12345:N0} gold", row.GoldText);
        Assert.Equal("5 spells", row.SpellsText);
        Assert.Equal("63 quests finished", row.QuestsText);
        Assert.Contains("2026-10-14", row.LastSeenText);
        Assert.True(row.HasLastSeen);
    }

    [Fact]
    public void ACardKeepsTheNumbersItsFaceCannotFit() {
        // A wizard's card is short on purpose, so the four lines the old card carried are one hover away
        // rather than gone - in the card's own words, with nothing strung together that says nothing.
        var row = Assert.Single(ProfileWizardRow.For(new LauncherProfile {
            AccountId = 7,
            Username = "Jordan",
            Wizards = [
                new ProfileWizard {
                    CharId = 11,
                    Name = "Rowan Stormheart",
                    School = "Storm",
                    Level = 42,
                    Gold = 12345,
                    QuestsCompleted = 63,
                    LearnedSpells = [1, 2, 3, 4, 5],
                },
            ],
        }));

        Assert.Contains("Storm", row.DetailToolTip);
        Assert.Contains("Level 42", row.DetailToolTip);
        Assert.Contains($"{12345:N0} gold", row.DetailToolTip);
        Assert.Contains("5 spells", row.DetailToolTip);
        Assert.Contains("63 quests finished", row.DetailToolTip);

        // A wizard never watched playing has no last-seen line, and the tooltip does not end on a dangling
        // separator because of it.
        Assert.False(row.DetailToolTip.EndsWith(" · "));
        Assert.DoesNotContain("last seen", row.DetailToolTip);
    }

    [Fact]
    public void ACardAsksForItsLogosByTheSchoolTheDatabaseGave() {
        var asked = new List<string>();

        var profile = new LauncherProfile {
            AccountId = 7,
            Username = "Jordan",
            Wizards = [
                new ProfileWizard { CharId = 1, School = "Fire" },
                new ProfileWizard { CharId = 2, School = "Balance" },
                new ProfileWizard { CharId = 3, School = string.Empty },
            ],
        };

        var rows = ProfileWizardRow.For(profile, school => {
            asked.Add(school);
            return null;
        });

        // One look per wizard, with the school exactly as stored - the card does not translate it, because
        // the lookup is what knows both spellings of a school.
        Assert.Equal(["Fire", "Balance", ""], asked);
        Assert.Equal(3, rows.Count);

        // Nothing to draw is not a logo: a symbol the launcher made up would be the wrong school's symbol.
        Assert.All(rows, row => Assert.False(row.HasSchoolIcon));
        Assert.All(rows, row => Assert.Null(row.SchoolIcon));
    }

    [Fact]
    public void ASchoolThatCouldNotBeReadIsSaidRatherThanGuessedAt() {
        var row = Assert.Single(ProfileWizardRow.For(new LauncherProfile {
            AccountId = 7,
            Username = "Jordan",
            Wizards = [new ProfileWizard { CharId = 1, Level = 3 }],
        }));

        Assert.Equal(string.Empty, row.School);
        Assert.Equal("School not read", row.SchoolText);
    }

    [Fact]
    public void ALevelThatWasNotReadIsADashRatherThanAZero() {
        // A zero would be a claim about the wizard; a dash is the same admission the sentence beside it
        // makes, in the room a circle has.
        Assert.Equal("—", new ProfileWizard { CharId = 1 }.LevelBadgeText);
        Assert.Equal("Level not read yet", new ProfileWizard { CharId = 1 }.LevelText);
        Assert.Equal("12", new ProfileWizard { CharId = 1, Level = 12 }.LevelBadgeText);
    }

    [Fact]
    public void TheSpellbookIsCountedInWords() {
        Assert.Equal("no spells", new ProfileWizard().SpellsText);
        Assert.Equal("1 spell", new ProfileWizard { LearnedSpells = [7] }.SpellsText);
        Assert.Equal("2 spells", new ProfileWizard { LearnedSpells = [7, 8] }.SpellsText);
    }

    [Fact]
    public void ALauncherWithNoProfileHasNoCards() {
        Assert.Empty(ProfileWizardRow.For(null));
        Assert.Empty(ProfileWizardRow.For(null, _ => null));
    }

    [Fact]
    public void AnAccountsWizardsKeepTheOrderTheyWereLearnedIn() {
        var profile = new LauncherProfile {
            AccountId = 7,
            Username = "Jordan",
            Wizards = [
                new ProfileWizard { CharId = 9, Name = "First" },
                new ProfileWizard { CharId = 4, Name = "Second" },
                new ProfileWizard { CharId = 6, Name = "Third", Level = 1 },
            ],
        };

        var rows = ProfileWizardRow.For(profile);

        // The order is the profile's own, not a sort: a card that moved as a level changed would move out
        // from under whoever was reading it.
        Assert.Equal(["First", "Second", "Third"], rows.Select(row => row.DisplayName));
    }

    [Fact]
    public void ACardThatChangesIsADifferentCard() {
        var before = Rows(school: "Fire", level: 10, spells: 2);
        var levelled = Rows(school: "Fire", level: 11, spells: 2);
        var learned = Rows(school: "Fire", level: 10, spells: 3);

        // The rows are values, so a level or a spell arriving is one comparison - which is what decides
        // whether the list on screen is touched at all, twenty times a minute.
        Assert.NotEqual(before, levelled);
        Assert.NotEqual(before, learned);
        Assert.Equal(before, Rows(school: "Fire", level: 10, spells: 2));
    }

    private static IReadOnlyList<ProfileWizardRow> Rows(string school, int level, int spells)
        => ProfileWizardRow.For(new LauncherProfile {
            AccountId = 7,
            Username = "Jordan",
            Wizards = [
                new ProfileWizard {
                    CharId = 1,
                    Name = "Rowan",
                    School = school,
                    Level = level,
                    LearnedSpells = [.. Enumerable.Range(1, spells).Select(n => (uint) n)],
                },
            ],
        });
}
