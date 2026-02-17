namespace FFXIV_Craft_Architect.Coordinators;

/// <summary>
/// Implementation of ICoordinatorServices - aggregates all coordinator services.
/// </summary>
public class CoordinatorServices : ICoordinatorServices
{
    public ImportCoordinator Import { get; }
    public ExportCoordinator Export { get; }
    public PlanPersistenceCoordinator Plans { get; }
    public MarketLogisticsCoordinator Market { get; }
    public IPriceRefreshCoordinator Prices { get; }
    public IShoppingOptimizationCoordinator Optimization { get; }
    public IWatchListCoordinator Watch { get; }

    public CoordinatorServices(
        ImportCoordinator importCoordinator,
        ExportCoordinator exportCoordinator,
        PlanPersistenceCoordinator planPersistenceCoordinator,
        MarketLogisticsCoordinator marketLogisticsCoordinator,
        IPriceRefreshCoordinator priceRefreshCoordinator,
        IShoppingOptimizationCoordinator shoppingOptimizationCoordinator,
        IWatchListCoordinator watchListCoordinator)
    {
        Import = importCoordinator;
        Export = exportCoordinator;
        Plans = planPersistenceCoordinator;
        Market = marketLogisticsCoordinator;
        Prices = priceRefreshCoordinator;
        Optimization = shoppingOptimizationCoordinator;
        Watch = watchListCoordinator;
    }
}
