using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Web.Services;

namespace FFXIV_Craft_Architect.Tests;

public class TradePayrollPersistenceServiceTests
{
    [Fact]
    public async Task GetOrCreateDraftAsync_PrefersExistingOrderDraft()
    {
        var companyProfileId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var store = new FakeTradePayrollDraftStore(
        [
            Draft(companyProfileId, null, 50, "Plan draft"),
            Draft(companyProfileId, orderId, 60, "Order draft")
        ]);
        var service = new TradePayrollPersistenceService(store);

        var draft = await service.GetOrCreateDraftAsync(
            companyProfileId,
            orderId,
            planSessionVersion: 50,
            marketAnalysisVersion: 70,
            sourcePlanName: "Active plan",
            assignedCrafterId: null,
            assignedCrafterDisplayName: null);

        Assert.Equal(orderId, draft.OrderId);
        Assert.Equal("Order draft", draft.SourcePlanName);
        Assert.Empty(store.SavedDrafts);
    }

    [Fact]
    public async Task LoadDraftsAsync_ReturnsCompanyDraftsWithoutCreatingNewDrafts()
    {
        var companyProfileId = Guid.NewGuid();
        var store = new FakeTradePayrollDraftStore(
        [
            Draft(companyProfileId, null, 50, "Plan draft")
        ]);
        var service = new TradePayrollPersistenceService(store);

        var drafts = await service.LoadDraftsAsync(companyProfileId);

        Assert.Single(drafts);
        Assert.Equal("Plan draft", drafts[0].SourcePlanName);
        Assert.Empty(store.SavedDrafts);
    }


    [Fact]
    public async Task GetOrCreateDraftAsync_ReusesPlanDraftWhenNoOrderIsLinked()
    {
        var companyProfileId = Guid.NewGuid();
        var store = new FakeTradePayrollDraftStore(
        [
            Draft(companyProfileId, null, 88, "Saved plan draft")
        ]);
        var service = new TradePayrollPersistenceService(store);

        var draft = await service.GetOrCreateDraftAsync(
            companyProfileId,
            orderId: null,
            planSessionVersion: 88,
            marketAnalysisVersion: 99,
            sourcePlanName: "Active plan",
            assignedCrafterId: null,
            assignedCrafterDisplayName: null);

        Assert.Equal("Saved plan draft", draft.SourcePlanName);
        Assert.Equal(88, draft.PlanSessionVersion);
        Assert.Empty(store.SavedDrafts);
    }

    [Fact]
    public async Task GetOrCreateDraftAsync_CreatesLocalDraftWhenNoMatchExists()
    {
        var companyProfileId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var crafterId = Guid.NewGuid();
        var store = new FakeTradePayrollDraftStore([]);
        var service = new TradePayrollPersistenceService(store);

        var draft = await service.GetOrCreateDraftAsync(
            companyProfileId,
            orderId,
            planSessionVersion: 101,
            marketAnalysisVersion: 202,
            sourcePlanName: "Commission Plan",
            assignedCrafterId: crafterId,
            assignedCrafterDisplayName: "Wei Ning");

        Assert.False(string.IsNullOrWhiteSpace(draft.Id));
        Assert.Equal(companyProfileId, draft.CompanyProfileId);
        Assert.Equal(orderId, draft.OrderId);
        Assert.Equal(101, draft.PlanSessionVersion);
        Assert.Equal(202, draft.MarketAnalysisVersion);
        Assert.Equal("Commission Plan", draft.SourcePlanName);
        Assert.Equal(crafterId, draft.AssignedCrafterId);
        Assert.Equal("Wei Ning", draft.AssignedCrafterDisplayName);
        Assert.Equal(TradeSyncState.LocalOnly, draft.SyncState);
        Assert.Single(store.SavedDrafts);
    }

    [Fact]
    public async Task GetOrCreateDraftAsync_AppliesPaymentPolicyToNewDraft()
    {
        var companyProfileId = Guid.NewGuid();
        var policy = new TradePaymentPolicy(
            TradePaymentContractMode.LaborStandard,
            18m,
            new TradeLaborStandard(
                "Cobalt Rivets benchmark",
                5099,
                "Cobalt Rivets",
                999,
                true,
                150_000m,
                200,
                new DateTime(2026, 6, 25, 18, 0, 0, DateTimeKind.Utc)));
        var service = new TradePayrollPersistenceService(new FakeTradePayrollDraftStore([]));

        var draft = await service.GetOrCreateDraftAsync(
            companyProfileId,
            orderId: Guid.NewGuid(),
            planSessionVersion: 101,
            marketAnalysisVersion: 202,
            sourcePlanName: "Commission Plan",
            assignedCrafterId: null,
            assignedCrafterDisplayName: null,
            paymentPolicy: policy);

        Assert.Equal(TradePaymentContractMode.LaborStandard, draft.ActivePaymentContract);
        Assert.Equal(policy.LaborStandard, draft.LaborStandard);
    }

    [Fact]
    public void ApplyResponsibilities_MergesSavedResponsibilityByItemAndHqFlag()
    {
        var service = new TradePayrollPersistenceService(new FakeTradePayrollDraftStore([]));
        var draft = Draft(Guid.NewGuid(), null, 1, "Plan");
        draft.Responsibilities =
        [
            new TradePayrollResponsibilityLine(100, RequiresHq: true, CommissionMaterialResponsibility.Provided)
        ];

        var lines = service.ApplyResponsibilities(
        [
            Line(100, "HQ Cloth", requiresHq: true),
            Line(101, "New Thread", requiresHq: false)
        ],
        draft);

        Assert.Equal(CommissionMaterialResponsibility.Provided, lines[0].Responsibility);
        Assert.Equal(CommissionMaterialResponsibility.Crafter, lines[1].Responsibility);
    }

    private static TradePayrollWorkflowDraft Draft(
        Guid companyProfileId,
        Guid? orderId,
        long planSessionVersion,
        string sourcePlanName)
    {
        return new TradePayrollWorkflowDraft
        {
            CompanyProfileId = companyProfileId,
            OrderId = orderId,
            PlanSessionVersion = planSessionVersion,
            SourcePlanName = sourcePlanName
        };
    }

    private static CommissionPayrollInputLine Line(int itemId, string name, bool requiresHq)
    {
        return new CommissionPayrollInputLine(
            itemId,
            name,
            3,
            100m,
            requiresHq,
            CommissionMaterialResponsibility.Crafter,
            "Evidence",
            "Explanation",
            DateTime.UtcNow,
            []);
    }

    private sealed class FakeTradePayrollDraftStore : ITradePayrollDraftStore
    {
        private readonly IReadOnlyList<TradePayrollWorkflowDraft> _drafts;

        public FakeTradePayrollDraftStore(IReadOnlyList<TradePayrollWorkflowDraft> drafts)
        {
            _drafts = drafts;
        }

        public List<TradePayrollWorkflowDraft> SavedDrafts { get; } = [];

        public Task<IReadOnlyList<TradePayrollWorkflowDraft>> LoadDraftsAsync(Guid companyProfileId)
        {
            return Task.FromResult<IReadOnlyList<TradePayrollWorkflowDraft>>(
                _drafts.Where(draft => draft.CompanyProfileId == companyProfileId).ToArray());
        }

        public Task<bool> SaveDraftAsync(TradePayrollWorkflowDraft draft)
        {
            SavedDrafts.Add(draft);
            return Task.FromResult(true);
        }

        public Task<bool> DeleteDraftAsync(string draftId)
        {
            return Task.FromResult(true);
        }
    }
}
