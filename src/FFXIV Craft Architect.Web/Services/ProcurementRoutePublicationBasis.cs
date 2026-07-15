using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Web.Services;

public enum ProcurementRoutePublicationValidity
{
    None,
    Current,
    SelectionChanged,
    InputsChanged
}

public sealed record ProcurementRoutePublicationBasis(
    long PlanSessionVersion,
    long PlanDecisionVersion,
    Guid MarketIntelligenceId,
    MarketFetchScope Scope,
    string SelectedDataCenter,
    string SelectedRegion,
    MarketAcquisitionLens Lens,
    bool IncludeSplitPurchases,
    int TravelTolerance,
    MarketTravelPriority TravelPriority,
    bool StartFromHomeDataCenter,
    HashSet<MarketWorldKey> BlacklistedWorlds,
    HashSet<MarketItemWorldKey> ExcludedItemWorlds)
{
    public bool HasSameRouteInputsAs(ProcurementRoutePublicationBasis other)
    {
        ArgumentNullException.ThrowIfNull(other);

        return PlanSessionVersion == other.PlanSessionVersion &&
               PlanDecisionVersion == other.PlanDecisionVersion &&
               MarketIntelligenceId == other.MarketIntelligenceId &&
               Scope == other.Scope &&
               string.Equals(SelectedDataCenter, other.SelectedDataCenter, StringComparison.Ordinal) &&
               string.Equals(SelectedRegion, other.SelectedRegion, StringComparison.Ordinal) &&
               Lens == other.Lens &&
               IncludeSplitPurchases == other.IncludeSplitPurchases &&
               TravelPriority == other.TravelPriority &&
               StartFromHomeDataCenter == other.StartFromHomeDataCenter &&
               BlacklistedWorlds.SetEquals(other.BlacklistedWorlds) &&
               ExcludedItemWorlds.SetEquals(other.ExcludedItemWorlds);
    }

    public bool Matches(ProcurementRoutePublicationBasis other) =>
        HasSameRouteInputsAs(other) && TravelTolerance == other.TravelTolerance;
}
