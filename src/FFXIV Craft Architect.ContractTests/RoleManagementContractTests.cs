using System.Net;
using System.Net.Http.Json;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.LodestoneLookup.Services.Identity;
using FFXIV_Craft_Architect.LodestoneLookup.Services.ProfileHosting;
using FFXIV_Craft_Architect.LodestoneLookup.Services.TradeCompanies;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FFXIV_Craft_Architect.ContractTests;

/// <summary>
/// V1 role-management contract tests: owner-only generic role changes and
/// strict ownership transfer. Authorization follows strict hierarchy —
/// only an active Owner may mutate member roles, and ownership moves
/// exclusively through the transfer command.
/// </summary>
public sealed class RoleManagementContractTests
{
    [Fact]
    public async Task OwnerChangesActiveCrafterToOperator()
    {
        await using var fixture = await RoleFixture.CreateAsync();
        var (owner, target) = await fixture.SeedOwnerAndCrafterAsync();

        using var response = await fixture.PutRoleAsync(
            fixture.CompanyId,
            target.ProfileId,
            "operator",
            owner.Key);
        var membership = await fixture.Memberships.LoadAsync(fixture.CompanyIdValue, target.ProfileId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(MembershipRole.Operator, membership!.Role);
    }

    [Fact]
    public async Task OperatorAndCrafterCannotChangeRoles()
    {
        await using var fixture = await RoleFixture.CreateAsync();
        var (owner, target) = await fixture.SeedOwnerAndCrafterAsync();
        await fixture.UpdateRoleAsync(
            target.ProfileId,
            MembershipRole.Operator);

        using var operatorResponse = await fixture.PutRoleAsync(
            fixture.CompanyId,
            owner.ProfileId,
            "operator",
            target.Key);

        Assert.Equal(HttpStatusCode.Forbidden, operatorResponse.StatusCode);
    }

    [Fact]
    public async Task GenericRoleChangeCannotPromoteToOwner()
    {
        await using var fixture = await RoleFixture.CreateAsync();
        var (owner, target) = await fixture.SeedOwnerAndCrafterAsync();

        using var response = await fixture.PutRoleRawAsync(
            fixture.CompanyId,
            target.ProfileId,
            new { Role = "owner" },
            owner.Key);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(MembershipRole.Crafter, (await fixture.Memberships.LoadAsync(fixture.CompanyIdValue, target.ProfileId))!.Role);
    }

    [Fact]
    public async Task RoleChangeRejectsNonActiveTarget()
    {
        await using var fixture = await RoleFixture.CreateAsync();
        var (owner, pending) = await fixture.SeedOwnerAndPendingMemberAsync();

        using var response = await fixture.PutRoleAsync(
            fixture.CompanyId,
            pending.ProfileId,
            "operator",
            owner.Key);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task RoleChangeReplayDoesNotDuplicateEvents()
    {
        await using var fixture = await RoleFixture.CreateAsync();
        var (owner, target) = await fixture.SeedOwnerAndCrafterAsync();

        using var first = await fixture.PutRoleAsync(fixture.CompanyId, target.ProfileId, "operator", owner.Key);
        using var replay = await fixture.PutRoleAsync(fixture.CompanyId, target.ProfileId, "operator", owner.Key);
        var events = await fixture.Memberships.LoadEventsAsync(fixture.CompanyIdValue, target.ProfileId);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(3, events.Count); // request + approve + one role change
    }

    [Fact]
    public async Task TransferPromotesTargetAndDemotesOwnerAtomically()
    {
        await using var fixture = await RoleFixture.CreateAsync();
        var (owner, target) = await fixture.SeedOwnerAndCrafterAsync();

        using var response = await fixture.PostTransferAsync(fixture.CompanyId, target.ProfileId, owner.Key);
        var transferred = await fixture.Memberships.LoadAsync(fixture.CompanyIdValue, target.ProfileId);
        var formerOwner = await fixture.Memberships.LoadAsync(fixture.CompanyIdValue, owner.ProfileId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(MembershipRole.Owner, transferred!.Role);
        Assert.Equal(MembershipState.Active, transferred.State);
        Assert.Equal(MembershipRole.Operator, formerOwner!.Role);
        Assert.Single(await fixture.ActiveOwnersAsync());
    }

    [Fact]
    public async Task TransferRefusesMissingPendingRevokedOrSelfTargets()
    {
        await using var fixture = await RoleFixture.CreateAsync();
        var (owner, pending) = await fixture.SeedOwnerAndPendingMemberAsync();

        using var selfTransfer = await fixture.PostTransferAsync(fixture.CompanyId, owner.ProfileId, owner.Key);
        using var pendingTransfer = await fixture.PostTransferAsync(fixture.CompanyId, pending.ProfileId, owner.Key);
        using var missingTransfer = await fixture.PostTransferAsync(fixture.CompanyId, Guid.NewGuid(), owner.Key);

        Assert.Equal(HttpStatusCode.Conflict, selfTransfer.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, pendingTransfer.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missingTransfer.StatusCode);
        Assert.Equal(
            owner.ProfileId,
            (await fixture.Memberships.LoadAsync(fixture.CompanyIdValue, owner.ProfileId))!.AccountProfileId);
    }

    [Fact]
    public async Task TransferRetryIsIdempotent()
    {
        await using var fixture = await RoleFixture.CreateAsync();
        var (owner, target) = await fixture.SeedOwnerAndCrafterAsync();

        using var first = await fixture.PostTransferAsync(fixture.CompanyId, target.ProfileId, owner.Key);
        using var replay = await fixture.PostTransferAsync(fixture.CompanyId, target.ProfileId, owner.Key);
        var events = await fixture.Memberships.LoadEventsAsync(fixture.CompanyIdValue, target.ProfileId);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(3, events.Count); // request + approve + transfer
    }

    [Fact]
    public async Task OperatorCannotTransferOwnership()
    {
        await using var fixture = await RoleFixture.CreateAsync();
        var (owner, target) = await fixture.SeedOwnerAndCrafterAsync();
        await fixture.UpdateRoleAsync(
            target.ProfileId,
            MembershipRole.Operator);

        using var response = await fixture.PostTransferAsync(fixture.CompanyId, owner.ProfileId, target.Key);
        using var replay = await fixture.PostTransferAsync(fixture.CompanyId, owner.ProfileId, target.Key);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, replay.StatusCode);
        Assert.Equal(MembershipRole.Owner, (await fixture.Memberships.LoadAsync(fixture.CompanyIdValue, owner.ProfileId))!.Role);
    }

    private sealed class RoleFixture : IAsyncDisposable
    {
        private readonly string root;
        private readonly WebApplicationFactory<Program> application;

        private RoleFixture(string root, WebApplicationFactory<Program> application)
        {
            this.root = root;
            this.application = application;
            Memberships = application.Services.GetRequiredService<SqliteMembershipStore>();
        }

        public SqliteMembershipStore Memberships { get; }
        public string CompanyId { get; } = Guid.NewGuid().ToString("D");
        public CompanyId CompanyIdValue => new(Guid.Parse(CompanyId));

        public static Task<RoleFixture> CreateAsync() =>
            Task.FromResult(new RoleFixture(
                Path.Combine(Path.GetTempPath(), $"craft-roles-{Guid.NewGuid():N}"),
                CreateApplication()));

        public async Task<(Account Account, Account Target)> SeedOwnerAndCrafterAsync()
        {
            var owner = await CreateAccountAsync("Owner");
            var target = await CreateAccountAsync("Invited crafter");
            await PutCompanyAsync(owner.Key);
            using (var requested = await target.Client.PostAsJsonAsync(
                       $"/trade/v1/companies/{CompanyId}/membership-requests",
                       new MembershipRequestBody(null)))
            {
                requested.EnsureSuccessStatusCode();
            }
            using (var approved = await owner.Client.PostAsync(
                       $"/trade/v1/companies/{CompanyId}/memberships/{target.ProfileId:D}/approve",
                       null))
            {
                approved.EnsureSuccessStatusCode();
            }
            return (owner, target);
        }

        public async Task<(Account Owner, Account Pending)> SeedOwnerAndPendingMemberAsync()
        {
            var owner = await CreateAccountAsync("Owner");
            var pending = await CreateAccountAsync("Pending crafter");
            await PutCompanyAsync(owner.Key);
            using (var requested = await pending.Client.PostAsJsonAsync(
                       $"/trade/v1/companies/{CompanyId}/membership-requests",
                       new MembershipRequestBody(null)))
            {
                requested.EnsureSuccessStatusCode();
            }
            return (owner, pending);
        }

        public Task<HttpResponseMessage> PutRoleAsync(
            string companyId,
            Guid accountProfileId,
            string role,
            string key) =>
            PutRoleRawAsync(companyId, accountProfileId, new { Role = role }, key);

        public async Task<HttpResponseMessage> PutRoleRawAsync(
            string companyId,
            Guid accountProfileId,
            object body,
            string key)
        {
            using var client = CreateClient(key);
            return await client.PutAsJsonAsync(
                $"/trade/v1/companies/{companyId}/memberships/{accountProfileId:D}/role",
                body);
        }

        public async Task<HttpResponseMessage> PostTransferAsync(
            string companyId,
            Guid targetProfileId,
            string key)
        {
            using var client = CreateClient(key);
            return await client.PostAsJsonAsync(
                $"/trade/v1/companies/{companyId}/ownership-transfer",
                new { TargetAccountProfileId = targetProfileId });
        }

        public HttpClient CreateClient(string key)
        {
            var client = application.CreateClient();
            client.DefaultRequestHeaders.Add("X-Profile-Key", key);
            return client;
        }

        private async Task PutCompanyAsync(string ownerKey)
        {
            var company = TradeCompanyProfile.CreateLocal("Sapphire Avenue", DateTime.UtcNow);
            company.Id = Guid.Parse(CompanyId);
            using var client = CreateClient(ownerKey);
            using var response = await client.PutAsJsonAsync(
                $"/profile-host/objects/{ProfileSyncCollections.TradeCompanyProfiles}/{CompanyId}",
                new ProfileSyncPutRequest
                {
                    PayloadJson = System.Text.Json.JsonSerializer.Serialize(company, ProfileSyncJson.CreateOptions()),
                    ExpectedRevision = 0
                });
            response.EnsureSuccessStatusCode();
        }

        public async Task<Account> CreateAccountAsync(string displayName)
        {
            var hasher = new ProfileAccessKeyHasher();
            var key = hasher.CreateAccessKey();
            var profiles = application.Services.GetRequiredService<SqliteProfileHostStore>();
            var profile = await profiles.CreateProfileAsync(displayName, CancellationToken.None);
            await profiles.AddAccessKeyAsync(profile.ProfileId, key.StoredHash, CancellationToken.None);
            var identities = application.Services.GetRequiredService<SqliteDiscordIdentityStore>();
            var linked = await identities.LinkAsync(
                Guid.Parse(profile.ProfileId),
                $"{100000000000000000L + Math.Abs((long)Guid.Parse(profile.ProfileId).GetHashCode()):D18}",
                displayName,
                DateTimeOffset.UtcNow);
            Assert.Equal(DiscordIdentityLinkResultStatus.Linked, linked.Status);
            var client = application.CreateClient();
            client.DefaultRequestHeaders.Add("X-Profile-Key", key.PlaintextKey);
            return new Account(Guid.Parse(profile.ProfileId), key.PlaintextKey, client);
        }

        public async Task UpdateRoleAsync(Guid accountProfileId, MembershipRole role) =>
            await Memberships.UpdateRoleInternalForTestsAsync(CompanyIdValue, accountProfileId, role);

        public async Task<IReadOnlyList<string>> ActiveOwnersAsync()
        {
            var owners = await Memberships.LoadActiveAsync(CompanyIdValue);
            return owners
                .Where(member => member.Role == MembershipRole.Owner)
                .Select(member => member.AccountProfileId.ToString("D"))
                .ToArray();
        }

        private static WebApplicationFactory<Program> CreateApplication() =>
            new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
                builder.ConfigureAppConfiguration((_, configuration) =>
                    configuration.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ProfileHost:Enabled"] = "true",
                        ["TradeMemberships:DatabasePath"] =
                            Path.Combine(Path.GetTempPath(), $"craft-roles-{Guid.NewGuid():N}", "memberships.db"),
                        // Keep founder reconciliation from racing role mutations mid-test.
                        ["TradeMemberships:FounderReconciliationIntervalSeconds"] = "3600",
                        ["Discord:Enabled"] = "false"
                    })));

        public ValueTask DisposeAsync()
        {
            application.Dispose();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
            return ValueTask.CompletedTask;
        }
    }

    private sealed record Account(Guid ProfileId, string Key, HttpClient Client);
}
