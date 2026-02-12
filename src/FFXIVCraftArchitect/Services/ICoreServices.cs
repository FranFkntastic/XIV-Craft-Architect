using FFXIVCraftArchitect.Core.Services;

namespace FFXIVCraftArchitect.Services;

/// <summary>
/// Aggregates core/data services to reduce constructor parameter bloat.
/// All services are application singletons that provide data access and core business logic.
/// </summary>
public interface ICoreServices
{
    GarlandService Garland { get; }
    UniversalisService Universalis { get; }
    SettingsService Settings { get; }
    ItemCacheService ItemCache { get; }
    RecipeCalculationService RecipeCalc { get; }
    PlanPersistenceService PlanPersistence { get; }
    PriceCheckService PriceCheck { get; }
    MarketShoppingService MarketShopping { get; }
    WorldBlacklistService Blacklist { get; }
}
