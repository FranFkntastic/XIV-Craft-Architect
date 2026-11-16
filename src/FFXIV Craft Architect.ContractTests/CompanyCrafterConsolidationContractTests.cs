using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.LodestoneLookup.Services.ProfileHosting;
using FFXIV_Craft_Architect.LodestoneLookup.Services.TradeCompanies;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace FFXIV_Craft_Architect.ContractTests;

public sealed class CompanyCrafterConsolidationContractTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    private static readonly Guid CompanyA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid CompanyB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");
    private static readonly Guid AccountX = Guid.Parse("eeeeeeee-0000-0000-0000-00000000000a");
    private static readonly Guid AccountY = Guid.Parse("eeeeeeee-0000-0000-0000-00000000000b");

    // Lodestone 100: older unbound row vs. newer bound row -> the bound row survives.
    private static readonly Guid OlderUnbound = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid NewerBound = Guid.Parse("00000000-0000-0000-0000-000000000002");
    // Lodestone 200: bound to two different accounts -> skipped.
    private static readonly Guid BoundToX = Guid.Parse("00000000-0000-0000-0000-000000000003");
    private static readonly Guid BoundToY = Guid.Parse("00000000-0000-0000-0000-000000000004");
    // Lodestone 300: same creation time, nothing bound -> lowest ID survives.
    private static readonly Guid TieLow = Guid.Parse("00000000-0000-0000-0000-000000000005");
    private static readonly Guid TieHigh = Guid.Parse("00000000-0000-0000-0000-000000000006");
    // Lodestone 100 in another company -> never touched.
    private static readonly Guid OtherCompanyTwin = Guid.Parse("00000000-0000-0000-0000-000000000007");

    [Fact]
    public async Task DuplicateLodestoneCraftersMergeIntoOneSurvivorAndRerunsAreNoOps()
    {
        var root = Path.Combine(Path.GetTempPath(), $"craft-architect-crafter-merge-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var profileOptions = new ProfileHostOptions
            {
                Enabled = true,
                DatabasePath = Path.Combine(root, "profiles.db")
            };
            var membershipOptions = new TradeMembershipOptions
            {
                DatabasePath = Path.Combine(root, "memberships.db")
            };
            var profiles = new SqliteProfileHostStore(profileOptions);
            var memberships = new SqliteMembershipStore(
                membershipOptions,
                TimeProvider.System,
                NullLogger<SqliteMembershipStore>.Instance);
            var service = new CompanyCrafterConsolidationService(
                profileOptions,
                membershipOptions,
                profiles,
                memberships,
                NullLogger<CompanyCrafterConsolidationService>.Instance);
            var host = (await profiles.CreateProfileAsync("Company host", CancellationToken.None)).ProfileId;
            var grant = (await profiles.CreateProfileAsync("Operator grant", CancellationToken.None)).ProfileId;
            var companyA = new CompanyId(CompanyA);

            var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            // July-era PascalCase payload: the merge must read it case-insensitively.
            await PutAsync(profiles, host, ProfileSyncCollections.TradeCrafters, OlderUnbound, JsonSerializer.Serialize(
                Crafter(OlderUnbound, CompanyA, "100", t0, world: "Sapphire", notes: "older notes")));
            await PutCrafterAsync(profiles, host, Crafter(NewerBound, CompanyA, " 100 ", t0.AddDays(30)));
            await PutCrafterAsync(profiles, host, Crafter(BoundToX, CompanyA, "200", t0));
            await PutCrafterAsync(profiles, host, Crafter(BoundToY, CompanyA, "200", t0.AddDays(1)));
            await PutCrafterAsync(profiles, host, Crafter(TieHigh, CompanyA, "300", t0));
            await PutCrafterAsync(profiles, host, Crafter(TieLow, CompanyA, "300", t0));
            await PutCrafterAsync(profiles, host, Crafter(OtherCompanyTwin, CompanyB, "100", t0.AddDays(-30)));

            await memberships.BindCrafterAsync(companyA, NewerBound, AccountX, CrafterAccountBindingEvidence.CommittedDiscordClaim, null);
            await memberships.BindCrafterAsync(companyA, BoundToX, AccountX, CrafterAccountBindingEvidence.CommittedDiscordClaim, null);
            await memberships.BindCrafterAsync(companyA, BoundToY, AccountY, CrafterAccountBindingEvidence.CommittedDiscordClaim, null);
            var invitation = await memberships.IssueInvitationAsync(companyA, AccountX, OlderUnbound);

            var order = Order(CompanyA, OlderUnbound);
            await PutAsync(profiles, host, ProfileSyncCollections.TradeOrders, order.Id, JsonSerializer.Serialize(order, WebJson));
            await PutAsync(profiles, grant, ProfileSyncCollections.TradeOrders, order.Id, JsonSerializer.Serialize(order, WebJson));
            var foreignOrder = Order(CompanyB, OlderUnbound);
            await PutAsync(profiles, host, ProfileSyncCollections.TradeOrders, foreignOrder.Id, JsonSerializer.Serialize(foreignOrder, WebJson));
            var draftId = Guid.NewGuid();
            await PutAsync(profiles, host, ProfileSyncCollections.TradePayrollDrafts, draftId, JsonSerializer.Serialize(
                new TradePayrollWorkflowDraft
                {
                    Id = draftId.ToString("D"),
                    CompanyProfileId = CompanyA,
                    AssignedCrafterId = TieHigh,
                    AssignedCrafterDisplayName = "Tie high"
                },
                WebJson));

            var dryRun = await service.RunAsync(dryRun: true, CancellationToken.None);
            Assert.Empty(dryRun.Backups);
            Assert.All(dryRun.Merges, item => Assert.Equal("planned", item.Status));
            Assert.Equal(2, dryRun.Merges.Count);
            Assert.Equal(2, dryRun.Merges.Single(item => item.SurvivorCrafterId == NewerBound).OrdersRepointed);
            Assert.False((await LoadAsync(profiles, ProfileSyncCollections.TradeCrafters, OlderUnbound, host))!.Deleted);

            var applied = await service.RunAsync(dryRun: false, CancellationToken.None);
            Assert.Equal(2, applied.Backups.Count);
            Assert.All(applied.Backups, path => Assert.True(File.Exists(path)));
            Assert.All(applied.Merges, item => Assert.Equal("merged", item.Status));
            var skipped = Assert.Single(applied.Skipped);
            Assert.Equal(CompanyCrafterConsolidationService.SkippedForConflictingAccounts, skipped.Reason);
            Assert.Equal(new[] { BoundToX, BoundToY }, skipped.CrafterIds);

            // Survivor choice: bound beats older; on a creation tie the lowest ID wins.
            Assert.True((await LoadAsync(profiles, ProfileSyncCollections.TradeCrafters, OlderUnbound, host))!.Deleted);
            Assert.True((await LoadAsync(profiles, ProfileSyncCollections.TradeCrafters, TieHigh, host))!.Deleted);
            var survivor = JsonSerializer.Deserialize<TradeCrafterProfile>(
                (await LoadAsync(profiles, ProfileSyncCollections.TradeCrafters, NewerBound, host))!.PayloadJson,
                ProfileSyncJson.CreateOptions())!;
            Assert.Equal("Sapphire", survivor.WorldName);
            Assert.Equal("older notes", survivor.OperatorNotes);
            Assert.False((await LoadAsync(profiles, ProfileSyncCollections.TradeCrafters, TieLow, host))!.Deleted);
            Assert.False((await LoadAsync(profiles, ProfileSyncCollections.TradeCrafters, BoundToY, host))!.Deleted);
            Assert.False((await LoadAsync(profiles, ProfileSyncCollections.TradeCrafters, OtherCompanyTwin, host))!.Deleted);

            // Every reference in this company follows the survivor, in every profile copy.
            foreach (var profile in new[] { host, grant })
            {
                var repointed = ReadOrder(await LoadAsync(profiles, ProfileSyncCollections.TradeOrders, order.Id, profile));
                Assert.Equal(NewerBound, repointed.AssignedCrafterId);
                Assert.All(repointed.History, item => Assert.Equal(NewerBound, item.CrafterId));
            }

            Assert.Equal(
                OlderUnbound,
                ReadOrder(await LoadAsync(profiles, ProfileSyncCollections.TradeOrders, foreignOrder.Id, host)).AssignedCrafterId);
            var draft = JsonSerializer.Deserialize<TradePayrollWorkflowDraft>(
                (await LoadAsync(profiles, ProfileSyncCollections.TradePayrollDrafts, draftId, host))!.PayloadJson,
                WebJson)!;
            Assert.Equal(TieLow, draft.AssignedCrafterId);
            Assert.Equal(
                NewerBound.ToString("D"),
                await ScalarAsync(
                    membershipOptions.DatabasePath,
                    $"SELECT legacy_crafter_id FROM company_membership_invitations WHERE invitation_id = '{invitation.InvitationId:D}';"));
            var receipts = await memberships.LoadCrafterMergeReceiptsAsync(companyA);
            Assert.Equal(new[] { OlderUnbound, TieHigh }, receipts.Select(item => item.LoserCrafterId).Order().ToArray());
            Assert.Equal(AccountX, receipts.Single(item => item.LoserCrafterId == OlderUnbound).AccountProfileId);

            var rerun = await service.RunAsync(dryRun: false, CancellationToken.None);
            Assert.Empty(rerun.Merges);
            Assert.Empty(rerun.Backups);
            Assert.Single(rerun.Skipped);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private static TradeCrafterProfile Crafter(
        Guid id,
        Guid companyId,
        string lodestone,
        DateTime createdAtUtc,
        string? world = null,
        string? notes = null) =>
        new()
        {
            Id = id,
            CompanyProfileId = companyId,
            DisplayName = $"Crafter {id.ToString("D")[^1]}",
            LodestoneCharacterId = lodestone,
            WorldName = world,
            OperatorNotes = notes,
            CreatedAtUtc = createdAtUtc,
            UpdatedAtUtc = createdAtUtc
        };

    private static TradeOrder Order(Guid companyId, Guid crafterId)
    {
        var id = Guid.NewGuid();
        return new TradeOrder
        {
            Id = id,
            CompanyProfileId = companyId,
            Title = "Merge contract order",
            Status = TradeOrderStatus.Assigned,
            AssignedCrafterId = crafterId,
            History =
            [
                new TradeOrderHistoryEvent
                {
                    CompanyProfileId = companyId,
                    OrderId = id,
                    Kind = TradeOrderHistoryEventKind.Assigned,
                    CrafterId = crafterId
                }
            ]
        };
    }

    private static TradeOrder ReadOrder(ProfileSyncObjectEnvelope? envelope) =>
        JsonSerializer.Deserialize<TradeOrder>(envelope!.PayloadJson, ProfileSyncJson.CreateOptions())!;

    private static Task PutCrafterAsync(SqliteProfileHostStore profiles, string profileId, TradeCrafterProfile crafter) =>
        PutAsync(profiles, profileId, ProfileSyncCollections.TradeCrafters, crafter.Id, JsonSerializer.Serialize(crafter, WebJson));

    private static async Task PutAsync(
        SqliteProfileHostStore profiles,
        string profileId,
        string collection,
        Guid objectId,
        string payloadJson)
    {
        var put = await profiles.PutObjectAsync(
            profileId,
            collection,
            objectId.ToString("D"),
            payloadJson,
            0,
            CancellationToken.None);
        Assert.True(put.Success, put.ErrorMessage);
    }

    private static async Task<ProfileSyncObjectEnvelope?> LoadAsync(
        SqliteProfileHostStore profiles,
        string collection,
        Guid objectId,
        string profileId) =>
        (await profiles.LoadObjectsAsync(collection, includeDeleted: true, CancellationToken.None))
        .SingleOrDefault(item =>
            item.ProfileId == profileId &&
            item.Object.ObjectId == objectId.ToString("D"))
        ?.Object;

    private static async Task<string?> ScalarAsync(string databasePath, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (await command.ExecuteScalarAsync())?.ToString();
    }
}
