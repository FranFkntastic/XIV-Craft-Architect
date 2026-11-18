using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services;

public static class TradePaymentPolicyNormalizer
{
    public static TradePaymentPolicy Normalize(TradePaymentPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        var laborGilPerSynth = policy.LaborGilPerSynth;
        if (laborGilPerSynth == 0 &&
            policy.StoredLaborBenchmark is { BenchmarkLaborPayout: > 0, BenchmarkSynthCount: > 0 } benchmark)
        {
            laborGilPerSynth = benchmark.BenchmarkLaborPayout / benchmark.BenchmarkSynthCount;
        }

        return policy with
        {
            ActiveContract = TradePaymentContractMode.LaborStandard,
            LaborGilPerSynth = laborGilPerSynth == 0 ? TradePaymentPolicy.DefaultLaborGilPerSynth : laborGilPerSynth,
            StoredLaborBenchmark = null
        };
    }
}
