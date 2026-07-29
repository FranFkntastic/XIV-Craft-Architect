using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.LodestoneLookup.Services.Discord;
using Microsoft.Data.Sqlite;

namespace FFXIV_Craft_Architect.ContractTests;

public sealed class DiscordCollaborationStoreContractTests
{
    [Fact]
    public void LifecycleProjection_OnlyOpenMessagesExposeVolunteer()
    {
        var published = new PublishedCommissionBrief
        {
            PublicId = "public-brief-03",
            Version = 1,
            PublishedAtUtc = DateTime.UnixEpoch,
            Brief = new CommissionBriefDocument
            {
                CompanyName = "The Studium",
                Title = "Raid gear",
                Outputs = [new CommissionBriefOutput(1, "Test Item", 1, false)]
            }
        };
        var actionToken = SqliteDiscordCollaborationStore.CreateActionToken();
        var open = JsonSerializer.SerializeToElement(DiscordCommissionMessage.Create(
            published,
            "https://example.test/commission?id=",
            DiscordPublicationState.Open,
            actionToken));
        var assigned = JsonSerializer.SerializeToElement(DiscordCommissionMessage.Create(
            published,
            "https://example.test/commission?id=",
            DiscordPublicationState.Assigned,
            actionToken));

        Assert.Equal(
            actionToken,
            open.GetProperty("components")[0]
                .GetProperty("components")[1]
                .GetProperty("custom_id")
                .GetString());
        Assert.Single(assigned.GetProperty("components")[0]
            .GetProperty("components")
            .EnumerateArray());
        Assert.Empty(open.GetProperty("allowed_mentions").GetProperty("parse").EnumerateArray());
        Assert.Empty(assigned.GetProperty("allowed_mentions").GetProperty("parse").EnumerateArray());
    }

    [Fact]
    public async Task PublicationAndVolunteerReplay_AreDurableAndTenantBound()
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            $"ca-discord-collaboration-{Guid.NewGuid():N}.db");
        try
        {
            var store = CreateStore(databasePath);
            var companyId = new CompanyId(
                Guid.Parse("018fdc85-9b7a-7c31-87ed-6f9bdb4a5555"));
            var binding = new DiscordCompanyInstallationBinding(
                Guid.Parse("67d69f89-6aaf-47ac-a518-f5ec5e328095"),
                companyId,
                "100000000000000001",
                "100000000000000002",
                "100000000000000003",
                DiscordRuntimePermission.Required,
                true,
                DateTimeOffset.UnixEpoch);
            var ownership = new TradeCompanyPublicationOwnership(
                companyId,
                Guid.Parse("cc58c224-d6e6-402b-bcdd-e7b45dd00b55"),
                new CompanyRecordRevision(7));
            await store.UpsertInstallationAsync(binding);

            var created = await store.CreatePublicationAsync(
                binding,
                ownership,
                "public-brief-01",
                1,
                "publish-operation-01",
                SqliteDiscordCollaborationStore.CreateActionToken(),
                DiscordPublicationState.Open,
                JsonSerializer.Serialize(DiscordCommissionMessage.CreateEphemeral("queued")),
                DateTimeOffset.UnixEpoch);
            var replay = await store.CreatePublicationAsync(
                binding,
                ownership,
                "public-brief-01",
                1,
                "publish-operation-01",
                SqliteDiscordCollaborationStore.CreateActionToken(),
                DiscordPublicationState.Open,
                JsonSerializer.Serialize(DiscordCommissionMessage.CreateEphemeral("queued")),
                DateTimeOffset.UnixEpoch);

            Assert.Equal(DiscordPublicationCreateStatus.Created, created.Status);
            Assert.Equal(DiscordPublicationCreateStatus.Replayed, replay.Status);
            Assert.Equal(created.Publication?.PublicationId, replay.Publication?.PublicationId);

            var leased = await store.LeaseDueAsync(
                DateTimeOffset.UnixEpoch,
                TimeSpan.FromSeconds(30),
                10);
            var work = Assert.Single(leased);
            await store.CompleteAsync(
                work.WorkItemId,
                work.LeaseId,
                "100000000000000004",
                DateTimeOffset.UnixEpoch.AddSeconds(1));
            var posted = await store.LoadPublicationAsync(created.Publication!.PublicationId);

            Assert.Equal("100000000000000004", posted?.MessageId);
            var interaction = new DiscordVolunteerInteraction(
                "100000000000000005",
                binding.ApplicationId,
                binding.GuildId,
                binding.ChannelId,
                posted!.MessageId!,
                posted.ActionToken,
                "100000000000000006",
                "Volunteer");
            var firstClaim = await store.RecordInterestAsync(interaction);
            var duplicateClaim = await store.RecordInterestAsync(interaction with
            {
                InteractionId = "100000000000000007"
            });
            var wrongChannel = await store.RecordInterestAsync(interaction with
            {
                InteractionId = "100000000000000008",
                ChannelId = "100000000000000009"
            });
            var claims = await store.LoadPendingClaimsAsync(companyId, ownership.OrderId);

            Assert.Equal(DiscordVolunteerInteractionStatus.Recorded, firstClaim.Status);
            Assert.Equal(DiscordVolunteerInteractionStatus.Replayed, duplicateClaim.Status);
            Assert.Equal(DiscordVolunteerInteractionStatus.NoLongerOpen, wrongChannel.Status);
            Assert.Single(claims);
            Assert.Equal(interaction.DiscordUserId, claims[0].DiscordUserId);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(databasePath))
            {
                File.Delete(databasePath);
            }
        }
    }

    [Fact]
    public async Task ClaimAcceptanceSaga_OnlyFinalizesTheMatchingOperation()
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            $"ca-discord-claim-{Guid.NewGuid():N}.db");
        try
        {
            var fixture = await CreatePendingClaimAsync(databasePath);
            var claim = Assert.Single(
                await fixture.Store.LoadPendingClaimsAsync(
                    fixture.CompanyId,
                    fixture.OrderId));
            var begun = await fixture.Store.BeginClaimAcceptanceAsync(
                fixture.CompanyId,
                claim.ClaimId,
                "accept-operation-01");
            var wrongCompletion = await fixture.Store.CompleteClaimAcceptanceAsync(
                fixture.CompanyId,
                claim.ClaimId,
                "different-operation",
                fixture.CrafterId,
                new CompanyRecordRevision(8),
                DateTimeOffset.UnixEpoch.AddMinutes(1));
            var completed = await fixture.Store.CompleteClaimAcceptanceAsync(
                fixture.CompanyId,
                claim.ClaimId,
                "accept-operation-01",
                fixture.CrafterId,
                new CompanyRecordRevision(8),
                DateTimeOffset.UnixEpoch.AddMinutes(1));
            var replayed = await fixture.Store.CompleteClaimAcceptanceAsync(
                fixture.CompanyId,
                claim.ClaimId,
                "accept-operation-01",
                fixture.CrafterId,
                new CompanyRecordRevision(8),
                DateTimeOffset.UnixEpoch.AddMinutes(1));

            Assert.Equal(DiscordClaimTransitionStatus.Applied, begun.Status);
            Assert.Equal(DiscordClaimTransitionStatus.Conflict, wrongCompletion.Status);
            Assert.Equal(DiscordClaimTransitionStatus.Applied, completed.Status);
            Assert.Equal(DiscordClaimTransitionStatus.Replayed, replayed.Status);
            Assert.Equal(DiscordInterestClaimState.Accepted, completed.Claim?.State);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(databasePath))
            {
                File.Delete(databasePath);
            }
        }
    }

    private static SqliteDiscordCollaborationStore CreateStore(string databasePath) =>
        new(new DiscordCommissionOptions
        {
            Enabled = true,
            ApplicationId = "100000000000000001",
            PublicKey = new string('0', 64),
            AllowedGuildId = "100000000000000002",
            AllowedChannelId = "100000000000000003",
            CommissionBaseUrl = "https://example.test/commission?id=",
            DatabasePath = databasePath
        });

    private static async Task<ClaimFixture> CreatePendingClaimAsync(string databasePath)
    {
        var store = CreateStore(databasePath);
        var companyId = new CompanyId(
            Guid.Parse("018fdc85-9b7a-7c31-87ed-6f9bdb4a6666"));
        var orderId = Guid.Parse("cc58c224-d6e6-402b-bcdd-e7b45dd00b66");
        var binding = new DiscordCompanyInstallationBinding(
            Guid.Parse("67d69f89-6aaf-47ac-a518-f5ec5e328096"),
            companyId,
            "200000000000000001",
            "200000000000000002",
            "200000000000000003",
            DiscordRuntimePermission.Required,
            true,
            DateTimeOffset.UnixEpoch);
        await store.UpsertInstallationAsync(binding);
        var publication = await store.CreatePublicationAsync(
            binding,
            new TradeCompanyPublicationOwnership(
                companyId,
                orderId,
                new CompanyRecordRevision(7)),
            "public-brief-02",
            1,
            "publish-operation-02",
            SqliteDiscordCollaborationStore.CreateActionToken(),
            DiscordPublicationState.Open,
            JsonSerializer.Serialize(DiscordCommissionMessage.CreateEphemeral("queued")),
            DateTimeOffset.UnixEpoch);
        var work = Assert.Single(
            await store.LeaseDueAsync(
                DateTimeOffset.UnixEpoch,
                TimeSpan.FromSeconds(30),
                10));
        await store.CompleteAsync(
            work.WorkItemId,
            work.LeaseId,
            "200000000000000004",
            DateTimeOffset.UnixEpoch.AddSeconds(1));
        var posted = await store.LoadPublicationAsync(publication.Publication!.PublicationId);
        var result = await store.RecordInterestAsync(new DiscordVolunteerInteraction(
            "200000000000000005",
            binding.ApplicationId,
            binding.GuildId,
            binding.ChannelId,
            posted!.MessageId!,
            posted.ActionToken,
            "200000000000000006",
            "Volunteer"));
        Assert.Equal(DiscordVolunteerInteractionStatus.Recorded, result.Status);
        return new ClaimFixture(
            store,
            companyId,
            orderId,
            Guid.Parse("9187e2bd-f941-4e3f-82e4-04507c46127d"));
    }

    private sealed record ClaimFixture(
        SqliteDiscordCollaborationStore Store,
        CompanyId CompanyId,
        Guid OrderId,
        Guid CrafterId);
}
