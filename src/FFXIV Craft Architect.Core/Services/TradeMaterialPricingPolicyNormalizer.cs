using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services;

public static class TradeMaterialPricingPolicyNormalizer
{
    public static TradeMaterialPricingPolicy Normalize(TradeMaterialPricingPolicy? policy)
    {
        policy ??= TradeMaterialPricingPolicy.Default;
        var minimum = Math.Max(0m, policy.MinimumSafetyAllowanceGil);
        var maximum = Math.Max(minimum, policy.MaximumSafetyAllowanceGil);
        return policy with
        {
            SchemaVersion = TradeMaterialPricingPolicy.CurrentSchemaVersion,
            MaximumConsolidationPremiumPercent = Math.Clamp(
                policy.MaximumConsolidationPremiumPercent,
                0m,
                100m),
            MaximumWorldStops = Math.Clamp(policy.MaximumWorldStops, 1, 32),
            SafetyAllowancePercent = Math.Clamp(policy.SafetyAllowancePercent, 0m, 100m),
            MinimumSafetyAllowanceGil = minimum,
            MaximumSafetyAllowanceGil = maximum,
            QuoteLifetimeMinutes = Math.Clamp(policy.QuoteLifetimeMinutes, 5, 24 * 60),
            MaximumEvidenceAgeMinutes = Math.Clamp(policy.MaximumEvidenceAgeMinutes, 5, 24 * 60)
        };
    }

    public static string Fingerprint(TradeMaterialPricingPolicy? policy)
    {
        var value = Normalize(policy);
        var routeDimensions = value.MaximumDataCenterTransfers is { } legacyTransfers
            ? new[]
            {
                value.MaximumWorldStops.ToString(CultureInfo.InvariantCulture),
                Math.Clamp(legacyTransfers, 0, 8).ToString(CultureInfo.InvariantCulture)
            }
            : [value.MaximumWorldStops.ToString(CultureInfo.InvariantCulture)];
        var canonical = string.Join('|', [
            value.SchemaVersion.ToString(CultureInfo.InvariantCulture),
            value.MaximumConsolidationPremiumPercent.ToString(CultureInfo.InvariantCulture),
            .. routeDimensions,
            value.AllowSplitPurchases ? "1" : "0",
            value.SafetyAllowancePercent.ToString(CultureInfo.InvariantCulture),
            value.MinimumSafetyAllowanceGil.ToString(CultureInfo.InvariantCulture),
            value.MaximumSafetyAllowanceGil.ToString(CultureInfo.InvariantCulture),
            value.QuoteLifetimeMinutes.ToString(CultureInfo.InvariantCulture),
            value.MaximumEvidenceAgeMinutes.ToString(CultureInfo.InvariantCulture)]);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..16];
    }
}
