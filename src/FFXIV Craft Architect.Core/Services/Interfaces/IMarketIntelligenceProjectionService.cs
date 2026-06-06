using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services.Interfaces;

public interface IMarketIntelligenceProjectionService
{
    MarketIntelligenceProjectionResult Project(MarketIntelligenceProjectionRequest request);
}
