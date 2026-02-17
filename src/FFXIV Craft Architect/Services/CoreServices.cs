using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Services;

/// <summary>
/// Implementation of ICoreServices - aggregates all core/data layer services.
/// </summary>
public class CoreServices : ICoreServices
{
    public GarlandService Garland { get; }
    public UniversalisService Universalis { get; }
    public SettingsService Settings { get; }
    public ItemCacheService ItemCache { get; }
    public RecipeCalculationService RecipeCalc { get; }
    public PlanPersistenceService PlanPersistence { get; }
    public PriceCheckService PriceCheck { get; }
    public MarketShoppingService MarketShopping { get; }
    public WorldBlacklistService Blacklist { get; }

    public CoreServices(
        GarlandService garlandService,
        UniversalisService universalisService,
        SettingsService settingsService,
        ItemCacheService itemCacheService,
        RecipeCalculationService recipeCalculationService,
        PlanPersistenceService planPersistenceService,
        PriceCheckService priceCheckService,
        MarketShoppingService marketShoppingService,
        WorldBlacklistService worldBlacklistService)
    {
        Garland = garlandService;
        Universalis = universalisService;
        Settings = settingsService;
        ItemCache = itemCacheService;
        RecipeCalc = recipeCalculationService;
        PlanPersistence = planPersistenceService;
        PriceCheck = priceCheckService;
        MarketShopping = marketShoppingService;
        Blacklist = worldBlacklistService;
    }
}
