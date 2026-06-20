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
    public void TradeCrafterProfile_CanStoreLodestoneProvenance()
    {
        var syncedAt = new DateTime(2026, 6, 20, 4, 0, 0, DateTimeKind.Utc);

        var crafter = new TradeCrafterProfile
        {
            DisplayName = "Level Checker",
            LodestoneCharacterId = "16331040",
            LodestoneProfileUrl = "https://na.finalfantasyxiv.com/lodestone/character/16331040/",
            LodestoneAvatarUrl = "https://img2.finalfantasyxiv.com/example.jpg",
            LodestonePortraitUrl = "https://img2.finalfantasyxiv.com/portrait.jpg",
            LodestoneFreeCompanyName = "Terms of Service",
            LodestoneRace = "Viera",
            LodestoneClan = "Veena",
            LodestoneGender = "Female",
            LodestoneLastSyncedAtUtc = syncedAt
        };

        Assert.Equal("16331040", crafter.LodestoneCharacterId);
        Assert.Equal("https://na.finalfantasyxiv.com/lodestone/character/16331040/", crafter.LodestoneProfileUrl);
        Assert.Equal("https://img2.finalfantasyxiv.com/example.jpg", crafter.LodestoneAvatarUrl);
        Assert.Equal("https://img2.finalfantasyxiv.com/portrait.jpg", crafter.LodestonePortraitUrl);
        Assert.Equal("Terms of Service", crafter.LodestoneFreeCompanyName);
        Assert.Equal("Viera", crafter.LodestoneRace);
        Assert.Equal("Veena", crafter.LodestoneClan);
        Assert.Equal("Female", crafter.LodestoneGender);
        Assert.Equal(syncedAt, crafter.LodestoneLastSyncedAtUtc);
    }

    [Fact]
    public void TradeCrafterProfile_CanStoreLocalContactIdentity()
    {
        var crafter = new TradeCrafterProfile
        {
            DisplayName = "Level Checker",
            Alias = "LC",
            DiscordHandle = "levelchecker",
            SocialProfileUrl = "https://example.com/levelchecker"
        };

        Assert.Equal("LC", crafter.Alias);
        Assert.Equal("levelchecker", crafter.DiscordHandle);
        Assert.Equal("https://example.com/levelchecker", crafter.SocialProfileUrl);
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

    [Fact]
    public void TradeOrder_DefaultCraftPlanLinkKindIsUnknownForLegacySafety()
    {
        var order = new TradeOrder();

        Assert.Null(order.CraftPlanId);
        Assert.Equal(TradeOrderCraftPlanLinkKind.Unknown, order.CraftPlanLinkKind);
    }
}
