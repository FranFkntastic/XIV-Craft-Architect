namespace FFXIV_Craft_Architect.Tests;

public class TradeOperationsPersistenceContractTests
{
    [Fact]
    public void IndexedDbScript_DefinesSeparateTradeStoresAndCrudFunctions()
    {
        var source = File.ReadAllText(GetWorkspacePath("src", "FFXIV Craft Architect.Web", "wwwroot", "indexedDB.js"));

        Assert.Contains("const STORE_TRADE_COMPANY_PROFILES = 'tradeCompanyProfiles';", source);
        Assert.Contains("const STORE_TRADE_CRAFTERS = 'tradeCrafters';", source);
        Assert.Contains("const STORE_TRADE_ORDERS = 'tradeOrders';", source);
        Assert.Contains("saveTradeCompanyProfile", source);
        Assert.Contains("loadTradeCompanyProfiles", source);
        Assert.Contains("saveTradeCrafter", source);
        Assert.Contains("loadTradeCrafters", source);
        Assert.Contains("saveTradeOrder", source);
        Assert.Contains("loadTradeOrders", source);
        Assert.Contains("deleteTradeOrder", source);
    }

    [Fact]
    public void IndexedDbService_ExposesTradeCrudWrappers()
    {
        var source = File.ReadAllText(GetWorkspacePath("src", "FFXIV Craft Architect.Web", "Services", "IndexedDbService.cs"));

        Assert.Contains("SaveTradeCompanyProfileAsync", source);
        Assert.Contains("LoadTradeCompanyProfilesAsync", source);
        Assert.Contains("SaveTradeCrafterAsync", source);
        Assert.Contains("LoadTradeCraftersAsync", source);
        Assert.Contains("SaveTradeOrderAsync", source);
        Assert.Contains("LoadTradeOrdersAsync", source);
        Assert.Contains("DeleteTradeOrderAsync", source);
    }

    [Fact]
    public void TradeOperationsPersistenceService_BootstrapsDefaultCompanyProfile()
    {
        var source = File.ReadAllText(GetWorkspacePath("src", "FFXIV Craft Architect.Web", "Services", "TradeOperationsPersistenceService.cs"));

        Assert.Contains("GetOrCreateActiveCompanyProfileAsync", source);
        Assert.Contains("TradeCompanyProfile.CreateLocal", source);
        Assert.Contains("FFXIV Trade Company", source);
    }

    private static string GetWorkspacePath(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "FFXIV Craft Architect.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(new[] { directory.FullName }.Concat(parts).ToArray());
    }
}
