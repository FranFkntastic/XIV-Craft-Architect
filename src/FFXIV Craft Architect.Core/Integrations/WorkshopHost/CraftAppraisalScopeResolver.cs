namespace FFXIV_Craft_Architect.Core.Integrations.WorkshopHost;

public static class CraftAppraisalScopeResolver
{
    private static readonly IReadOnlyList<string> NorthAmericanDataCenters =
    [
        "Aether",
        "Primal",
        "Crystal",
        "Dynamis"
    ];

    public static CraftAppraisalResolvedScope Resolve(CraftAppraisalRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!string.IsNullOrWhiteSpace(request.Scope.World))
        {
            return new CraftAppraisalResolvedScope(
                IsSupported: true,
                MarketScopes: [request.Scope.World.Trim()],
                UnsupportedReason: string.Empty);
        }

        if (!string.IsNullOrWhiteSpace(request.Scope.DataCenter))
        {
            return new CraftAppraisalResolvedScope(
                IsSupported: true,
                MarketScopes: [request.Scope.DataCenter.Trim()],
                UnsupportedReason: string.Empty);
        }

        if (IsNorthAmerica(request.Scope.Region))
        {
            return new CraftAppraisalResolvedScope(
                IsSupported: true,
                MarketScopes: NorthAmericanDataCenters,
                UnsupportedReason: string.Empty);
        }

        return new CraftAppraisalResolvedScope(
            IsSupported: false,
            MarketScopes: [],
            UnsupportedReason: "Region-only craft appraisal pricing is currently supported for North America only; provide a data center or world.");
    }

    private static bool IsNorthAmerica(string? region)
    {
        return string.Equals(region, "North America", StringComparison.OrdinalIgnoreCase)
            || string.Equals(region, "NA", StringComparison.OrdinalIgnoreCase);
    }
}

public sealed record CraftAppraisalResolvedScope(
    bool IsSupported,
    IReadOnlyList<string> MarketScopes,
    string UnsupportedReason);
