using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.LodestoneLookup.Services.CommissionBriefs;
using FFXIV_Craft_Architect.LodestoneLookup.Services.Discord;
using FFXIV_Craft_Architect.LodestoneLookup.Services.Identity;
using FFXIV_Craft_Architect.LodestoneLookup.Services.ProfileHosting;
using FFXIV_Craft_Architect.LodestoneLookup.Services.TradeCompanies;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace FFXIV_Craft_Architect.ContractTests;

public sealed class CompanyRetirementContractTests
{
    private const string PublicationCollection = "tradeCompany.publication";
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task RetirementRefusesWhileReferencedThenLeavesOnlyTombstones()
    {
        var root = Path.Combine(Path.GetTempPath(), $"craft-architect-company-retirement-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var profileOptions = new ProfileHostOptions { Enabled = true, DatabasePath = Path.Combine(root, "profiles.db") };
            var membershipOptions = new TradeMembershipOptions { DatabasePath = Path.Combine(root, "memberships.db") };
            var profiles = new SqliteProfileHostStore(profileOptions);
            var memberships = new SqliteMembershipStore(
                membershipOptions,
                TimeProvider.System,
                NullLogger<SqliteMembershipStore>.Instance);
            var discordOptions = new DiscordCommissionOptions { DatabasePath = Path.Combine(root, "discord.db") };
            var service = new CompanyRetirementService(
                profileOptions,
                membershipOptions,
                new CommissionBriefOptions { DatabasePath = Path.Combine(root, "briefs.db") },
                discordOptions,
                new DiscordIdentityOptions { DatabasePath = Path.Combine(root, "identity.db") },
                profiles,
                memberships,
                new SqliteDiscordCollaborationStore(discordOptions),
                NullLogger<CompanyRetirementService>.Instance);

            var host = (await profiles.CreateProfileAsync("Company host", CancellationToken.None)).ProfileId;
            var stale = new CompanyId(Guid.NewGuid());
            var live = new CompanyId(Guid.NewGuid());
            var staleCrafter = Guid.NewGuid();
            var liveTwin = Guid.NewGuid();
            var stalePublication = Guid.NewGuid().ToString("D");
            var livePublication = Guid.NewGuid().ToString("D");

            // July-era PascalCase payloads, as found in production.
            await PutAsync(profiles, host, ProfileSyncCollections.TradeCompanyProfiles, stale.ToString(),
                JsonSerializer.Serialize(new { Id = stale.Value, DisplayName = "FFXIV Trade Company" }));
            await PutAsync(profiles, host, ProfileSyncCollections.TradeCrafters, staleCrafter.ToString("D"),
                JsonSerializer.Serialize(new TradeCrafterProfile
                {
                    Id = staleCrafter,
                    CompanyProfileId = stale.Value,
                    DisplayName = "July crafter",
                    LodestoneCharacterId = "4242"
                }));
            await PutAsync(profiles, host, PublicationCollection, stalePublication,
                JsonSerializer.Serialize(new TradeCompanyPublicationOwnership(stale, Guid.NewGuid(), new CompanyRecordRevision(1)), WebJson));
            await PutAsync(profiles, host, ProfileSyncCollections.TradeCrafters, liveTwin.ToString("D"),
                JsonSerializer.Serialize(new TradeCrafterProfile
                {
                    Id = liveTwin,
                    CompanyProfileId = live.Value,
                    DisplayName = "Current crafter",
                    LodestoneCharacterId = "4242"
                }, WebJson));
            await PutAsync(profiles, host, PublicationCollection, livePublication,
                JsonSerializer.Serialize(new TradeCompanyPublicationOwnership(live, Guid.NewGuid(), new CompanyRecordRevision(1)), WebJson));
            await memberships.IssueInvitationAsync(stale, Guid.Parse(host), staleCrafter);
            await memberships.BindCrafterAsync(stale, staleCrafter, Guid.NewGuid(), CrafterAccountBindingEvidence.OperatorConfirmed, Guid.Parse(host));

            // Another company's live order still points at the stale crafter: refuse, mutate nothing.
            var order = new TradeOrder
            {
                CompanyProfileId = live.Value,
                Title = "Live order",
                AssignedCrafterId = staleCrafter
            };
            var orderRevision = await PutAsync(profiles, host, ProfileSyncCollections.TradeOrders, order.Id.ToString("D"),
                JsonSerializer.Serialize(order, WebJson));
            var refused = await service.RunAsync([stale], dryRun: false, CancellationToken.None);
            Assert.False(refused.Applied);
            Assert.Empty(refused.Backups);
            var refusal = Assert.Single(refused.Companies);
            Assert.Equal("refused", refusal.Status);
            Assert.Contains(refusal.RefusalReasons, reason => reason.StartsWith("crafter_referenced:", StringComparison.Ordinal));
            Assert.Equal(3, refusal.Tombstones.Count);
            Assert.Equal(1, (await memberships.LoadCompanyFootprintAsync(stale)).OpenInvitations);
            Assert.False(await IsDeletedAsync(profiles, ProfileSyncCollections.TradeCrafters, staleCrafter.ToString("D")));

            order.AssignedCrafterId = liveTwin;
            await PutAsync(profiles, host, ProfileSyncCollections.TradeOrders, order.Id.ToString("D"),
                JsonSerializer.Serialize(order, WebJson), orderRevision);

            var planned = await service.RunAsync([stale], dryRun: true, CancellationToken.None);
            Assert.Equal("planned", Assert.Single(planned.Companies).Status);
            Assert.False(await IsDeletedAsync(profiles, ProfileSyncCollections.TradeCompanyProfiles, stale.ToString()));

            var retired = await service.RunAsync([stale], dryRun: false, CancellationToken.None);
            Assert.True(retired.Applied);
            Assert.NotEmpty(retired.Backups);
            Assert.Equal("retired", Assert.Single(retired.Companies).Status);
            Assert.True(await IsDeletedAsync(profiles, ProfileSyncCollections.TradeCompanyProfiles, stale.ToString()));
            Assert.True(await IsDeletedAsync(profiles, ProfileSyncCollections.TradeCrafters, staleCrafter.ToString("D")));
            var liveObjects = await profiles.LoadAllLiveObjectsAsync(CancellationToken.None);
            Assert.DoesNotContain(liveObjects, item => item.ObjectId == stalePublication);
            Assert.Contains(liveObjects, item => item.ObjectId == livePublication);
            Assert.False(await IsDeletedAsync(profiles, ProfileSyncCollections.TradeCrafters, liveTwin.ToString("D")));
            var footprint = await memberships.LoadCompanyFootprintAsync(stale);
            Assert.Equal((0, 0, true), (footprint.OpenInvitations, footprint.CrafterBindings, footprint.AlreadyRetired));

            // A stale browser copy cannot resurrect the retired row.
            var tombstone = (await profiles.LoadObjectsAsync(ProfileSyncCollections.TradeCompanyProfiles, includeDeleted: true, CancellationToken.None))
                .Single(item => item.Object.ObjectId == stale.ToString()).Object;
            var resurrect = await profiles.PutObjectAsync(
                host,
                ProfileSyncCollections.TradeCompanyProfiles,
                stale.ToString(),
                JsonSerializer.Serialize(new { Id = stale.Value, DisplayName = "FFXIV Trade Company" }),
                tombstone.Revision,
                CancellationToken.None);
            Assert.Equal("retired_object", resurrect.ErrorCode);
            Assert.True(resurrect.RemoteObject!.Deleted);

            var rerun = await service.RunAsync([stale], dryRun: false, CancellationToken.None);
            Assert.Equal("already_retired", Assert.Single(rerun.Companies).Status);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<long> PutAsync(
        SqliteProfileHostStore profiles,
        string profileId,
        string collection,
        string objectId,
        string payloadJson,
        long expectedRevision = 0)
    {
        var put = await profiles.PutObjectAsync(
            profileId,
            collection,
            objectId,
            payloadJson,
            expectedRevision,
            CancellationToken.None,
            allowCompanyCollection: collection.StartsWith("tradeCompany.", StringComparison.Ordinal));
        Assert.True(put.Success, put.ErrorMessage);
        return put.Object!.Revision;
    }

    private static async Task<bool> IsDeletedAsync(
        SqliteProfileHostStore profiles,
        string collection,
        string objectId) =>
        (await profiles.LoadObjectsAsync(collection, includeDeleted: true, CancellationToken.None))
        .Single(item => item.Object.ObjectId == objectId)
        .Object.Deleted;
}
