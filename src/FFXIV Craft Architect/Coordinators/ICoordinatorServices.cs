namespace FFXIV_Craft_Architect.Coordinators;

/// <summary>
/// Aggregates coordinator services to reduce constructor parameter bloat.
/// Coordinators orchestrate complex workflows that span multiple services.
/// </summary>
public interface ICoordinatorServices
{
    ImportCoordinator Import { get; }
    ExportCoordinator Export { get; }
    PlanPersistenceCoordinator Plans { get; }
    IMarketLogisticsCoordinator Market { get; }
    IPriceRefreshCoordinator Prices { get; }
    IShoppingOptimizationCoordinator Optimization { get; }
    IWatchListCoordinator Watch { get; }
}
