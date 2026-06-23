using FFXIV_Craft_Architect.Web.Services;

namespace FFXIV_Craft_Architect.Tests;

public class TradeDisplayFormatterTests
{
    [Fact]
    public void FormatQuantity_UsesCompactTradeQuantityPrefix()
    {
        Assert.Equal("x0", TradeDisplayFormatter.FormatQuantity(0));
        Assert.Equal("x4,995", TradeDisplayFormatter.FormatQuantity(4995));
    }
}
