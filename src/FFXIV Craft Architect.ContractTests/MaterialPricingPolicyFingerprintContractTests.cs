using System.Text.Json;

using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.ContractTests;

public sealed class MaterialPricingPolicyFingerprintContractTests
{
    /// <summary>
    /// Every published commission stores its quote's applied policy and fingerprint, and each
    /// terms revision re-fingerprints that policy and must match. A policy stored before the
    /// travel tolerance existed must read as the default tolerance and keep the fingerprint the
    /// pre-tolerance code gave it, or every in-flight commission fails reconciliation.
    /// </summary>
    [Fact]
    public void PolicyStoredBeforeTravelToleranceKeepsItsFingerprint()
    {
        const string stored = """
            {"schemaVersion":1,"maximumConsolidationPremiumPercent":5,"maximumWorldStops":8,
             "allowSplitPurchases":true,"safetyAllowancePercent":10,"minimumSafetyAllowanceGil":10000,
             "maximumSafetyAllowanceGil":250000,"quoteLifetimeMinutes":30,"maximumEvidenceAgeMinutes":120}
            """;
        var policy = JsonSerializer.Deserialize<TradeMaterialPricingPolicy>(
            stored, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        Assert.Equal(TradeMaterialPricingPolicy.DefaultTravelTolerance, policy.TravelTolerance);
        Assert.Equal("B633D91700837306", TradeMaterialPricingPolicyNormalizer.Fingerprint(policy));
        Assert.NotEqual(
            "B633D91700837306",
            TradeMaterialPricingPolicyNormalizer.Fingerprint(policy with { TravelTolerance = 11 }));
    }
}
