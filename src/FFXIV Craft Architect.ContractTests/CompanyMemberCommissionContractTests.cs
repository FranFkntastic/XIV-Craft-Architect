using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
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
    public async Task PublicBriefUsesPublicationProfileWhenOrderIdentityIsDuplicated()
    {
        await using var fixture = await MemberCommissionFixture.CreateAsync();
        await fixture.DuplicateOrderIdentityAsync();

        var projection = await fixture.LoadPublicBriefAsync();

        Assert.Equal(fixture.Order.Id, projection?.CommissionId);
    }

    [Fact]
    public async Task AuthenticatedPortableClaimReplaysTheSameCommandExactlyOnce()
    {
        await using var fixture = await MemberCommissionFixture.CreateAsync();
        var claimant = await fixture.CreateAccountAsync("Replay crafter");
        var capability = await fixture.IssueClaimCapabilityAsync();
        var initial = await fixture.LoadPublicBriefAsync();
        var commandId = Guid.NewGuid();
        var participantCredential = new string('r', 43);
        var provisionalCrafterId = Guid.NewGuid();
        var submittedAtUtc = DateTime.UtcNow;

        using var first = await fixture.ClaimPortableAsync(
            capability.PlaintextToken,
            claimant,
            commandId: commandId,
            expectedProjectionRevision: initial!.ProjectionRevision,
            participantCredential: participantCredential,
            provisionalCrafterId: provisionalCrafterId,
            submittedAtUtc: submittedAtUtc);
        using var replay = await fixture.ClaimPortableAsync(
            capability.PlaintextToken,
            claimant,
            commandId: commandId,
            expectedProjectionRevision: initial.ProjectionRevision,
            participantCredential: participantCredential,
            provisionalCrafterId: provisionalCrafterId,
            submittedAtUtc: submittedAtUtc);
        using var mismatchedReplay = await fixture.ClaimPortableAsync(
            capability.PlaintextToken,
            claimant,
            commandId: commandId,
            expectedProjectionRevision: initial.ProjectionRevision,
            participantCredential: participantCredential,
            provisionalCrafterId: Guid.NewGuid(),
            submittedAtUtc: submittedAtUtc);
        var canonical = await fixture.LoadCanonicalOrderAsync();
        var accepted = canonical.CompanyCommission!.Activity
            .Where(item => item.Kind == CompanyCommissionActivityKind.ClaimAccepted)
            .ToArray();

        Assert.True(first.IsSuccessStatusCode, await first.Content.ReadAsStringAsync());
        Assert.True(replay.IsSuccessStatusCode, await replay.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, mismatchedReplay.StatusCode);
        Assert.Single(accepted);
        Assert.Equal(commandId, canonical.CompanyCommission.ActiveClaim!.ClaimId);
        Assert.Equal(claimant.ProfileId, canonical.CompanyCommission.ActiveClaim.AccountEvidence?.ProfileId);
    }

    [Fact]
    public async Task PortableClaimWithRejectedAccountKeyDoesNotFallBackToAnonymous()
    {
        await using var fixture = await MemberCommissionFixture.CreateAsync();
        var capability = await fixture.IssueClaimCapabilityAsync();

        using var response = await fixture.ClaimPortableAsync(
            capability.PlaintextToken,
            accessKey: "cap_invalid_portable_claim_account_key_1234567890");
        var publicBrief = await fixture.LoadPublicBriefAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(publicBrief!.IsClaimed);
    }

    [Fact]
    public async Task AnonymousPortableClaimRequiresAnAccount()
    {
        await using var fixture = await MemberCommissionFixture.CreateAsync();
        var capability = await fixture.IssueClaimCapabilityAsync();

        using var response = await fixture.ClaimPortableAsync(
            capability.PlaintextToken);
        var error = await response.Content.ReadFromJsonAsync<MembershipErrorResponse>();
        var publicBrief = await fixture.LoadPublicBriefAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("claim_requires_account", error!.Error);
        Assert.False(publicBrief!.IsClaimed);
    }

    [Fact]
    public async Task ForeignAccountsCannotTakeClaimantParticipantAccess()
    {
        await using var fixture = await MemberCommissionFixture.CreateAsync();
        var claimant = await fixture.CreateAccountAsync("Contractor");
        var otherMember = await fixture.AddActiveMemberAsync("Other member");
        var capability = await fixture.IssueClaimCapabilityAsync();
        using var claimed = await fixture.ClaimPortableAsync(
            capability.PlaintextToken,
            claimant);
        claimed.EnsureSuccessStatusCode();

        using var other = await fixture.InstallParticipantAccessAsync(
            otherMember,
            new string('o', 43));
        using var owner = await fixture.InstallParticipantAccessAsync(
            fixture.Owner,
            new string('w', 43));
        using var anonymous = await fixture.InstallParticipantAccessAsync(
            null,
            new string('n', 43));

        Assert.Equal(HttpStatusCode.Forbidden, other.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, owner.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [Fact]
    public async Task LegacyClaimParticipantAccessHonorsOnlyThisCompanysCrafterBinding()
    {
        await using var fixture = await MemberCommissionFixture.CreateAsync();
        var member = await fixture.AddActiveMemberAsync("Legacy claimant");
        var returning = await fixture.CreateAccountAsync("Returning contractor");
        using var claimed = await fixture.ClaimAsync(member);
        claimed.EnsureSuccessStatusCode();
        var legacyCrafterId = await fixture.ConvertClaimToLegacyCrafterAsync(
            removeAccountEvidence: true);
        Assert.Equal(
            CrafterAccountBindingMutationStatus.Applied,
            (await fixture.Memberships.BindCrafterAsync(
                new CompanyId(Guid.NewGuid()),
                legacyCrafterId,
                returning.ProfileId,
                CrafterAccountBindingEvidence.OperatorConfirmed,
                Guid.NewGuid())).Status);

        using var crossCompany = await fixture.InstallParticipantAccessAsync(
            returning,
            new string('x', 43));
        Assert.Equal(
            CrafterAccountBindingMutationStatus.Applied,
            (await fixture.Memberships.BindCrafterAsync(
                new CompanyId(fixture.Company.Id),
                legacyCrafterId,
                returning.ProfileId,
                CrafterAccountBindingEvidence.OperatorConfirmed,
                fixture.Owner.ProfileId)).Status);
        using var sameCompany = await fixture.InstallParticipantAccessAsync(
            returning,
            new string('y', 43));

        Assert.Equal(HttpStatusCode.Forbidden, crossCompany.StatusCode);
        Assert.True(sameCompany.IsSuccessStatusCode, await sameCompany.Content.ReadAsStringAsync());
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
    public async Task OnlyTheClaimantAccountCanConfirmFinalPaymentAfterDelivery()
    {
        await using var fixture = await MemberCommissionFixture.CreateAsync();
        var claimant = await fixture.AddActiveMemberAsync("Claimant");
        var other = await fixture.AddActiveMemberAsync("Other member");
        using var claimed = await fixture.ClaimAsync(claimant);
        claimed.EnsureSuccessStatusCode();
        var projection = (await claimed.Content.ReadFromJsonAsync<CompanyCommissionParticipantBrief>())!;
        await fixture.MarkDeliveredWithSettlementPendingAsync();

        using var otherResponse = await fixture.ConfirmSettlementAsync(
            other,
            projection.Public.ProjectionRevision);
        using var ownerResponse = await fixture.ConfirmSettlementAsync(
            fixture.Owner,
            projection.Public.ProjectionRevision);

        Assert.Equal(HttpStatusCode.Forbidden, otherResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, ownerResponse.StatusCode);
        Assert.Null((await fixture.LoadCanonicalOrderAsync())
            .CompanyCommission!.SettlementPayment.CrafterReceived);

        using var claimantResponse = await fixture.ConfirmSettlementAsync(
            claimant,
            projection.Public.ProjectionRevision);

        Assert.Equal(HttpStatusCode.OK, claimantResponse.StatusCode);
        Assert.NotNull((await fixture.LoadCanonicalOrderAsync())
            .CompanyCommission!.SettlementPayment.CrafterReceived);
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
    public async Task CrafterMembershipCannotLoadOwnerCommission()
    {
        await using var fixture = await MemberCommissionFixture.CreateAsync();
        var crafter = await fixture.AddActiveMemberAsync("Crafter");

        using var response = await fixture.LoadOwnerCommissionAsync(crafter);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync());
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
    public async Task NotificationOwnerLinkResolvesWithoutActiveCompanyContext()
    {
        await using var fixture = await MemberCommissionFixture.CreateAsync();
        var recipient = await fixture.CreateAccountAsync("Discord commissioner");
        var crafter = await fixture.AddActiveMemberAsync("Crafter");
        var outsider = await fixture.CreateAccountAsync("Outsider");
        await fixture.ConfigureCommissionerRouteAsync(recipient);

        using var ownerResponse = await fixture.LoadNotificationOwnerCommissionAsync(
            fixture.Owner);
        using var recipientResponse = await fixture.LoadNotificationOwnerCommissionAsync(
            recipient);
        using var crafterResponse = await fixture.LoadNotificationOwnerCommissionAsync(crafter);
        using var outsiderResponse = await fixture.LoadNotificationOwnerCommissionAsync(outsider);

        ownerResponse.EnsureSuccessStatusCode();
        recipientResponse.EnsureSuccessStatusCode();
        var projection = await recipientResponse.Content
            .ReadFromJsonAsync<CompanyCommissionOwnerProjection>();
        Assert.Equal(fixture.Order.Id, projection!.Order.Id);
        Assert.Equal(fixture.Company.Id, projection.Order.CompanyProfileId);
        Assert.Equal(HttpStatusCode.NotFound, crafterResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, outsiderResponse.StatusCode);
    }

    [Fact]
    public async Task NotificationOwnerLinkFailsClosedWhenCompanyIdentityIsDuplicated()
    {
        await using var fixture = await MemberCommissionFixture.CreateAsync();
        var recipient = await fixture.CreateAccountAsync("Discord commissioner");
        await fixture.ConfigureCommissionerRouteAsync(recipient);
        await fixture.DuplicateCompanyIdentityAsync();

        using var response = await fixture.LoadNotificationOwnerCommissionAsync(recipient);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
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
    public async Task OwnerComparisonRejectsUnauthorizedAndOversizedBatches()
    {
        await using var fixture = await MemberCommissionFixture.CreateAsync();
        var outsider = await fixture.CreateAccountAsync("Outsider");
        var items = Enumerable.Range(0, 51)
            .Select(_ => new CompanyCommissionOwnerComparisonItem
            {
                OrderId = Guid.NewGuid(),
                CommissionId = Guid.NewGuid(),
                ProfileObjectRevision = new(1),
                ObjectRevision = CompanyRecordRevision.None,
                CompanyRevision = CompanyRecordRevision.None
            })
            .ToArray();

        using var unauthorized = await fixture.CompareOwnersAsync(
            outsider,
            [items[0]]);
        using var oversized = await fixture.CompareOwnersAsync(fixture.Owner, items);

        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, oversized.StatusCode);
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

        public Task<IssuedCompanyCommissionCapability> IssueClaimCapabilityAsync() =>
            Capabilities.IssueAsync(
                new CompanyId(Company.Id),
                Order.Id,
                Order.CommissionPublication!.PublicId,
                CompanyCommissionCapabilityKind.Claim,
                grantId: null,
                Order.CompanyCommission!.ActiveClaimCapabilityRevision,
                DateTime.UtcNow,
                CancellationToken.None);

        public async Task<HttpResponseMessage> ClaimPortableAsync(
            string claimCapability,
            Account? account = null,
            string? accessKey = null,
            Guid? commandId = null,
            long? expectedProjectionRevision = null,
            string? participantCredential = null,
            Guid? provisionalCrafterId = null,
            DateTime? submittedAtUtc = null)
        {
            var client = application.CreateClient();
            var projection = await LoadPublicBriefAsync();
            var key = accessKey ?? account?.Key;
            if (!string.IsNullOrWhiteSpace(key))
            {
                client.DefaultRequestHeaders.Add("X-Profile-Key", key);
            }
            return await client.PostAsJsonAsync(
                $"/xivdata/commission-briefs/{Order.CommissionPublication!.PublicId}/commands/claim",
                new PublicCompanyCommissionCommandEnvelope
                {
                    ProtocolVersion = CompanyCommissionProtocol.Version1,
                    PublicBriefId = Order.CommissionPublication.PublicId,
                    ExpectedProjectionRevision = expectedProjectionRevision ??
                        projection!.ProjectionRevision,
                    CommandId = commandId ?? Guid.NewGuid(),
                    ClaimCapability = claimCapability,
                    Command = JsonSerializer.SerializeToElement(new
                    {
                        termsVersion = 1,
                        claimIdentityVersion = 2,
                        claimantCharacter = new CompanyCommissionClaimantCharacter(
                            provisionalCrafterId ?? Guid.NewGuid(),
                            "Wei Ning",
                            "Siren",
                            "12345678",
                            "https://na.finalfantasyxiv.com/lodestone/character/12345678/",
                            submittedAtUtc ?? DateTime.UtcNow),
                        existingCrafterId = (Guid?)null,
                        newParticipantCredential = participantCredential ?? new string('p', 43)
                    })
                });
        }

        public Task<CompanyCommissionPublicBrief?> LoadPublicBriefAsync() =>
            Commissions.LoadPublicAsync(
                Order.CommissionPublication!.PublicId,
                CancellationToken.None);

        public async Task DuplicateOrderIdentityAsync()
        {
            var duplicate = await CreateAccountAsync("Duplicate order profile");
            var put = await Profiles.PutObjectAsync(
                duplicate.ProfileId.ToString("D"),
                ProfileSyncCollections.TradeOrders,
                Order.Id.ToString("D"),
                JsonSerializer.Serialize(Order, JsonOptions),
                expectedRevision: 0,
                ct: CancellationToken.None);
            Assert.True(put.Success);
        }

        public Task<HttpResponseMessage> ReportProgressAsync(Account account, long projectionRevision) =>
            PostProgressAsync(CreateClient(account.Key), null, projectionRevision);

        public Task<HttpResponseMessage> ReportProgressWithCapabilityAsync(
            string capability,
            long projectionRevision) =>
            PostProgressAsync(application.CreateClient(), capability, projectionRevision);

        public async Task MarkDeliveredWithSettlementPendingAsync()
        {
            var envelope = await Profiles.LoadObjectAsync(
                Owner.ProfileId.ToString("D"),
                ProfileSyncCollections.TradeOrders,
                Order.Id.ToString("D"),
                CancellationToken.None);
            var order = JsonSerializer.Deserialize<TradeOrder>(
                envelope!.PayloadJson,
                JsonOptions)!;
            order.Status = TradeOrderStatus.Completed;
            order.CompanyCommission = order.CompanyCommission! with
            {
                SettlementState = CompanyCommissionSettlementState.Pending
            };
            var orderPut = await Profiles.PutObjectAsync(
                Owner.ProfileId.ToString("D"),
                ProfileSyncCollections.TradeOrders,
                Order.Id.ToString("D"),
                JsonSerializer.Serialize(order, JsonOptions),
                expectedRevision: envelope.Revision,
                ct: CancellationToken.None);
            Assert.True(orderPut.Success);
        }

        public Task<HttpResponseMessage> ConfirmSettlementAsync(
            Account account,
            long projectionRevision) =>
            CreateClient(account.Key).PostAsJsonAsync(
                $"/xivdata/commission-briefs/{Order.CommissionPublication!.PublicId}/commands/confirm-settlement-received",
                new PublicCompanyCommissionCommandEnvelope
                {
                    ProtocolVersion = CompanyCommissionProtocol.Version1,
                    PublicBriefId = Order.CommissionPublication.PublicId,
                    ExpectedProjectionRevision = projectionRevision,
                    CommandId = Guid.NewGuid(),
                    Command = JsonSerializer.SerializeToElement(new
                    {
                        termsVersion = 1,
                        note = "Received by mail"
                    })
                });

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

        public async Task<Guid> LoadActiveCrafterIdAsync()
        {
            var order = await LoadCanonicalOrderAsync();
            return order.CompanyCommission!.ActiveClaim!.CrafterId!.Value;
        }

        public async Task<Guid> ConvertClaimToLegacyCrafterAsync(
            bool removeAccountEvidence = false)
        {
            var legacyCrafter = new TradeCrafterProfile
            {
                Id = Guid.NewGuid(),
                CompanyProfileId = Company.Id,
                DisplayName = "Returning crafter legacy record",
                WorldName = "Siren",
                LodestoneCharacterId = "49131404"
            };
            var crafterPut = await Profiles.PutObjectAsync(
                Owner.ProfileId.ToString("D"),
                ProfileSyncCollections.TradeCrafters,
                legacyCrafter.Id.ToString("D"),
                JsonSerializer.Serialize(legacyCrafter, JsonOptions),
                expectedRevision: 0,
                ct: CancellationToken.None);
            Assert.True(crafterPut.Success);

            var envelope = await Profiles.LoadObjectAsync(
                Owner.ProfileId.ToString("D"),
                ProfileSyncCollections.TradeOrders,
                Order.Id.ToString("D"),
                CancellationToken.None);
            var order = JsonSerializer.Deserialize<TradeOrder>(
                envelope!.PayloadJson,
                JsonOptions)!;
            order.AssignedCrafterId = legacyCrafter.Id;
            order.CompanyCommission = order.CompanyCommission! with
            {
                ActiveClaim = order.CompanyCommission.ActiveClaim! with
                {
                    CrafterId = legacyCrafter.Id,
                    AccountEvidence = removeAccountEvidence
                        ? null
                        : order.CompanyCommission.ActiveClaim.AccountEvidence
                }
            };
            var orderPut = await Profiles.PutObjectAsync(
                Owner.ProfileId.ToString("D"),
                ProfileSyncCollections.TradeOrders,
                Order.Id.ToString("D"),
                JsonSerializer.Serialize(order, JsonOptions),
                expectedRevision: envelope.Revision,
                ct: CancellationToken.None);
            Assert.True(orderPut.Success);
            return legacyCrafter.Id;
        }

        public Task<HttpResponseMessage> LoadOwnerCommissionAsync(Account account)
        {
            var client = CreateClient(account.Key);
            return client.GetAsync(
                $"/trade/v1/companies/{Company.Id:D}/commissions/{Order.Id:D}/owner");
        }

        public Task<HttpResponseMessage> CompareOwnersAsync(
            Account account,
            IReadOnlyList<CompanyCommissionOwnerComparisonItem> items)
        {
            var client = CreateClient(account.Key);
            return client.PostAsJsonAsync(
                $"/trade/v1/companies/{Company.Id:D}/commissions/owner-comparison",
                new CompanyCommissionOwnerComparisonRequest { Items = items });
        }

        public Task<HttpResponseMessage> LoadNotificationOwnerCommissionAsync(Account account)
        {
            var client = CreateClient(account.Key);
            return client.GetAsync($"/trade/v1/commissions/{Order.Id:D}/owner");
        }

        public async Task ConfigureCommissionerRouteAsync(
            Account account,
            long expectedRevision = 0,
            DiscordNotificationDestinationMode destinationMode =
                DiscordNotificationDestinationMode.CommissionerDirectMessage)
        {
            var route = await Notifications.PutRouteAsync(
                new CompanyId(Company.Id),
                new DiscordNotificationRouteUpdate(
                    account.DiscordUserId,
                    destinationMode,
                    destinationMode is DiscordNotificationDestinationMode.UpdateChannel or
                        DiscordNotificationDestinationMode.Both
                        ? "123456789012345678"
                        : null,
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

        public Task<HttpResponseMessage> InstallParticipantAccessAsync(
            Account? account,
            string participantCredential)
        {
            var client = account == null
                ? application.CreateClient()
                : CreateClient(account.Key);
            return client.PostAsJsonAsync(
                $"/xivdata/commission-briefs/{Order.CommissionPublication!.PublicId}/participant-access",
                new CompanyCommissionParticipantAccessRequest(participantCredential));
        }

        public Task RevokeParticipantCapabilitiesAsync() =>
            Capabilities.RevokeAllAsync(
                new CompanyId(Company.Id),
                Order.Id,
                CompanyCommissionCapabilityKind.Participant,
                DateTime.UtcNow,
                CancellationToken.None);

        public async Task<TradeOrder> LoadCanonicalOrderAsync()
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

        public static TradeOrder CreateOrder(Guid companyId)
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

    [Theory]
    [InlineData(1050, true, false, true)]
    [InlineData(950, true, false, false)]
    [InlineData(1051, true, true, false)]
    [InlineData(949, true, true, true)]
    [InlineData(2000, false, false, true)]
    [InlineData(-1, true, false, false)]
    [InlineData(1010, true, false, false, true)]
    public async Task ClaimRefreshCommitsOnlyTheAcceptedPayment(int listingPrice, bool knownRate, bool conflict, bool useLink, bool mixed = false)
    {
        // A sealed single-listing route exercises the real optimizer and CAS without a market server or an account key.
        var root = Directory.CreateTempSubdirectory("claim-pricing-");
        try
        {
            var profiles = new SqliteProfileHostStore(new ProfileHostOptions { Enabled = true, DatabasePath = Path.Combine(root.FullName, "profiles.db") });
            var host = await profiles.CreateProfileAsync("Offline fixture", default);
            var company = TradeCompanyProfile.CreateLocal("Offline company", DateTime.UtcNow);
            var order = MemberCommissionFixture.CreateOrder(company.Id);
            var commission = order.CompanyCommission!;
            var terms = commission.CurrentTerms;
            var now = DateTime.UtcNow;
            var policy = new TradeMaterialPricingPolicy { SafetyAllowancePercent = mixed ? 10 : 0, MinimumSafetyAllowanceGil = 0 };
            var plan = new CraftingPlan
            {
                RootItems = [new PlanNode
            {
                ItemId = 100, Name = "Market item", Quantity = 1, Source = AcquisitionSource.MarketBuyNq,
                CanBuyFromMarket = true, SourceReason = AcquisitionSourceReason.UserSelected
            }]
            };
            var provided = new CompanyCommissionMaterialTerm(Guid.NewGuid(), 101, "Company stock", 2, false,
                CommissionMaterialResponsibility.Provided, 50, 100);
            if (mixed)
            {
                plan.RootItems.Add(new PlanNode
                {
                    ItemId = 101,
                    Name = provided.Name,
                    Quantity = 2,
                    Source = AcquisitionSource.VendorBuy,
                    VendorPrice = 75,
                    CanBuyFromVendor = true,
                    SourceReason = AcquisitionSourceReason.UserSelected
                });
            }
            var id = plan.Id.ToString();
            var old = new TradeMaterialQuote
            {
                CompanyProfileId = company.Id,
                SourcePlanId = id,
                PlanSessionVersion = 1,
                MarketAnalysisVersion = 1,
                PolicyFingerprint = TradeMaterialPricingPolicyNormalizer.Fingerprint(policy),
                AppliedPolicy = policy,
                RouteSelectionKey = "old",
                QuotedAtUtc = now.AddHours(-2),
                ExpiresAtUtc = now.AddHours(-1),
                RouteCashRequired = 1000,
                SafetyAllowance = mixed ? 100 : 0,
                MaterialReimbursement = mixed ? 1100 : 1000,
                WorldStops = 0,
                Lines = [new(100, "Market item", 1, false, 1000, ["Siren (Aether)"], null)]
            };
            terms = terms with
            {
                Payment = new(CompanyCommissionPaymentSchedule.OnDelivery, "Commission", 1000, 100, 0, 1100,
                    MaterialAdjustmentPercent: knownRate ? 10 : null),
                Materials = [new(Guid.NewGuid(), 100, "Market item", 1, false, CommissionMaterialResponsibility.Crafter, 1000, 1000)],
                PricingEvidence = new("ProcurementRoute", "DC", "Aether", old.QuotedAtUtc, old)
            };
            if (mixed)
            {
                terms = terms with
                {
                    Materials = [.. terms.Materials, provided],
                    Payment = terms.Payment with { MaterialAdjustment = 110, MaterialReimbursement = 1100, Total = 1210 }
                };
            }
            order.CompanyCommission = commission with { TermsVersions = [terms] };
            order.CraftPlanId = id;
            order.CraftPlanSavedAtUtc = now;
            order.SourceSnapshot = new()
            {
                RootItems = terms.Outputs.Select(o => new TradeOrderRootItemSnapshot(o.ItemId, o.Name, o.RequiredQuantity, o.MustBeHq, 0)).ToArray(),
                SourcePlanId = id,
                PlanSessionVersion = 1,
                MarketAnalysisVersion = 1,
                MaterialQuote = old,
                Region = "North America",
                DataCenter = "Aether",
                MarketFetchScope = MarketFetchScope.SelectedDataCenter,
                RequestedDataCenters = ["Aether"]
            };
            async Task Put(string collection, string key, object value) =>
                Assert.True((await profiles.PutObjectAsync(host.ProfileId, collection, key, JsonSerializer.Serialize(value), 0, default)).Success);
            await Put(ProfileSyncCollections.TradeCompanyProfiles, company.Id.ToString(), company);
            await Put(ProfileSyncCollections.TradeOrders, order.Id.ToString(), order);
            await Put(ProfileSyncCollections.Plans, id, new ProfileSyncPlanSnapshot
            {
                Id = id,
                LinkedOrderId = order.Id,
                SavedAt = now,
                PlanJson = JsonSerializer.Serialize(plan),
                PlanStateJson = StoredPlanRuntimeState.Capture(plan)
            });
            var companies = new ProfileHostedTradeCompanyService(profiles, new ProfileAccessKeyHasher());
            var profile = Guid.Parse(host.ProfileId);
            var access = new TradeCompanyAccessContext(new(company.Id), profile, TradeCompanyRole.Owner, profile);
            var ownership = order.CommissionPublication!.Ownership!;
            var publicId = order.CommissionPublication.PublicId;
            Assert.True((await companies.PutRecordAsync(access, TradeCompanyRecordKinds.Publication, publicId,
                JsonSerializer.Serialize(ownership), CompanyRecordRevision.None, "fixture-publication")).Success);
            using var http = new HttpClient(new OfflineListing(listingPrice));
            var universalis = new UniversalisService(http);
            universalis.SeedWorldData(new() { WorldIdToName = { [57] = "Siren" }, DataCenterToWorlds = { ["Aether"] = ["Siren"] } });
            var clock = new ClaimClock();
            var pricing = new ClaimMaterialPricing(profiles, new JsonFileMarketCacheService(universalis, root.FullName), universalis, clock);
            var log = new PricingLog();
            var service = new HostedCompanyCommissionService(companies, profiles, clock, [], log, pricing);
            var snapshot = (await service.LoadOwnerAsync(access, order.Id))!;
            var claim = new ClaimCompanyCommissionCommand(new(new(company.Id), order.Id, snapshot.Envelope.RecordRevision,
                snapshot.CompanyRevision, Guid.NewGuid(), 1), 1, null, Guid.NewGuid(), new(Guid.NewGuid(), "123456789012345678", "Offline"));
            Task<CompanyCommissionMutationResult> Submit(ClaimCompanyCommissionCommand candidate) => useLink
                ? service.ExecuteCapabilityAsync(new(new(company.Id), order.Id, publicId, CompanyCommissionCapabilityKind.Claim, null, 1), candidate)
                : service.ExecuteAccountClaimAsync(ownership, publicId, candidate.AccountEvidence!.ProfileId, candidate);
            CompanyCommissionMutationResult result;
            if (listingPrice == 1050)
            {
                var rival = claim with
                {
                    Context = claim.Context with { CommandId = Guid.NewGuid() },
                    AccountEvidence = new(Guid.NewGuid(), "123456789012345679", "Other crafter")
                };
                var race = await Task.WhenAll(Submit(claim), Submit(rival));
                result = Assert.Single(race, r => r.Success);
                claim = race[0].Success ? claim : rival;
            }
            else
            {
                result = await Submit(claim);
            }
            var refreshed = knownRate && listingPrice > 0;
            if (refreshed)
            {
                Assert.True(log.Warning == null, log.Warning);
            }
            else
            {
                Assert.Contains(knownRate ? "Market data incomplete" : "rate is unknown", log.Warning);
            }
            if (conflict)
            {
                Assert.Equal("claim_price_changed", result.ErrorCode);
                Assert.Null(result.Order!.CompanyCommission!.ActiveClaim);
                var response = CompanyCommissionEndpoints.ClaimPriceChanged(result);
                Assert.Equal(409, ((Microsoft.AspNetCore.Http.IStatusCodeHttpResult)response).StatusCode);
                var body = JsonSerializer.SerializeToElement(((Microsoft.AspNetCore.Http.IValueHttpResult)response).Value);
                Assert.Equal("claim_price_changed", body.GetProperty("code").GetString());
                Assert.Equal(1100m, body.GetProperty("previousPayment").GetDecimal());
                Assert.Equal(result.Order.CompanyCommission.CurrentTerms.Payment.Total, body.GetProperty("currentPayment").GetDecimal());
                Assert.Equal(2, body.GetProperty("termsVersion").GetInt32());
                if (useLink)
                {
                    var capability = new CompanyCommissionCapabilityResolution(new(company.Id), order.Id,
                        publicId, CompanyCommissionCapabilityKind.Claim, null, 1);
                    Assert.NotNull(await service.CreateCapabilityCommandContextAsync(capability, 0, claim.Context.CommandId, 1,
                        claimAccountId: claim.AccountEvidence!.ProfileId, claimTermsVersion: 1));
                    Assert.Null(await service.CreateCapabilityCommandContextAsync(capability, 0, claim.Context.CommandId, 1,
                        claimAccountId: claim.AccountEvidence.ProfileId, claimTermsVersion: 2));
                }
                var retry = await Submit(claim);
                Assert.Equal("claim_price_changed", retry.ErrorCode);
                Assert.Null(retry.Order!.CompanyCommission!.ActiveClaim);
                var repeatedResponse = CompanyCommissionEndpoints.ClaimPriceChanged(retry);
                Assert.Equal(409, ((Microsoft.AspNetCore.Http.IStatusCodeHttpResult)repeatedResponse).StatusCode);
                Assert.Equal(body.GetRawText(), JsonSerializer.Serialize(((Microsoft.AspNetCore.Http.IValueHttpResult)repeatedResponse).Value));
                clock.Offset = TimeSpan.FromDays(1); // Even a delayed confirmation accepts the price shown.
                snapshot = (await service.LoadOwnerAsync(access, order.Id))!;
                claim = claim with
                {
                    TermsVersion = 2,
                    Context = claim.Context with
                    {
                        ExpectedObjectRevision = snapshot.Envelope.RecordRevision,
                        ExpectedCompanyRevision = snapshot.CompanyRevision
                    }
                };
                result = await Submit(claim);
            }
            Assert.Equal(CompanyCommissionMutationStatus.Applied, result.Status);
            Assert.Equal(CompanyCommissionMutationStatus.Replayed, (await Submit(claim)).Status);
            var final = (await service.LoadOwnerAsync(access, order.Id))!.Order.CompanyCommission!;
            Assert.Equal(refreshed ? 2 : 1, final.ActiveClaim!.AcceptedTermsVersion);
            Assert.Equal(final.CurrentTermsVersion, final.ParticipantAcknowledgedTermsVersion);
            if (mixed)
            {
                Assert.Equal(provided, Assert.Single(final.CurrentTerms.Materials, m => m.Responsibility == CommissionMaterialResponsibility.Provided));
                var quote = final.CurrentTerms.PricingEvidence.MaterialQuote!;
                Assert.Equal(100, Assert.Single(quote.Lines).ItemId);
                Assert.Equal(101, quote.SafetyAllowance);
                Assert.Equal(1111, quote.MaterialReimbursement);
                Assert.Equal(quote.MaterialReimbursement, final.CurrentTerms.Payment.MaterialReimbursement);
                Assert.Equal(111, final.CurrentTerms.Payment.MaterialAdjustment);
                Assert.Equal(1222, final.CurrentTerms.Payment.Total);
            }
            else
            {
                Assert.Equal(refreshed ? listingPrice + decimal.Round(listingPrice / 10m, 0, MidpointRounding.AwayFromZero) : 1100m, final.CurrentTerms.Payment.Total);
            }
            Assert.Equal(refreshed ? 1 : 0, final.Activity.Count(e => e.Kind == CompanyCommissionActivityKind.TermsAmended &&
                e.Actor.Kind == CompanyCommissionActorKind.System && e.Visibility == CompanyCommissionActivityVisibility.Shared));
            Assert.Single(final.Activity, e => e.Kind == CompanyCommissionActivityKind.ClaimAccepted);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            root.Delete(true);
        }
    }

    private sealed class ClaimClock : TimeProvider
    {
        public TimeSpan Offset { get; set; }
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow + Offset;
    }

    private sealed class PricingLog : Microsoft.Extensions.Logging.ILogger<HostedCompanyCommissionService>
    {
        public string? Warning { get; private set; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel level) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel level, Microsoft.Extensions.Logging.EventId id,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Warning = formatter(state, exception);
    }

    private sealed class OfflineListing(int price) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (price < 0)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }
            var timestamp = DateTimeOffset.UtcNow;
            var ids = Uri.UnescapeDataString(request.RequestUri!.Segments[^1]).Split(',').Select(int.Parse).ToArray();
            object Listing(int id) => new
            {
                itemID = id,
                dcName = "Aether",
                lastUploadTime = timestamp.ToUnixTimeMilliseconds(),
                worldUploadTimes = new Dictionary<int, long> { [57] = timestamp.ToUnixTimeMilliseconds() },
                averagePrice = price,
                averagePriceNQ = price,
                listings = new[] { new { worldName = "Siren", pricePerUnit = price, quantity = id == 101 ? 2 : 1,
                    total = price * (id == 101 ? 2 : 1), retainerName = "Fixture", lastReviewTime = timestamp.ToUnixTimeSeconds() } }
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(ids.Length == 1 ? Listing(ids[0]) : new { items = ids.ToDictionary(id => id, Listing) })
            });
        }
    }

    private sealed record Account(Guid ProfileId, string Key, string DiscordUserId);
}
