using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services;

public static class TradePaymentPolicyNormalizer
{
    public static TradePaymentPolicy Normalize(TradePaymentPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        return policy with
        {
            ActiveContract = TradePaymentContractMode.LaborStandard,
            LaborGilPerSynth = policy.LaborGilPerSynth == 0 ? TradePaymentPolicy.DefaultLaborGilPerSynth : policy.LaborGilPerSynth
        };
    }
}
