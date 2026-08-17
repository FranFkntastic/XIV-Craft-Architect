using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.LodestoneLookup.Services.ProfileHosting;
using FFXIV_Craft_Architect.LodestoneLookup.Services.TradeCompanies;
using Microsoft.Extensions.Logging.Abstractions;

namespace FFXIV_Craft_Architect.ContractTests;

public sealed class CompanyOwnershipTransferContractTests
{
    private static readonly JsonSerializerOptions JsonOptions = ProfileSyncJson.CreateOptions();

    [Fact]
    public async Task TransferMovesCanonicalUnionProjectsMembershipAndReplays()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ca-ownership-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var memberships = new SqliteMembershipStore(
                new TradeMembershipOptions { DatabasePath = Path.Combine(root, "memberships.db") },
                TimeProvider.System,
                NullLogger<SqliteMembershipStore>.Instance);
            var profiles = new SqliteProfileHostStore(
                new ProfileHostOptions { DatabasePath = Path.Combine(root, "profiles.db"), DeepArchiveEnabled = true },
                founderBinder: memberships);
            var transfers = new CompanyOwnershipTransferService(profiles, memberships);
            var source = await profiles.CreateProfileAsync("Current owner", CancellationToken.None);
            var target = await profiles.CreateProfileAsync("Next owner", CancellationToken.None);
            var sourceId = Guid.Parse(source.ProfileId);
            var targetId = Guid.Parse(target.ProfileId);
            var company = TradeCompanyProfile.CreateLocal("Transfer Fixture", DateTime.UtcNow);
            var companyId = new CompanyId(company.Id);

            Assert.True((await PutAsync(profiles, source.ProfileId, ProfileSyncCollections.TradeCompanyProfiles, company.Id, company)).Success);
            await memberships.RequestAsync(companyId, targetId, null);
            Assert.Equal(MembershipMutationStatus.Applied, (await memberships.ApproveAsync(companyId, targetId, sourceId)).Status);

            var sharedCrafterId = Guid.NewGuid();
            var targetOnlyCrafterId = Guid.NewGuid();
            Assert.True((await PutAsync(profiles, target.ProfileId, ProfileSyncCollections.TradeCrafters, sharedCrafterId, new TradeCrafterProfile
            {
                Id = sharedCrafterId, CompanyProfileId = company.Id, DisplayName = "Target shadow"
            })).Success);
            Assert.True((await PutAsync(profiles, target.ProfileId, ProfileSyncCollections.TradeCrafters, targetOnlyCrafterId, new TradeCrafterProfile
            {
                Id = targetOnlyCrafterId, CompanyProfileId = company.Id, DisplayName = "Target-only contractor"
            })).Success);
            Assert.True((await PutAsync(profiles, source.ProfileId, ProfileSyncCollections.TradeCrafters, sharedCrafterId, new TradeCrafterProfile
            {
                Id = sharedCrafterId, CompanyProfileId = company.Id, DisplayName = "Canonical source"
            })).Success);

            var orderId = Guid.NewGuid();
            var planId = Guid.NewGuid().ToString("D");
            var savedAt = DateTime.UtcNow.AddDays(-200);
            var plan = new ProfileSyncPlanSnapshot
            {
                Id = planId,
                Name = "Archived order plan",
                SavedAt = savedAt,
                LinkedOrderId = orderId,
                PlanJson = "{\"sealed\":true}"
            };
            Assert.True((await profiles.PutObjectAsync(
                source.ProfileId,
                ProfileSyncCollections.Plans,
                planId,
                ProfileSyncPlanPayloadCodec.Serialize(plan),
                0,
                CancellationToken.None)).Success);
            var order = new TradeOrder
            {
                Id = orderId,
                CompanyProfileId = company.Id,
                Title = "Archived transfer order",
                Status = TradeOrderStatus.Completed,
                CraftPlanId = planId,
                CraftPlanSavedAtUtc = savedAt,
                CraftPlanLinkKind = TradeOrderCraftPlanLinkKind.OrderGenerated
            };
            var orderPut = await PutAsync(profiles, source.ProfileId, ProfileSyncCollections.TradeOrders, orderId, order);
            Assert.True(orderPut.Success);
            Assert.True(await profiles.MoveOrderToDeepArchiveAsync(source.ProfileId, orderPut.Object!, CancellationToken.None));
            Assert.True((await profiles.PutObjectAsync(
                source.ProfileId,
                "tradeCompany.publication",
                Guid.NewGuid().ToString("D"),
                JsonSerializer.Serialize(new { companyId = company.Id, kind = "fixture" }, JsonOptions),
                0,
                CancellationToken.None,
                allowCompanyCollection: true)).Success);

            var preview = await transfers.PreviewAsync(companyId, sourceId, targetId);
            Assert.Equal(CompanyOwnershipTransferStatus.Applied, preview.Status);
            Assert.Equal(1, preview.Preview!.Counts.Collisions);
            Assert.Equal(1, preview.Preview.Counts.TargetOnlyObjects);
            Assert.Equal(1, preview.Preview.Counts.DeepArchivedOrders);
            Assert.Equal(1, preview.Preview.Counts.LinkedPlans);

            var key = Guid.NewGuid();
            var applied = await transfers.TransferAsync(
                companyId,
                sourceId,
                targetId,
                PreviousOwnerDisposition.Operator,
                key,
                preview.Preview.ScopeFingerprint);
            var replay = await transfers.TransferAsync(
                companyId,
                sourceId,
                targetId,
                PreviousOwnerDisposition.Operator,
                key,
                preview.Preview.ScopeFingerprint);

            Assert.Equal(CompanyOwnershipTransferStatus.Applied, applied.Status);
            Assert.Equal(CompanyOwnershipTransferStatus.Replayed, replay.Status);
            Assert.Equal(applied.Receipt!.TransferId, replay.Receipt!.TransferId);
            Assert.NotNull(applied.Receipt.MembershipProjectedAtUtc);
            var hosted = await profiles.FindObjectAsync(ProfileSyncCollections.TradeCompanyProfiles, company.Id.ToString("D"), CancellationToken.None);
            Assert.Equal(target.ProfileId, hosted!.ProfileId);
            Assert.True((await profiles.LoadObjectAsync(source.ProfileId, ProfileSyncCollections.TradeCrafters, sharedCrafterId.ToString("D"), CancellationToken.None))!.Deleted);
            var canonicalCrafter = await profiles.LoadObjectAsync(target.ProfileId, ProfileSyncCollections.TradeCrafters, sharedCrafterId.ToString("D"), CancellationToken.None);
            Assert.Contains("Canonical source", canonicalCrafter!.PayloadJson);
            Assert.False((await profiles.LoadObjectAsync(target.ProfileId, ProfileSyncCollections.TradeCrafters, targetOnlyCrafterId.ToString("D"), CancellationToken.None))!.Deleted);
            Assert.False((await profiles.LoadObjectAsync(target.ProfileId, ProfileSyncCollections.Plans, planId, CancellationToken.None))!.Deleted);
            Assert.NotNull(await profiles.LoadDeepArchivedOrderAsync(target.ProfileId, orderId.ToString("D"), CancellationToken.None));
            Assert.Null(await profiles.LoadDeepArchivedOrderAsync(source.ProfileId, orderId.ToString("D"), CancellationToken.None));
            Assert.Equal(MembershipRole.Owner, (await memberships.LoadAsync(companyId, targetId))!.Role);
            Assert.Equal(MembershipRole.Operator, (await memberships.LoadAsync(companyId, sourceId))!.Role);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task TransferRejectsScopeDriftBeforeMutation()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ca-ownership-drift-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var memberships = new SqliteMembershipStore(new TradeMembershipOptions { DatabasePath = Path.Combine(root, "memberships.db") }, TimeProvider.System, NullLogger<SqliteMembershipStore>.Instance);
            var profiles = new SqliteProfileHostStore(new ProfileHostOptions { DatabasePath = Path.Combine(root, "profiles.db") }, founderBinder: memberships);
            var transfers = new CompanyOwnershipTransferService(profiles, memberships);
            var source = await profiles.CreateProfileAsync("Source", CancellationToken.None);
            var target = await profiles.CreateProfileAsync("Target", CancellationToken.None);
            var sourceId = Guid.Parse(source.ProfileId);
            var targetId = Guid.Parse(target.ProfileId);
            var company = TradeCompanyProfile.CreateLocal("Drift Fixture", DateTime.UtcNow);
            var companyId = new CompanyId(company.Id);
            Assert.True((await PutAsync(profiles, source.ProfileId, ProfileSyncCollections.TradeCompanyProfiles, company.Id, company)).Success);
            await memberships.RequestAsync(companyId, targetId, null);
            await memberships.ApproveAsync(companyId, targetId, sourceId);
            var preview = (await transfers.PreviewAsync(companyId, sourceId, targetId)).Preview!;
            Assert.Equal(CompanyOwnershipTransferStatus.Forbidden, (await transfers.PreviewAsync(companyId, targetId, sourceId)).Status);
            var crafterId = Guid.NewGuid();
            Assert.True((await PutAsync(profiles, source.ProfileId, ProfileSyncCollections.TradeCrafters, crafterId, new TradeCrafterProfile
            {
                Id = crafterId, CompanyProfileId = company.Id, DisplayName = "Late change"
            })).Success);

            var result = await transfers.TransferAsync(companyId, sourceId, targetId, PreviousOwnerDisposition.Revoked, Guid.NewGuid(), preview.ScopeFingerprint);

            Assert.Equal(CompanyOwnershipTransferStatus.Conflict, result.Status);
            Assert.Equal(source.ProfileId, (await profiles.FindObjectAsync(ProfileSyncCollections.TradeCompanyProfiles, company.Id.ToString("D"), CancellationToken.None))!.ProfileId);
            Assert.Equal(MembershipRole.Crafter, (await memberships.LoadAsync(companyId, targetId))!.Role);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task StartupReconciliationFinishesCommittedMembershipProjection()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ca-ownership-reconcile-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var memberships = new SqliteMembershipStore(new TradeMembershipOptions { DatabasePath = Path.Combine(root, "memberships.db") }, TimeProvider.System, NullLogger<SqliteMembershipStore>.Instance);
            var profiles = new SqliteProfileHostStore(new ProfileHostOptions { DatabasePath = Path.Combine(root, "profiles.db") }, founderBinder: memberships);
            var transfers = new CompanyOwnershipTransferService(profiles, memberships);
            var source = await profiles.CreateProfileAsync("Interrupted source", CancellationToken.None);
            var target = await profiles.CreateProfileAsync("Interrupted target", CancellationToken.None);
            var sourceId = Guid.Parse(source.ProfileId);
            var targetId = Guid.Parse(target.ProfileId);
            var company = TradeCompanyProfile.CreateLocal("Interrupted Fixture", DateTime.UtcNow);
            var companyId = new CompanyId(company.Id);
            Assert.True((await PutAsync(profiles, source.ProfileId, ProfileSyncCollections.TradeCompanyProfiles, company.Id, company)).Success);
            await memberships.RequestAsync(companyId, targetId, null);
            await memberships.ApproveAsync(companyId, targetId, sourceId);
            var preview = (await transfers.PreviewAsync(companyId, sourceId, targetId)).Preview!;
            var committed = await profiles.CommitCompanyOwnershipTransferAsync(
                companyId,
                sourceId,
                targetId,
                PreviousOwnerDisposition.Revoked,
                Guid.NewGuid(),
                preview.ScopeFingerprint);
            Assert.Null(committed.Receipt!.MembershipProjectedAtUtc);
            Assert.Equal(MembershipRole.Crafter, (await memberships.LoadAsync(companyId, targetId))!.Role);

            Assert.Equal(1, await transfers.ReconcilePendingAsync());

            Assert.Equal(MembershipRole.Owner, (await memberships.LoadAsync(companyId, targetId))!.Role);
            Assert.Equal(MembershipState.Revoked, (await memberships.LoadAsync(companyId, sourceId))!.State);
            Assert.Empty(await profiles.LoadPendingOwnershipTransfersAsync());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static Task<ProfileSyncPutResponse> PutAsync(
        SqliteProfileHostStore profiles,
        string profileId,
        string collection,
        Guid objectId,
        object payload) => profiles.PutObjectAsync(
            profileId,
            collection,
            objectId.ToString("D"),
            JsonSerializer.Serialize(payload, JsonOptions),
            0,
            CancellationToken.None);
}
