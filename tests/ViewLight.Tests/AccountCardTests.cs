using ViewLight.Models;
using ViewLight.Services;
using Xunit;

namespace ViewLight.Tests;

/// <summary>
/// The tiles the sign-in screen draws: who each account is, what it is worth, and which wizards it shows.
/// </summary>
/// <remarks>
/// The pictures themselves are Avalonia's business - a bitmap cannot be decoded in a test process with no
/// platform behind it - so what is pinned down here is what can go wrong quietly: a tile that says the wrong
/// score, a wizard list that grows without limit, and tiles that compare unequal every time they are built,
/// which would rebuild the row under the pointer on every poll.
/// </remarks>
public class AccountCardTests {

    [Fact]
    public void ATileSaysWhoTheAccountIsAndWhatItIsWorth() {
        var profile = new LauncherProfile {
            AccountId = 7,
            Username = "jordan",
            DisplayName = "Jordan Rivers",
            Achievements = [
                new EarnedAchievement("first-steps", DateTimeOffset.Now, string.Empty),
            ],
            Wizards = [
                new ProfileWizard { CharId = 1, Name = "Rowan", School = "Storm", Level = 42 },
                new ProfileWizard { CharId = 2, Name = "Wren", School = "Fire", Level = 7 },
            ],
        };

        var card = Assert.Single(AccountCard.For([profile]));

        Assert.Equal(7UL, card.AccountId);
        Assert.Equal("Jordan Rivers", card.Name);
        Assert.Equal("jordan", card.Username);
        Assert.Equal("JR", card.Initials);

        // The score is the profile's own - the same one its circle shows - and a clean profile's badges count
        // double, which is the rule the achievements page explains rather than a second total invented here.
        Assert.Equal(20, card.Score);
        Assert.Equal("20", card.ScoreText);

        Assert.Equal(2, card.WizardCount);
        Assert.Equal("2 wizards", card.WizardsCaption);
        Assert.False(card.HasMoreWizards);
    }

    [Fact]
    public void ATileListsThreeWizardsAndCountsTheRest() {
        var profile = new LauncherProfile {
            AccountId = 7,
            Username = "jordan",
            Wizards = [.. Enumerable.Range(0, 5).Select(n => new ProfileWizard {
                CharId = (ulong) (n + 1),
                Name = $"Wizard {n + 1}",
                School = "Fire",
                Level = n + 1,
            })],
        };

        var card = Assert.Single(AccountCard.For([profile]));

        // A tile has room for three lines: the rest is a count rather than a scroll area inside a card on a
        // screen that already scrolls.
        Assert.Equal(3, card.Wizards.Length);
        Assert.Equal(["Wizard 1", "Wizard 2", "Wizard 3"], card.Wizards.Select(w => w.DisplayName));
        Assert.Equal(2, card.MoreWizards);
        Assert.Equal("+2 more", card.MoreWizardsText);
        Assert.True(card.HasMoreWizards);

        // The order is the profile's own, not a sort of the tile's, so the two screens cannot list them
        // differently.
        Assert.Equal([1UL, 2UL, 3UL], card.Wizards.Select(w => w.CharId));
    }

    [Fact]
    public void ATileAsksForEachLogosByTheSchoolTheDatabaseGave() {
        var asked = new List<string>();

        var profile = new LauncherProfile {
            AccountId = 7,
            Username = "jordan",
            Wizards = [
                new ProfileWizard { CharId = 1, Name = "Rowan", School = "Fire", Level = 3 },
                new ProfileWizard { CharId = 2, Name = "Wren", School = "Balance", Level = 4 },
                new ProfileWizard { CharId = 3, Name = "Ash", School = string.Empty, Level = 5 },
                new ProfileWizard { CharId = 4, Name = "Not asked", School = "Storm", Level = 6 },
            ],
        };

        var card = Assert.Single(AccountCard.For([profile], schoolIconFor: school => {
            asked.Add(school);
            return null;
        }));

        // One look per wizard that is on the tile - not one per wizard the account has, because the fourth is
        // not drawn - and the school exactly as stored, because the lookup knows both spellings.
        Assert.Equal(["Fire", "Balance", ""], asked);

        // Nothing to draw is not a logo: a made-up symbol would be the wrong school's symbol.
        Assert.All(card.Wizards, wizard => Assert.False(wizard.HasSchoolIcon));
        Assert.All(card.Wizards, wizard => Assert.Null(wizard.SchoolIcon));
    }

    [Fact]
    public void AnAccountWithNoWizardsYetSaysSo() {
        var card = Assert.Single(AccountCard.For([
            new LauncherProfile { AccountId = 7, Username = "jordan" },
        ]));

        Assert.False(card.HasWizards);
        Assert.Empty(card.Wizards);
        Assert.Equal("No wizards read yet", card.WizardsCaption);
        Assert.False(card.HasMoreWizards);
    }

    [Fact]
    public void TheTilesKeepTheOrderThePickerOffersThemIn() {
        var alex = new LauncherProfile { AccountId = 2, Username = "alex" };
        var jordan = new LauncherProfile { AccountId = 1, Username = "jordan" };

        // The order is the picker's own, whatever that is: the sign-in screen and the list behind the profile
        // page must not disagree about who comes first.
        Assert.Equal(["alex", "jordan"], AccountCard.For([alex, jordan]).Select(card => card.Username));
        Assert.Equal(["jordan", "alex"], AccountCard.For([jordan, alex]).Select(card => card.Username));
    }

    [Fact]
    public void ATileThatHasNotChangedIsTheSameTile() {
        var profile = new LauncherProfile {
            AccountId = 7,
            Username = "jordan",
            Wizards = [new ProfileWizard { CharId = 1, Name = "Rowan", School = "Fire", Level = 3 }],
        };

        Assert.Equal(AccountCard.For([profile]), AccountCard.For([profile]));

        // A level that moved is a different tile, which is what lets the row be left alone when nothing has.
        var levelled = new LauncherProfile {
            AccountId = 7,
            Username = "jordan",
            Wizards = [new ProfileWizard { CharId = 1, Name = "Rowan", School = "Fire", Level = 4 }],
        };

        Assert.NotEqual(AccountCard.For([profile]), AccountCard.For([levelled]));
    }
}
