using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Tests;

public class TradeOperationsModelTests
{
    [Fact]
    public void CreateLocalCompanyProfile_UsesStableIdentityAndSyncFields()
    {
        var createdAt = new DateTime(2026, 6, 17, 12, 30, 0, DateTimeKind.Utc);

        var profile = TradeCompanyProfile.CreateLocal("Night Market Co.", createdAt);

        Assert.NotEqual(Guid.Empty, profile.Id);
        Assert.Equal(TradeCompanyProfile.CurrentSchemaVersion, profile.SchemaVersion);
        Assert.Equal("Night Market Co.", profile.Name);
        Assert.Equal(createdAt, profile.CreatedAtUtc);
        Assert.Equal(createdAt, profile.UpdatedAtUtc);
        Assert.Null(profile.RemoteId);
        Assert.Equal(TradeSyncState.LocalOnly, profile.SyncState);
    }

    [Fact]
    public void CrafterProfile_StoresCraftingJobLevelsOnly()
    {
        var crafter = new TradeCrafterProfile
        {
            CompanyProfileId = Guid.NewGuid(),
            DisplayName = "Aurelia",
            JobLevels =
            [
                new TradeCraftingJobLevel(TradeCraftingJob.Carpenter, 100),
                new TradeCraftingJobLevel(TradeCraftingJob.Goldsmith, 97)
            ]
        };

        Assert.Contains(crafter.JobLevels, job => job.Job == TradeCraftingJob.Carpenter && job.Level == 100);
        Assert.Contains(crafter.JobLevels, job => job.Job == TradeCraftingJob.Goldsmith && job.Level == 97);
        Assert.DoesNotContain(crafter.JobLevels.Select(job => job.Job.ToString()), name => name.Contains("Miner", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void OrderStatus_WorkflowOrderKeepsActiveStatusesBeforeArchiveStatuses()
    {
        var ordered = TradeOrderStatusWorkflow.ActiveStatuses.Concat(TradeOrderStatusWorkflow.ArchiveStatuses).ToArray();

        Assert.Equal(
            [
                TradeOrderStatus.Draft,
                TradeOrderStatus.ReadyToAssign,
                TradeOrderStatus.Assigned,
                TradeOrderStatus.InProgress,
                TradeOrderStatus.AwaitingDelivery,
                TradeOrderStatus.Completed,
                TradeOrderStatus.Canceled
            ],
            ordered);
    }

    [Fact]
    public void CreateManualNoteEvent_CapturesVisibleOrderHistoryNote()
    {
        var orderId = Guid.NewGuid();
        var companyProfileId = Guid.NewGuid();
        var createdAt = new DateTime(2026, 6, 17, 13, 0, 0, DateTimeKind.Utc);

        var history = TradeOrderHistoryEvent.CreateManualNote(
            companyProfileId,
            orderId,
            "Crafter asked for mats to be mailed.",
            createdAt);

        Assert.Equal(companyProfileId, history.CompanyProfileId);
        Assert.Equal(orderId, history.OrderId);
        Assert.Equal(TradeOrderHistoryEventKind.ManualNote, history.Kind);
        Assert.Equal("Crafter asked for mats to be mailed.", history.Note);
        Assert.Equal(createdAt, history.CreatedAtUtc);
    }

    [Fact]
    public void OrderSourceSnapshot_DefaultsToActiveCraftPlanForExistingOrders()
    {
        var snapshot = new TradeOrderSourceSnapshot();

        Assert.Equal(TradeOrderSourceKind.ActiveCraftPlan, snapshot.SourceKind);
        Assert.Equal("Active craft plan", snapshot.SourcePlanName);
        Assert.Null(snapshot.SourcePlanId);
        Assert.Null(snapshot.DataCenter);
        Assert.Null(snapshot.World);
    }
}
