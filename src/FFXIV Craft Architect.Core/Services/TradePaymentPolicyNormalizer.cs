using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services;

public static class TradePaymentPolicyNormalizer
{
    public static TradePaymentPolicy Normalize(TradePaymentPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        var laborGilPerSynth = policy.LaborGilPerSynth;
        if (laborGilPerSynth == 0 &&
            policy.LegacyLaborStandard is
            {
                BenchmarkLaborPayout: > 0,
                BenchmarkSynthCount: > 0
            } legacyLaborStandard)
        {
            laborGilPerSynth =
                legacyLaborStandard.BenchmarkLaborPayout /
                legacyLaborStandard.BenchmarkSynthCount;
        }

        if (laborGilPerSynth == 0)
        {
            laborGilPerSynth = TradePaymentPolicy.DefaultLaborGilPerSynth;
        }

        return policy with
        {
            LaborGilPerSynth = laborGilPerSynth,
            LegacyLaborStandard = null
        };
    }
}
