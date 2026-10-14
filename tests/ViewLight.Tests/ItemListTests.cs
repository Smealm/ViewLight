using ViewLight.Services;
using Xunit;

namespace ViewLight.Tests;

public class ItemListTests {

    [Fact]
    public void Parse_SplitsTrimsAndIgnoresBlanks() {
        Assert.Equal([87256u, 126983u], ItemList.Parse(" 87256 , , 126983 ,"));
    }

    [Fact]
    public void Parse_DropsDuplicatesSoTheyDoNotEatABatchSlot() {
        Assert.Equal([87256u, 126983u], ItemList.Parse("87256,87256,126983,126983,87256"));
    }

    [Fact]
    public void Parse_DropsAnythingThatIsNotANumber() {
        Assert.Equal([87256u], ItemList.Parse("87256,Deck-Tutorial-001,abc,-5"));
    }

    [Fact]
    public void Parse_HandlesEmptyInput() {
        Assert.Empty(ItemList.Parse(null));
        Assert.Empty(ItemList.Parse(string.Empty));
        Assert.Empty(ItemList.Parse("   "));
    }

    [Fact]
    public void Format_IsStableAndCommaSeparated() {
        Assert.Equal("87256,126983", ItemList.Format([87256u, 126983u]));
        Assert.Equal(string.Empty, ItemList.Format([]));
    }

    [Fact]
    public void FormatThenParse_RoundTrips() {
        const string original = "1317127,1317128,87256,126983";

        Assert.Equal(original, ItemList.Format(ItemList.Parse(original)));
    }
}
