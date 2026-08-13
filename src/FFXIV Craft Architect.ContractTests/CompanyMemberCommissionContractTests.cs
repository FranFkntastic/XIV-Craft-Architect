using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.LodestoneLookup.Services.Discord;
using FFXIV_Craft_Architect.LodestoneLookup.Services.Identity;
using FFXIV_Craft_Architect.LodestoneLookup.Services.ProfileHosting;
using FFXIV_Craft_Architect.LodestoneLookup.Services.TradeCompanies;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FFXIV_Craft_Architect.ContractTests;

public sealed class CompanyMemberCommissionContractTests
{
    [Fact]
    public async Task ActiveMemberClaimsOpenCommissionAndCapturesLinkedDiscordContact()
    {
        await using var fixture = await MemberCommissionFixture.CreateAsync();
        var member = await fixture.AddActiveMemberAsync("Crafter");

        using var response = await fixture.ClaimAsync(member);
        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            await response.Content.ReadAsStringAsync());
        var projection = await response.Content.ReadFromJsonAsync<CompanyCommissionParticipantBrief>();
        var claimId = await fixture.LoadActiveClaimIdAsync();
        var claimActorDisplayName = await fixture.LoadClaimActorDisplayNameAsync(claimId);
        var notificationActorDisplayName = await fixture
            .ResolveClaimNotificationActorDisplayNameAsync(claimId);
        var captured = await fixture.Notifications.HasCommittedClaimContactAsync(
            new CompanyId(fixture.Company.Id),
            fixture.Order.Id,
            claimId,
            member.DiscordUserId,
            CancellationToken.None);

        Assert.Equal(fixture.Order.Id, projection!.Public.CommissionId);
        Assert.True(projection.Public.IsClaimed);
        Assert.True(captured);
        Assert.Null(claimActorDisplayName);
        Assert.Equal("Crafter", notificationActorDisplayName);
    }

    [Fact]
    public async Task ConcurrentMemberClaimsCommitExactlyOneClaimant()
    {
        await using var fixture = await MemberCommissionFixture.CreateAsync();
        var first = await fixture.AddActiveMemberAsync("First crafter");
        var second = await fixture.AddActiveMemberAsync("Second crafter");
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstClaim = ClaimAfterBarrierAsync(first);
        var secondClaim = ClaimAfterBarrierAsync(second);
        barrier.SetResult();

        using var firstResponse = await firstClaim;
        using var secondResponse = await secondClaim;
        var responses = new[] { firstResponse, secondResponse };
        var accepted = Assert.Single(
            responses,
            item => item.StatusCode == HttpStatusCode.OK);
        var refused = Assert.Single(
            responses,
            item => item.StatusCode == HttpStatusCode.Conflict);
        var error = await refused.Content.ReadFromJsonAsync<MembershipErrorResponse>();
        var persistedCrafterId = await fixture.LoadActiveCrafterIdAsync();

        Assert.NotNull(accepted);
        Assert.Equal("claim_slot_taken", error!.Error);
        Assert.Contains(persistedCrafterId, new[] { first.ProfileId, second.ProfileId });

        async Task<HttpResponseMessage> ClaimAfterBarrierAsync(Account account)
        {
            await barrier.Task;
            return await fixture.ClaimAsync(account);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NonMemberAndPendingMemberCannotClaim(bool pending)
    {
        await using var fixture = await MemberCommissionFixture.CreateAsync();
        var account = await fixture.CreateAccountAsync(pending ? "Pending" : "Outsider");
        if (pending)
        {
            await fixture.RequestMembershipAsync(account);
        }

        using var response = await fixture.ClaimAsync(account);
        var error = await response.Content.ReadFromJsonAsync<MembershipErrorResponse>();

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(
            pending ? "membership_inactive" : "active_membership_required",
            error!.Error);
    }

    [Fact]
    public async Task MemberReportsProgressUsingAccountAuthority()
    {
        await using var fixture = await MemberCommissionFixture.CreateAsync();
        var member = await fixture.AddActiveMemberAsync("Crafter");
        using var claimed = await fixture.ClaimAsync(member);
        var claimProjection = (await claimed.Content.ReadFromJsonAsync<CompanyCommissionParticipantBrief>())!;

        using var response = await fixture.ReportProgressAsync(
            member,
            claimProjection.Public.ProjectionRevision);
        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            await response.Content.ReadAsStringAsync());
        var projection = await response.Content.ReadFromJsonAsync<CompanyCommissionParticipantBrief>();

        Assert.Equal(1, projection!.Public.OutputProgress.Single().CompletedQuantity);
        Assert.Equal(
            CompanyCommissionActivityKind.ProgressReported,
            projection.Activity.Last().Kind);
    }

    [Fact]
    public async Task OtherMemberAndCompanyOwnerCannotUseClaimantsParticipantAuthority()
    {
        await using var fixture = await MemberCommissionFixture.CreateAsync();
        var claimant = await fixture.AddActiveMemberAsync("Claimant");
        var other = await fixture.AddActiveMemberAsync("Other member");
        using var claimed = await fixture.ClaimAsync(claimant);
        var projection = (await claimed.Content.ReadFromJsonAsync<CompanyCommissionParticipantBrief>())!;

        using var otherResponse = await fixture.ReportProgressAsync(
            other,
            projection.Public.ProjectionRevision);
        using var ownerResponse = await fixture.ReportProgressAsync(
            fixture.Owner,
            projection.Public.ProjectionRevision);

        Assert.Equal(HttpStatusCode.Forbidden, otherResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, ownerResponse.StatusCode);
    }

    [Fact]
    public async Task ClaimantCannotUseMembershipAuthorityAfterCommissionCancellation()
    {
        await using var fixture = await MemberCommissionFixture.CreateAsync();
        var member = await fixture.AddActiveMemberAsync("Crafter");
        using var claimed = await fixture.ClaimAsync(member);
        var projection = (await claimed.Content.ReadFromJsonAsync<CompanyCommissionParticipantBrief>())!;
        using var cancelled = await fixture.CancelCommissionAsync();
        cancelled.EnsureSuccessStatusCode();

        using var response = await fixture.ReportProgressAsync(
            member,
            projection.Public.ProjectionRevision + 1);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ParticipantCapabilityFlowStillReportsProgress()
    {
        await using var fixture = await MemberCommissionFixture.CreateAsync();
        var member = await fixture.AddActiveMemberAsync("Crafter");
        using var claimed = await fixture.ClaimAsync(member);
        var claimProjection = (await claimed.Content.ReadFromJsonAsync<CompanyCommissionParticipantBrief>())!;
        var capability = await fixture.IssueParticipantCapabilityAsync();

        using var response = await fixture.ReportProgressWithCapabilityAsync(
            capability,
            claimProjection.Public.ProjectionRevision);
        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            await response.Content.ReadAsStringAsync());
        var projection = await response.Content.ReadFromJsonAsync<CompanyCommissionParticipantBrief>();

        Assert.Equal(1, projection!.Public.OutputProgress.Single().CompletedQuantity);
    }

    [Fact]
    public async Task RevokedParticipantCapabilityRetainsUnauthorizedContract()
    {
        await using var fixture = await MemberCommissionFixture.CreateAsync();
        var member = await fixture.AddActiveMemberAsync("Crafter");
        using var claimed = await fixture.ClaimAsync(member);
        var projection = (await claimed.Content.ReadFromJsonAsync<CompanyCommissionParticipantBrief>())!;
        var capability = await fixture.IssueParticipantCapabilityAsync();
        await fixture.RevokeParticipantCapabilitiesAsync();

        using var response = await fixture.ReportProgressWithCapabilityAsync(
            capability,
            projection.Public.ProjectionRevision);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task StaleParticipantCapabilityCommandRetainsProjectionConflictContract()
    {
        await using var fixture = await MemberCommissionFixture.CreateAsync();
        var member = await fixture.AddActiveMemberAsync("Crafter");
        using var claimed = await fixture.ClaimAsync(member);
        var projection = (await claimed.Content.ReadFromJsonAsync<CompanyCommissionParticipantBrief>())!;
        var capability = await fixture.IssueParticipantCapabilityAsync();

        using var response = await fixture.ReportProgressWithCapabilityAsync(
            capability,
            projection.Public.ProjectionRevision - 1);
        using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("projection_conflict", error.RootElement.GetProperty("error").GetString());
        Assert.Equal(
            "The canonical commission changed before the public command was applied.",
            error.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task OwnerMembershipCanClaimCommission()
    {
        await using var fixture = await MemberCommissionFixture.CreateAsync();

        using var response = await fixture.ClaimAsync(fixture.Owner);
        var projection = await response.Content.ReadFromJsonAsync<CompanyCommissionParticipantBrief>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(projection!.Public.IsClaimed);
    }

    [Theory]
    [InlineData(MembershipRole.Owner)]
    [InlineData(MembershipRole.Operator)]
    public async Task AuthorizedMembershipAccountCanLoadAndCommandOwnerCommission(
        MembershipRole role)
    {
        await using var fixture = await MemberCommissionFixture.CreateAsync();
        var account = await fixture.AddActiveMemberAsync(role.ToString());
        await fixture.SetRoleAsync(account, role);

        using var loaded = await fixture.LoadOwnerCommissionAsync(account);
        Assert.True(
            loaded.IsSuccessStatusCode,
            await loaded.Content.ReadAsStringAsync());
        var projection = await loaded.Content
            .ReadFromJsonAsync<CompanyCommissionOwnerProjection>();
        Assert.Equal(fixture.Order.Id, projection!.Order.Id);

        using var cancelled = await fixture.CancelCommissionAsync(account);
        Assert.True(
            cancelled.IsSuccessStatusCode,
            await cancelled.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task CrafterMembershipCannotLoadOwnerCommission()
    {
        await using var fixture = await MemberCommissionFixture.CreateAsync();
        var crafter = await fixture.AddActiveMemberAsync("Crafter");

        using var response = await fixture.LoadOwnerCommissionAsync(crafter);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task CommissionerDiscordRecipientCanOperateFromCrafterAccountContext()
    {
        await using var fixture = await MemberCommissionFixture.CreateAsync();
        var recipient = await fixture.AddActiveMemberAsync("Discord commissioner");
        await fixture.ConfigureCommissionerRouteAsync(recipient);

        using var response = await fixture.LoadOwnerCommissionAsync(recipient);
        using var memberships = await fixture.LoadMembershipsAsync(recipient);
        using var hub = await fixture.LoadCompanyHubAsync(recipient);

        Assert.True(
            response.IsSuccessStatusCode,
            await response.Content.ReadAsStringAsync());
        memberships.EnsureSuccessStatusCode();
        var membershipRows = await memberships.Content.ReadFromJsonAsync<MembershipResponse[]>();
        Assert.Contains(
            membershipRows!,
            item => item.CompanyId == fixture.Company.Id.ToString("D") &&
                item.Role == "operator" &&
                item.State == "active");
        hub.EnsureSuccessStatusCode();
        using var hubJson = JsonDocument.Parse(await hub.Content.ReadAsStringAsync());
        Assert.Equal(
            "operator",
            hubJson.RootElement.GetProperty("standing").GetProperty("role").GetString());
    }

    [Fact]
    public async Task CommissionerDiscordRecipientDoesNotRequireACompanyMembership()
    {
        await using var fixture = await MemberCommissionFixture.CreateAsync();
        var recipient = await fixture.CreateAccountAsync("Discord commissioner");
        await fixture.ConfigureCommissionerRouteAsync(recipient);

        using var response = await fixture.LoadOwnerCommissionAsync(recipient);
        using var hub = await fixture.LoadCompanyHubAsync(recipient);

        Assert.True(
            response.IsSuccessStatusCode,
            await response.Content.ReadAsStringAsync());
        hub.EnsureSuccessStatusCode();
        using var hubJson = JsonDocument.Parse(await hub.Content.ReadAsStringAsync());
        Assert.Equal(
            "operator",
            hubJson.RootElement.GetProperty("standing").GetProperty("role").GetString());
    }

    [Fact]
    public async Task CommissionerDiscordRecipientFailsClosedWhenCompanyIdentityIsDuplicated()
    {
        await using var fixture = await MemberCommissionFixture.CreateAsync();
        var recipient = await fixture.CreateAccountAsync("Discord commissioner");
        await fixture.ConfigureCommissionerRouteAsync(recipient);
        await fixture.DuplicateCompanyIdentityAsync();

        using var response = await fixture.LoadOwnerCommissionAsync(recipient);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task CommissionerDiscordRecipientLosesOperatorAccessWhenRouteMoves()
    {
        await using var fixture = await MemberCommissionFixture.CreateAsync();
        var recipient = await fixture.CreateAccountAsync("Former Discord commissioner");
        var replacement = await fixture.CreateAccountAsync("Current Discord commissioner");
        await fixture.ConfigureCommissionerRouteAsync(recipient);

        using var authorized = await fixture.LoadOwnerCommissionAsync(recipient);
        Assert.True(
            authorized.IsSuccessStatusCode,
            await authorized.Content.ReadAsStringAsync());

        await fixture.ConfigureCommissionerRouteAsync(replacement, expectedRevision: 1);
        using var denied = await fixture.LoadOwnerCommissionAsync(recipient);

        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        Assert.Empty(await denied.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task HostingProfileOwnerRetainsOwnerCommissionAuthority()
    {
        await using var fixture = await MemberCommissionFixture.CreateAsync();

        using var response = await fixture.LoadOwnerCommissionAsync(fixture.Owner);

        Assert.True(
            response.IsSuccessStatusCode,
            await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task HostingProfileOwnerRetainsAuthorityWhenCompanyIdentityIsDuplicated()
    {
        await using var fixture = await MemberCommissionFixture.CreateAsync();
        await fixture.DuplicateCompanyIdentityAsync();

        using var response = await fixture.LoadOwnerCommissionAsync(fixture.Owner);

        Assert.True(
            response.IsSuccessStatusCode,
            await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task MembershipAccountFailsClosedWhenCompanyIdentityIsDuplicated()
    {
        await using var fixture = await MemberCommissionFixture.CreateAsync();
        var account = await fixture.AddActiveMemberAsync("Operator");
        await fixture.SetRoleAsync(account, MembershipRole.Operator);
        await fixture.DuplicateCompanyIdentityAsync();

        using var response = await fixture.LoadOwnerCommissionAsync(account);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task RevokedMemberCannotClaimAfterPreviouslyBeingActive()
    {
        await using var fixture = await MemberCommissionFixture.CreateAsync();
        var member = await fixture.AddActiveMemberAsync("Former crafter");
        using var ownerClient = fixture.CreateClient(fixture.Owner.Key);
        using var revoked = await ownerClient.PostAsync(
            $"/trade/v1/companies/{fixture.Company.Id:D}/memberships/{member.ProfileId:D}/revoke",
            null);
        revoked.EnsureSuccessStatusCode();

        using var response = await fixture.ClaimAsync(member);
        var error = await response.Content.ReadFromJsonAsync<MembershipErrorResponse>();

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("membership_inactive", error!.Error);
    }

    private sealed class MemberCommissionFixture : IAsyncDisposable
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
        private readonly string root;
        private readonly WebApplicationFactory<Program> application;

        private MemberCommissionFixture(string root, WebApplicationFactory<Program> application)
        {
            this.root = root;
            this.application = application;
            Profiles = application.Services.GetRequiredService<SqliteProfileHostStore>();
            Identities = application.Services.GetRequiredService<SqliteDiscordIdentityStore>();
            Memberships = application.Services.GetRequiredService<SqliteMembershipStore>();
            Companies = application.Services.GetRequiredService<ProfileHostedTradeCompanyService>();
            Commissions = application.Services.GetRequiredService<HostedCompanyCommissionService>();
            Capabilities = application.Services.GetRequiredService<SqliteCompanyCommissionCapabilityStore>();
            Notifications = application.Services.GetRequiredService<SqliteDiscordNotificationStore>();
        }

        public SqliteProfileHostStore Profiles { get; }
        public SqliteDiscordIdentityStore Identities { get; }
        public SqliteMembershipStore Memberships { get; }
        public ProfileHostedTradeCompanyService Companies { get; }
        public HostedCompanyCommissionService Commissions { get; }
        public SqliteCompanyCommissionCapabilityStore Capabilities { get; }
        public SqliteDiscordNotificationStore Notifications { get; }
        public Account Owner { get; private set; } = null!;
        public TradeCompanyProfile Company { get; private set; } = null!;
        public TradeOrder Order { get; private set; } = null!;

        public static async Task<MemberCommissionFixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), $"craft-member-actions-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var application = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
                builder.ConfigureAppConfiguration((_, configuration) =>
                    configuration.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ProfileHost:Enabled"] = "true",
                        ["ProfileHost:DatabasePath"] = Path.Combine(root, "profiles.db"),
                        ["ProfileHost:ArchiveBackupDirectory"] = Path.Combine(root, "archive"),
                        ["TradeMemberships:DatabasePath"] = Path.Combine(root, "memberships.db"),
                        ["CommissionBriefs:Enabled"] = "true",
                        ["CommissionBriefs:DatabasePath"] = Path.Combine(root, "briefs.db"),
                        ["CommissionBriefs:PublicBaseUrl"] = "https://example.test/commissions/",
                        ["Discord:Notifications:DatabasePath"] = Path.Combine(root, "notifications.db")
                    })));
            var fixture = new MemberCommissionFixture(root, application);
            await fixture.InitializeAsync();
            return fixture;
        }

        public HttpClient CreateClient(string key)
        {
            var client = application.CreateClient();
            client.DefaultRequestHeaders.Add("X-Profile-Key", key);
            return client;
        }

        public async Task<Account> CreateAccountAsync(string displayName)
        {
            var hasher = new ProfileAccessKeyHasher();
            var key = hasher.CreateAccessKey();
            var profile = await Profiles.CreateProfileAsync(displayName, CancellationToken.None);
            await Profiles.AddAccessKeyAsync(profile.ProfileId, key.StoredHash, CancellationToken.None);
            var profileId = Guid.Parse(profile.ProfileId);
            var discordUserId = $"{100000000000000000L + Math.Abs((long)profileId.GetHashCode()):D18}";
            var linked = await Identities.LinkAsync(
                profileId,
                discordUserId,
                displayName,
                DateTimeOffset.UtcNow);
            Assert.Equal(DiscordIdentityLinkResultStatus.Linked, linked.Status);
            return new Account(profileId, key.PlaintextKey, discordUserId);
        }

        public async Task<Account> AddActiveMemberAsync(string displayName)
        {
            var account = await CreateAccountAsync(displayName);
            await RequestMembershipAsync(account);
            using var ownerClient = CreateClient(Owner.Key);
            using var approved = await ownerClient.PostAsync(
                $"/trade/v1/companies/{Company.Id:D}/memberships/{account.ProfileId:D}/approve",
                null);
            approved.EnsureSuccessStatusCode();
            return account;
        }

        public async Task RequestMembershipAsync(Account account)
        {
            using var client = CreateClient(account.Key);
            using var requested = await client.PostAsJsonAsync(
                $"/trade/v1/companies/{Company.Id:D}/membership-requests",
                new MembershipRequestBody(null));
            requested.EnsureSuccessStatusCode();
        }

        public Task<HttpResponseMessage> ClaimAsync(Account account)
        {
            var client = CreateClient(account.Key);
            return client.PostAsync(
                $"/trade/v1/companies/{Company.Id:D}/commissions/{Order.Id:D}/claim",
                null);
        }

        public Task<HttpResponseMessage> ReportProgressAsync(Account account, long projectionRevision) =>
            PostProgressAsync(CreateClient(account.Key), null, projectionRevision);

        public Task<HttpResponseMessage> ReportProgressWithCapabilityAsync(
            string capability,
            long projectionRevision) =>
            PostProgressAsync(application.CreateClient(), capability, projectionRevision);

        public async Task<string> IssueParticipantCapabilityAsync()
        {
            var access = await Companies.ResolveMembershipAccessAsync(
                Owner.ProfileId,
                new CompanyId(Company.Id),
                CancellationToken.None);
            var record = await Companies.LoadRecordAsync(
                access!,
                TradeCompanyRecordKinds.Order,
                Order.Id.ToString("D"),
                CancellationToken.None);
            var order = JsonSerializer.Deserialize<TradeOrder>(record!.PayloadJson, JsonOptions)!;
            var participant = order.CompanyCommission!.ParticipantGrant!;
            var issued = await Capabilities.IssueAsync(
                new CompanyId(Company.Id),
                Order.Id,
                order.CompanyCommission.PublicMetadata.PublicBriefId,
                CompanyCommissionCapabilityKind.Participant,
                participant.GrantId,
                participant.CapabilityRevision,
                DateTime.UtcNow,
                CancellationToken.None);
            return issued.PlaintextToken;
        }

        public async Task<Guid> LoadActiveClaimIdAsync()
        {
            var order = await LoadCanonicalOrderAsync();
            return order.CompanyCommission!.ActiveClaim!.ClaimId;
        }

        public async Task<string?> LoadClaimActorDisplayNameAsync(Guid claimId)
        {
            var order = await LoadCanonicalOrderAsync();
            return order.CompanyCommission!.Activity
                .Single(item => item.CommandId == claimId)
                .Actor.DisplayName;
        }

        public async Task<string?> ResolveClaimNotificationActorDisplayNameAsync(Guid claimId)
        {
            var order = await LoadCanonicalOrderAsync();
            var commission = order.CompanyCommission!;
            var activity = commission.Activity.Single(item => item.CommandId == claimId);
            return await DiscordCompanyCommissionPostCommitSink.ResolveActorDisplayNameAsync(
                commission,
                activity,
                Notifications,
                Identities,
                Profiles,
                TimeProvider.System);
        }

        public async Task<Guid> LoadActiveCrafterIdAsync()
        {
            var order = await LoadCanonicalOrderAsync();
            return order.CompanyCommission!.ActiveClaim!.CrafterId!.Value;
        }

        public Task<HttpResponseMessage> LoadOwnerCommissionAsync(Account account)
        {
            var client = CreateClient(account.Key);
            return client.GetAsync(
                $"/trade/v1/companies/{Company.Id:D}/commissions/{Order.Id:D}/owner");
        }

        public Task<HttpResponseMessage> LoadMembershipsAsync(Account account)
        {
            var client = CreateClient(account.Key);
            return client.GetAsync("/trade/v1/memberships");
        }

        public Task<HttpResponseMessage> LoadCompanyHubAsync(Account account)
        {
            var client = CreateClient(account.Key);
            return client.GetAsync($"/trade/v1/companies/{Company.Id:D}/hub");
        }

        public async Task ConfigureCommissionerRouteAsync(
            Account account,
            long expectedRevision = 0)
        {
            var route = await Notifications.PutRouteAsync(
                new CompanyId(Company.Id),
                new DiscordNotificationRouteUpdate(
                    account.DiscordUserId,
                    DiscordNotificationDestinationMode.CommissionerDirectMessage,
                    null,
                    DiscordDirectMessageFallback.None,
                    DiscordNotificationMentionBehavior.NoPing,
                    DiscordNotificationMentionBehavior.Push,
                    DiscordNotificationMentionBehavior.Push,
                    expectedRevision,
                    $"commissioner-route-{Guid.NewGuid():N}"),
                DateTimeOffset.UtcNow);
            Assert.True(route.Success, route.Error);
        }

        public async Task DuplicateCompanyIdentityAsync()
        {
            var duplicate = await CreateAccountAsync("Duplicate host");
            await using var connection = new SqliteConnection(
                $"Data Source={Path.Combine(root, "profiles.db")}");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO sync_objects (
                    profile_id,
                    collection,
                    object_id,
                    payload_json,
                    revision,
                    updated_at_utc,
                    deleted,
                    deleted_at_utc)
                VALUES ($profileId, $collection, $objectId, $payload, 1, $updatedAt, 0, NULL);
                """;
            command.Parameters.AddWithValue("$profileId", duplicate.ProfileId.ToString("D"));
            command.Parameters.AddWithValue(
                "$collection",
                ProfileSyncCollections.TradeCompanyProfiles);
            command.Parameters.AddWithValue("$objectId", Company.Id.ToString("D"));
            command.Parameters.AddWithValue(
                "$payload",
                JsonSerializer.Serialize(Company, ProfileSyncJson.CreateOptions()));
            command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        public async Task SetRoleAsync(Account account, MembershipRole role)
        {
            await using var connection = new SqliteConnection(
                $"Data Source={Path.Combine(root, "memberships.db")}");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE company_memberships
                SET role = $role
                WHERE company_id = $companyId AND account_profile_id = $profileId;
                """;
            command.Parameters.AddWithValue(
                "$role",
                role.ToString().ToLowerInvariant());
            command.Parameters.AddWithValue("$companyId", Company.Id.ToString("D"));
            command.Parameters.AddWithValue("$profileId", account.ProfileId.ToString("D"));
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        public Task<HttpResponseMessage> CancelCommissionAsync() =>
            CancelCommissionAsync(Owner);

        public async Task<HttpResponseMessage> CancelCommissionAsync(Account account)
        {
            var access = await Companies.ResolveMembershipAccessAsync(
                account.ProfileId,
                new CompanyId(Company.Id),
                CancellationToken.None);
            var snapshot = await Commissions.LoadOwnerAsync(
                access!,
                Order.Id,
                CancellationToken.None);
            var command = new CancelCompanyCommissionCommand(
                new CompanyCommissionCommandContext(
                    new CompanyId(Company.Id),
                    Order.Id,
                    snapshot!.Envelope.RecordRevision,
                    snapshot.CompanyRevision,
                    Guid.NewGuid(),
                    CompanyCommissionProtocol.Version1),
                "Cancelled by contract fixture");
            var client = CreateClient(account.Key);
            return await client.PostAsJsonAsync(
                $"/trade/v1/companies/{Company.Id:D}/commissions/{Order.Id:D}/commands/cancel",
                command);
        }

        public Task RevokeParticipantCapabilitiesAsync() =>
            Capabilities.RevokeAllAsync(
                new CompanyId(Company.Id),
                Order.Id,
                CompanyCommissionCapabilityKind.Participant,
                DateTime.UtcNow,
                CancellationToken.None);

        private async Task<TradeOrder> LoadCanonicalOrderAsync()
        {
            var access = await Companies.ResolveMembershipAccessAsync(
                Owner.ProfileId,
                new CompanyId(Company.Id),
                CancellationToken.None);
            var record = await Companies.LoadRecordAsync(
                access!,
                TradeCompanyRecordKinds.Order,
                Order.Id.ToString("D"),
                CancellationToken.None);
            return JsonSerializer.Deserialize<TradeOrder>(record!.PayloadJson, JsonOptions)!;
        }

        public async ValueTask DisposeAsync()
        {
            await application.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }

        private async Task InitializeAsync()
        {
            Owner = await CreateAccountAsync("Owner");
            Company = TradeCompanyProfile.CreateLocal("Member Actions", DateTime.UtcNow);
            using var ownerClient = CreateClient(Owner.Key);
            using var companyPut = await ownerClient.PutAsJsonAsync(
                $"/profile-host/objects/{ProfileSyncCollections.TradeCompanyProfiles}/{Company.Id:D}",
                new ProfileSyncPutRequest
                {
                    PayloadJson = JsonSerializer.Serialize(Company, ProfileSyncJson.CreateOptions()),
                    ExpectedRevision = 0
                });
            companyPut.EnsureSuccessStatusCode();

            Order = CreateOrder(Company.Id);
            using var orderPut = await ownerClient.PutAsJsonAsync(
                $"/profile-host/objects/{ProfileSyncCollections.TradeOrders}/{Order.Id:D}",
                new ProfileSyncPutRequest
                {
                    PayloadJson = JsonSerializer.Serialize(Order, ProfileSyncJson.CreateOptions()),
                    ExpectedRevision = 0
                });
            orderPut.EnsureSuccessStatusCode();
            var access = await Companies.ResolveMembershipAccessAsync(
                Owner.ProfileId,
                new CompanyId(Company.Id),
                CancellationToken.None);
            var ownership = Order.CommissionPublication!.Ownership!;
            var publication = await Companies.PutRecordAsync(
                access!,
                TradeCompanyRecordKinds.Publication,
                ownership.OrderId == Order.Id ? Order.CommissionPublication.PublicId : string.Empty,
                JsonSerializer.Serialize(ownership),
                CompanyRecordRevision.None,
                "member-actions-publication",
                CancellationToken.None);
            Assert.True(publication.Success);
        }

        private Task<HttpResponseMessage> PostProgressAsync(
            HttpClient client,
            string? capability,
            long projectionRevision)
        {
            var output = Order.CompanyCommission!.CurrentTerms.Outputs.Single();
            return client.PostAsJsonAsync(
                $"/xivdata/commission-briefs/{Order.CommissionPublication!.PublicId}/commands/report-progress",
                new PublicCompanyCommissionCommandEnvelope
                {
                    ProtocolVersion = CompanyCommissionProtocol.Version1,
                    PublicBriefId = Order.CommissionPublication.PublicId,
                    ExpectedProjectionRevision = projectionRevision,
                    CommandId = Guid.NewGuid(),
                    ParticipantCapability = capability,
                    Command = JsonSerializer.SerializeToElement(new
                    {
                        outputs = new[]
                        {
                            new CompanyCommissionProgressQuantity(output.LineId, output.ItemId, 1, 0)
                        },
                        comment = "Started"
                    })
                });
        }

        private static TradeOrder CreateOrder(Guid companyId)
        {
            var now = DateTime.UtcNow;
            var orderId = Guid.NewGuid();
            var publicId = $"member-{orderId:N}";
            var ownership = new TradeCompanyPublicationOwnership(
                new CompanyId(companyId),
                orderId,
                new CompanyRecordRevision(1));
            var terms = new CompanyCommissionTermsVersion
            {
                Version = 1,
                CreatedAtUtc = now,
                CreatedBy = new("owner", CompanyCommissionActorKind.Commissioner),
                Outputs = [new(Guid.NewGuid(), 100, "Rarefied Sykon Bavarois", 2, false)],
                Payment = new(CompanyCommissionPaymentSchedule.OnDelivery, "Delivery", 0, 0, 1000, 1000),
                PricingEvidence = new("test", "test", "test", now)
            };
            var actor = new CompanyCommissionActor(
                "owner",
                CompanyCommissionActorKind.Commissioner);
            return new TradeOrder
            {
                Id = orderId,
                CompanyProfileId = companyId,
                Title = "Member commission",
                Status = TradeOrderStatus.ReadyToAssign,
                CommissionPublication = new TradeCommissionPublication
                {
                    PublicId = publicId,
                    PublicUrl = $"https://example.test/commissions/{publicId}",
                    Version = 1,
                    PublishedAtUtc = now,
                    Ownership = ownership
                },
                CompanyCommission = new TradeCompanyCommission
                {
                    CommissionId = orderId,
                    CompanyId = new CompanyId(companyId),
                    CommissionerActorId = "owner",
                    Reference = "MA-1",
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                    CurrentTermsVersion = 1,
                    TermsVersions = [terms],
                    PublicMetadata = new()
                    {
                        PublicBriefId = publicId,
                        PublicUrl = $"https://example.test/commissions/{publicId}",
                        ViewState = CompanyCommissionPublicViewState.Published
                    },
                    ActiveClaimCapabilityRevision = 1,
                    OutputProgress = terms.Outputs.Select(output =>
                        new CompanyCommissionOutputProgress(
                            output.LineId,
                            output.ItemId,
                            output.RequiredQuantity,
                            0,
                            0,
                            0,
                            now,
                            actor)).ToArray(),
                    Gates = new(
                        new(CompanyCommissionClearanceState.NotRequired),
                        new(CompanyCommissionClearanceState.NotRequired),
                        new(CompanyCommissionClearanceState.NotRequired, [])),
                    DeliveryReadiness = new(false),
                    SettlementState = CompanyCommissionSettlementState.NotDue
                }
            };
        }
    }

    private sealed record Account(Guid ProfileId, string Key, string DiscordUserId);
}
