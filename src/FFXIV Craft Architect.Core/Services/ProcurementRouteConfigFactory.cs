using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services;

public static class ProcurementRouteConfigFactory
{
    public static MarketAnalysisConfig Create(ProcurementRouteExecutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var source = request.ProcurementConfig;
        return new MarketAnalysisConfig
        {
            MaxWorldsPerItem = source.MaxWorldsPerItem,
            TravelTolerance = source.TravelTolerance,
            EnableSplitWorld = source.EnableSplitWorld,
            MaxPriceMultiplier = source.MaxPriceMultiplier,
            StartFromHomeDataCenter = source.StartFromHomeDataCenter,
            HomeDataCenter = source.StartFromHomeDataCenter
                ? request.SelectedDataCenter
                : string.Empty,
            TravelPriority = source.TravelPriority
        };
    }
}
