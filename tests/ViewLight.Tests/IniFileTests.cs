using System.Text;
using ViewLight.Services;
using Xunit;

namespace ViewLight.Tests;

/// <summary>
/// The INI editor is the piece most likely to quietly destroy a user's configuration, so these tests
/// are deliberately strict about preserving formatting, comments, spacing and encoding.
/// </summary>
public class IniFileTests {

    /// <summary>Modelled on the shape of Imlight's stock <c>Config/Imlight.ini</c>: CRLF, comment above
    /// almost every key, blank lines between sections, and an empty value.</summary>
    private const string Sample =
        "; Imlight style configuration\r\n" +
        "[Global Settings]\r\n" +
        "GameRevision = r803238.Wizard_1_610\r\n" +
        "; If true, the servers will refuse connections if the game revision does not match.\r\n" +
        "EnforceRevision = false\r\n" +
        "\r\n" +
        "[Character]\r\n" +
        "StartingLevel = 1\r\n" +
        "; If true, new characters skip the tutorial and start in StartingZone.\r\n" +
        "TutorialDisabled = false\r\n" +
        "; Starter kit template IDs, comma-separated.\r\n" +
        "; Do not exceed 16 items.\r\n" +
        "DefaultItems = 1317127,1317128\r\n" +
        "StartingZone = WizardCity/Interiors/WC_Headmistress_House\r\n" +
        "\r\n" +
        "[Database]\r\n" +
        "PlayerDatabaseUrl =\r\n" +
        "AdminAccountUsername = admin\r\n" +
        "AdminAccountPassword = compsci\r\n";

    [Fact]
    public void ParseThenWrite_IsByteIdentical() {
        var ini = IniFile.Parse(Sample);

        Assert.Equal(Sample, ini.ToText());
    }

    [Fact]
    public void Set_ChangesOnlyTheTargetValue() {
        var ini = IniFile.Parse(Sample);

        ini.Set("Character", "TutorialDisabled", "true");

        var expected = Sample.Replace("TutorialDisabled = false", "TutorialDisabled = true");
        Assert.Equal(expected, ini.ToText());
    }

    [Fact]
    public void Set_IsCaseInsensitiveOnSectionAndKey() {
        var ini = IniFile.Parse(Sample);

        Assert.Equal("admin", ini.Get("database", "adminaccountusername"));

        ini.Set("DATABASE", "AdminAccountPassword", "hunter2");
        Assert.Equal("hunter2", ini.Get("Database", "AdminAccountPassword"));
    }

    [Fact]
    public void Set_PreservesInlineCommentAndSpacing() {
        var ini = IniFile.Parse("[A]\r\nKey = 5 ; keep me\r\n");

        ini.Set("A", "Key", "9");

        Assert.Equal("[A]\r\nKey = 9 ; keep me\r\n", ini.ToText());
        Assert.Equal("9", ini.Get("A", "Key"));
    }

    [Fact]
    public void Set_LeavesEmptyValueAloneUntilAsked() {
        var ini = IniFile.Parse(Sample);

        Assert.Equal(string.Empty, ini.Get("Database", "PlayerDatabaseUrl"));

        ini.Set("Character", "StartingLevel", "50");

        Assert.Contains("PlayerDatabaseUrl =\r\n", ini.ToText());
    }

    [Fact]
    public void Set_AppendsNewKeyToItsSection() {
        var ini = IniFile.Parse(Sample);

        ini.Set("Global Settings", "BrandNewKey", "hello");

        var text = ini.ToText();
        Assert.Contains("BrandNewKey = hello\r\n", text);

        // The new key must land inside its own section, i.e. before the next section header.
        var newKeyIndex = text.IndexOf("BrandNewKey", StringComparison.Ordinal);
        var characterSectionIndex = text.IndexOf("[Character]", StringComparison.Ordinal);
        Assert.True(newKeyIndex < characterSectionIndex);
        Assert.True(ini.Get("Global Settings", "BrandNewKey") == "hello");
    }

    [Fact]
    public void Set_AppendsMissingSectionAtTheEnd() {
        var ini = IniFile.Parse(Sample);

        ini.Set("Advanced", "KeepAliveInterval", "60");

        var text = ini.ToText();
        Assert.Contains("[Advanced]\r\nKeepAliveInterval = 60\r\n", text);
    }

    [Fact]
    public void SetCommentAbove_ReplacesTheExistingCommentBlockOnly() {
        var ini = IniFile.Parse(Sample);

        ini.SetCommentAbove("Character", "DefaultItems", ["Starter kit.", "87256 = Wand-T1-016."]);

        var expected = Sample.Replace(
            "; Starter kit template IDs, comma-separated.\r\n; Do not exceed 16 items.\r\n",
            "; Starter kit.\r\n; 87256 = Wand-T1-016.\r\n");

        Assert.Equal(expected, ini.ToText());

        // The comment above the *previous* key must be untouched.
        Assert.Contains("; If true, new characters skip the tutorial and start in StartingZone.", ini.ToText());
    }

    [Fact]
    public void ParseThenWrite_PreservesASectionTheLauncherKnowsNothingAbout() {
        // Real configs carry sections this launcher has never heard of - [CrownShop] ships in newer
        // Imlight builds but is absent from the one Imlight's own stock config documents.
        var withFeatureSection = Sample.Replace(
            "[Database]\r\n",
            "[CrownShop]\r\n"
            + "Enabled = true\r\n"
            + "AutoReloadOnChanges = true\r\n"
            + "Path = ./Config/CrownShop\r\n"
            + "WishlistMaxSize = 30\r\n"
            + "WishlistSBExpansionSize = 10\r\n"
            + "MaxBuyCount = 100\r\n"
            + "\r\n"
            + "[Database]\r\n");

        var ini = IniFile.Parse(withFeatureSection);

        Assert.Equal(withFeatureSection, ini.ToText());
        Assert.Equal("30", ini.Get("CrownShop", "WishlistMaxSize"));
        Assert.Contains("CrownShop", ini.Sections);

        // Editing an unrelated key must not disturb it.
        ini.Set("Character", "TutorialDisabled", "true");
        Assert.Equal("true", ini.Get("CrownShop", "Enabled"));
        Assert.Contains("Path = ./Config/CrownShop", ini.ToText());
    }

    [Fact]
    public void MergeFrom_CarriesAWholeUserOnlySectionIntoTheNewReleaseConfig() {
        // A release that predates [CrownShop] must not strip the user's section on update.
        var release = IniFile.Parse("[Character]\r\nDefaultItems = 1,2\r\n");
        var user = IniFile.Parse(
            "[Character]\r\nDefaultItems = 8,9\r\n"
            + "\r\n"
            + "[CrownShop]\r\nEnabled = true\r\nWishlistMaxSize = 30\r\n");

        release.MergeFrom(user);

        Assert.Equal("8,9", release.Get("Character", "DefaultItems"));
        Assert.Equal("true", release.Get("CrownShop", "Enabled"));
        Assert.Equal("30", release.Get("CrownShop", "WishlistMaxSize"));
        Assert.Contains("[CrownShop]", release.ToText());
    }

    [Fact]
    public void MergeFrom_KeepsUserValuesAndAddsBrandNewDefaults() {
        // "new release" defaults
        var release = IniFile.Parse(
            "[Character]\r\nDefaultItems = 1,2,3\r\nTutorialDisabled = false\r\nNewSetting = fromRelease\r\n");

        // what the user had
        var user = IniFile.Parse(
            "[Character]\r\nDefaultItems = 87256,126983\r\nTutorialDisabled = true\r\nUserOnlyKey = keepMe\r\n");

        var applied = release.MergeFrom(user);

        Assert.Equal(3, applied);
        Assert.Equal("87256,126983", release.Get("Character", "DefaultItems"));
        Assert.Equal("true", release.Get("Character", "TutorialDisabled"));
        Assert.Equal("fromRelease", release.Get("Character", "NewSetting"));
        Assert.Equal("keepMe", release.Get("Character", "UserOnlyKey"));
    }

    [Fact]
    public void MergeFrom_DoesNotDuplicateAKeyThatAlreadyExists() {
        var release = IniFile.Parse("[Character]\r\nDefaultItems = 1,2,3\r\n");
        var user = IniFile.Parse("[Character]\r\nDefaultItems = 9\r\n");

        release.MergeFrom(user);

        var occurrences = release.ToText().Split("DefaultItems").Length - 1;
        Assert.Equal(1, occurrences);
    }

    [Fact]
    public void Load_HandlesFilesThatAreNotValidUtf8() {
        using var workspace = new TempWorkspace();

        // 0x97 is not valid UTF-8 on its own: exactly the kind of stray byte Imlight's shipped config
        // contains inside a comment ("never contacted ? only local data is used").
        var bytes = Encoding.Latin1.GetBytes("; never contacted \u0097 only local data is used\r\n[A]\r\nK = V\r\n");
        var path = workspace.WriteFile("config/Imlight.ini", bytes);

        var ini = IniFile.Load(path);
        Assert.Equal("V", ini.Get("A", "K"));

        ini.Set("A", "K", "W");
        ini.Save(path);

        var written = File.ReadAllBytes(path);
        var text = Encoding.Latin1.GetString(written);
        Assert.Contains("\u0097", text);
        Assert.Contains("K = W", text);
        Assert.DoesNotContain("\uFFFD", text);
    }

    [Fact]
    public void Load_KeepsUtf8FilesAsUtf8() {
        using var workspace = new TempWorkspace();

        var path = workspace.WriteFile("config/Imlight.ini", "; café ☕\r\n[A]\r\nK = V\r\n");
        var ini = IniFile.Load(path);
        ini.Set("A", "K", "W");
        ini.Save(path);

        var text = File.ReadAllText(path, Encoding.UTF8);
        Assert.Contains("café ☕", text);
    }

    [Fact]
    public void Parse_HandlesLfOnlyFilesAndMissingTrailingNewline() {
        var ini = IniFile.Parse("[A]\nK = V");

        Assert.Equal("[A]\nK = V", ini.ToText());

        ini.Set("A", "K", "W");
        Assert.Equal("[A]\nK = W", ini.ToText());
    }

    [Fact]
    public void Snapshot_ReportsEveryKey() {
        var ini = IniFile.Parse(Sample);

        var snapshot = ini.Snapshot();

        Assert.Contains("Character\u001fDefaultItems", snapshot.Keys);
        Assert.Equal("1", snapshot["Character\u001fStartingLevel"]);
    }
}
