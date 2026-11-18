using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.ContractTests;

public sealed class PersistenceContractTests
{
    [Fact]
    public void StoredAgreementAndProductionPolicyKeepDistinctPaymentAuthorities()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var policy = JsonSerializer.Deserialize<TradePaymentPolicy>(
            """{"activeContract":1,"legacyCommissionPercent":20,"laborGilPerSynth":450}""", options)!;
        Assert.Equal(TradePaymentContractMode.LaborStandard, policy.ActiveContract);
        var calculated = new TradePaymentCalculator().Calculate(new(
            [new(1, "Ore", 2, false, 50, CommissionMaterialResponsibility.Crafter, "", "", null, [])],
            [new("craft", 2, "Ingot", 1, 1, [])], policy, [], MaterialSafetyAllowance: 5, MaterialRouteCashRequired: 100));
        Assert.Equal(555, calculated.TotalPayment);
        Assert.DoesNotContain("percent", JsonSerializer.Serialize(policy, options), StringComparison.OrdinalIgnoreCase);
        const string legacy = """{"schedule":0,"contractLabel":"Commission","materialReimbursement":100,"materialAdjustment":1,"craftLabor":0,"total":101}""";
        var payment = JsonSerializer.Deserialize<CompanyCommissionPaymentTerms>(legacy, options)!;
        Assert.Equal(1, payment.HistoricalAmount);
        Assert.Equal(101m, payment.Total);
        Assert.Equal(payment, JsonSerializer.Deserialize<CompanyCommissionPaymentTerms>(JsonSerializer.Serialize(payment, options), options));
    }
}
