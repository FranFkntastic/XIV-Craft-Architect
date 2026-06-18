using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Tests;

public class TradePayrollWorkflowModelTests
{
    [Fact]
    public void PayrollWorkflowDraft_StoresDurableCompanyOrderAndResponsibilityState()
    {
        var companyProfileId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var createdAt = new DateTime(2026, 6, 18, 14, 0, 0, DateTimeKind.Utc);
        var crafterId = Guid.NewGuid();

        var draft = new TradePayrollWorkflowDraft
        {
            CompanyProfileId = companyProfileId,
            OrderId = orderId,
            PlanSessionVersion = 42,
            MarketAnalysisVersion = 84,
            SourcePlanName = "Rinascita Commission",
            AssignedCrafterId = crafterId,
            AssignedCrafterDisplayName = "Riviene Cahernaut",
            CreatedAtUtc = createdAt,
            UpdatedAtUtc = createdAt,
            Responsibilities =
            [
                new TradePayrollResponsibilityLine(
                    36105,
                    RequiresHq: true,
                    CommissionMaterialResponsibility.Provided)
            ]
        };

        Assert.False(string.IsNullOrWhiteSpace(draft.Id));
        Assert.Equal(companyProfileId, draft.CompanyProfileId);
        Assert.Equal(orderId, draft.OrderId);
        Assert.Equal(42, draft.PlanSessionVersion);
        Assert.Equal(84, draft.MarketAnalysisVersion);
        Assert.Equal("Rinascita Commission", draft.SourcePlanName);
        Assert.Equal(crafterId, draft.AssignedCrafterId);
        Assert.Equal("Riviene Cahernaut", draft.AssignedCrafterDisplayName);
        Assert.Null(draft.RemoteId);
        Assert.Equal(TradeSyncState.LocalOnly, draft.SyncState);
        Assert.Equal(createdAt, draft.CreatedAtUtc);
        Assert.Equal(createdAt, draft.UpdatedAtUtc);

        var responsibility = Assert.Single(draft.Responsibilities);
        Assert.Equal(36105, responsibility.ItemId);
        Assert.True(responsibility.RequiresHq);
        Assert.Equal(CommissionMaterialResponsibility.Provided, responsibility.Responsibility);
    }
}
