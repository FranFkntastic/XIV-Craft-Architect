using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Web.Services;
namespace FFXIV_Craft_Architect.ContractTests;

public sealed class PersistenceContractTests
{
    [Fact]
    public async Task ZeroCommissionPayrollDraftSurvivesPersistenceRoundTrip()
    {
        var companyProfileId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var store = new JsonRoundTripPayrollStore();
        var service = new TradePayrollPersistenceService(store);
        await service.GetOrCreateDraftAsync(
            companyProfileId,
            orderId: null,
            planSessionVersion: 12,
            marketAnalysisVersion: 34,
            sourcePlanName: "Zero commission plan",
            assignedCrafterId: null,
            assignedCrafterDisplayName: null,
            paymentPolicy: new TradePaymentPolicy(
                TradePaymentContractMode.LegacyCommission,
                0m,
                TradePaymentPolicy.DefaultLaborGilPerSynth));
        var reloaded = Assert.Single(await new TradePayrollPersistenceService(store).LoadDraftsAsync(companyProfileId));
        Assert.Equal(0m, reloaded.CommissionPercent);
        Assert.Equal(TradePaymentContractMode.LegacyCommission, reloaded.ActivePaymentContract);
    }
    private sealed class JsonRoundTripPayrollStore : ITradePayrollDraftStore
    {
        private string json = "[]";
        public Task<IReadOnlyList<TradePayrollWorkflowDraft>> LoadDraftsAsync(Guid companyProfileId)
        {
            var drafts = JsonSerializer.Deserialize<TradePayrollWorkflowDraft[]>(json) ?? [];
            return Task.FromResult<IReadOnlyList<TradePayrollWorkflowDraft>>(
                drafts.Where(draft => draft.CompanyProfileId == companyProfileId).ToArray());
        }
        public Task<bool> SaveDraftAsync(TradePayrollWorkflowDraft draft)
        {
            json = JsonSerializer.Serialize(new[] { draft });
            return Task.FromResult(true);
        }
        public Task<bool> DeleteDraftAsync(string draftId)
        {
            json = "[]";
            return Task.FromResult(true);
        }
    }
}
