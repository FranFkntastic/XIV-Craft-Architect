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
using Microsoft.Extensions.Hosting;

namespace FFXIV_Craft_Architect.ContractTests;

public sealed class MembershipContractTests
{
    [Fact]
    public async Task CompanyPutRefusesMismatchedIdentityAndCompetingFounder()
    {
        await using var fixture = await MembershipFixture.CreateAsync();
        var victim = await fixture.CreateAccountAsync("Victim owner");
        var attacker = await fixture.CreateAccountAsync("Attacker");
        var company = CreateCompany();
        var companyId = new CompanyId(company.Id);
        using var victimClient = fixture.CreateClient(victim.Key);
        using var attackerClient = fixture.CreateClient(attacker.Key);
        await PutCompanyAsync(victimClient, company);

        await PutCompanyExpectingConflictAsync(attackerClient, company, objectId: Guid.NewGuid());
        var mismatched = await fixture.Memberships.LoadAsync(companyId, attacker.ProfileId);
        await PutCompanyExpectingConflictAsync(attackerClient, company);
        var competing = await fixture.Memberships.LoadAsync(companyId, attacker.ProfileId);
        var owner = await fixture.Memberships.LoadAsync(companyId, victim.ProfileId);

        Assert.Null(mismatched);
        Assert.Null(competing);
        Assert.Equal(MembershipRole.Owner, owner!.Role);
        Assert.Equal(MembershipState.Active, owner.State);
    }

    [Fact]
    public async Task ReconciliationRefusesAmbiguousCompanyHolders()
    {
        await using var fixture = await MembershipFixture.CreateWithAmbiguousCompanyAsync();
        var companyId = new CompanyId(fixture.ExistingCompanyId!.Value);

        await fixture.Reconciler.RunReconciliationAsync(CancellationToken.None);
        var first = await fixture.Memberships.LoadAsync(
            companyId,
            fixture.ExistingProfileId!.Value);
        var second = await fixture.Memberships.LoadAsync(
            companyId,
            fixture.SecondExistingProfileId!.Value);

        Assert.Null(first);
        Assert.Null(second);
    }

    [Theory]
    [InlineData(MembershipState.Pending)]
    [InlineData(MembershipState.Denied)]
    [InlineData(MembershipState.Revoked)]
    public async Task FounderBindingNeverOverwritesExistingMembership(MembershipState state)
    {
        await using var fixture = await MembershipFixture.CreateAsync();
        var account = await fixture.CreateAccountAsync("Existing member");
        var companyId = new CompanyId(Guid.NewGuid());
        await fixture.Memberships.RequestAsync(companyId, account.ProfileId, "Keep this evidence");
        if (state == MembershipState.Denied)
        {
            await fixture.Memberships.DenyAsync(
                companyId,
                account.ProfileId,
                Guid.NewGuid());
        }
        else if (state == MembershipState.Revoked)
        {
            await fixture.Memberships.ApproveAsync(
                companyId,
                account.ProfileId,
                Guid.NewGuid());
            await fixture.Memberships.RevokeAsync(
                companyId,
                account.ProfileId,
                Guid.NewGuid());
        }
        var before = await fixture.Memberships.LoadAsync(companyId, account.ProfileId);
        var eventCount = (await fixture.Memberships.LoadEventsAsync(
            companyId,
            account.ProfileId)).Count;

        var result = await fixture.Memberships.EnsureFounderAsync(companyId, account.ProfileId);
        var after = await fixture.Memberships.LoadAsync(companyId, account.ProfileId);
        var events = await fixture.Memberships.LoadEventsAsync(companyId, account.ProfileId);

        Assert.Equal(FounderBindingStatus.ExistingMembership, result.Status);
        Assert.Equal(before, after);
        Assert.Equal(state, after!.State);
        Assert.Equal("Keep this evidence", after.RequestNote);
        Assert.Equal(eventCount, events.Count);
    }

    [Fact]
    public async Task CommissionerInvitationEnrollsDiscordAccountOnceWithoutACommission()
    {
        await using var fixture = await MembershipFixture.CreateAsync();
        var owner = await fixture.CreateAccountAsync("Owner");
        var recipient = await fixture.CreateAccountAsync("Invited crafter");
        var other = await fixture.CreateAccountAsync("Other crafter");
        var company = CreateCompany();
        using var ownerClient = fixture.CreateClient(owner.Key);
        using var recipientClient = fixture.CreateClient(recipient.Key);
        using var otherClient = fixture.CreateClient(other.Key);
        await PutCompanyAsync(ownerClient, company);

        var requestedExpiry = DateTimeOffset.UtcNow.AddDays(30);
        using var issuedResponse = await ownerClient.PostAsJsonAsync(
            $"/trade/v1/companies/{company.Id:D}/membership-invitations",
            new MembershipInvitationIssueBody(null, requestedExpiry));
        var invitation = await issuedResponse.Content.ReadFromJsonAsync<MembershipInvitationResponse>();
        Assert.Equal(HttpStatusCode.OK, issuedResponse.StatusCode);
        Assert.NotNull(invitation?.Token);
        Assert.Equal(requestedExpiry, invitation.ExpiresAtUtc);

        using var accepted = await recipientClient.PostAsync(
            $"/trade/v1/membership-invitations/{invitation.Token}/accept",
            null);
        using var replayed = await recipientClient.PostAsync(
            $"/trade/v1/membership-invitations/{invitation.Token}/accept",
            null);
        using var refusedOther = await otherClient.PostAsync(
            $"/trade/v1/membership-invitations/{invitation.Token}/accept",
            null);
        var membership = await fixture.Memberships.LoadAsync(
            new CompanyId(company.Id),
            recipient.ProfileId);
        var events = await fixture.Memberships.LoadEventsAsync(
            new CompanyId(company.Id),
            recipient.ProfileId);

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replayed.StatusCode);
        Assert.Equal(HttpStatusCode.Gone, refusedOther.StatusCode);
        Assert.Equal(MembershipState.Active, membership!.State);
        Assert.Equal(MembershipRole.Crafter, membership.Role);
        Assert.Single(events);
    }

    [Fact]
    public async Task InvitationRefusesForeignIssuerRevocationAndExpiryWithoutSessionLoss()
    {
        await using var fixture = await MembershipFixture.CreateAsync();
        var owner = await fixture.CreateAccountAsync("Owner");
        var outsider = await fixture.CreateAccountAsync("Outsider");
        var recipient = await fixture.CreateAccountAsync("Recipient");
        var company = CreateCompany();
        using var ownerClient = fixture.CreateClient(owner.Key);
        using var outsiderClient = fixture.CreateClient(outsider.Key);
        using var recipientClient = fixture.CreateClient(recipient.Key);
        await PutCompanyAsync(ownerClient, company);

        using var foreignIssue = await outsiderClient.PostAsJsonAsync(
            $"/trade/v1/companies/{company.Id:D}/membership-invitations",
            new MembershipInvitationIssueBody(null, DateTimeOffset.UtcNow.AddDays(7)));
        var revoked = await fixture.Memberships.IssueInvitationAsync(
            new CompanyId(company.Id),
            owner.ProfileId,
            null);
        using var foreignRevoke = await outsiderClient.DeleteAsync(
            $"/trade/v1/companies/{company.Id:D}/membership-invitations/{revoked.InvitationId:D}");
        using var ownerRevoke = await ownerClient.DeleteAsync(
            $"/trade/v1/companies/{company.Id:D}/membership-invitations/{revoked.InvitationId:D}");
        using var revokedAccept = await recipientClient.PostAsync(
            $"/trade/v1/membership-invitations/{revoked.Token}/accept",
            null);

        var expired = await fixture.Memberships.IssueInvitationAsync(
            new CompanyId(company.Id),
            owner.ProfileId,
            null);
        await fixture.ExpireInvitationAsync(expired.InvitationId);
        using var expiredAccept = await recipientClient.PostAsync(
            $"/trade/v1/membership-invitations/{expired.Token}/accept",
            null);
        using var sessionStillWorks = await recipientClient.GetAsync("/trade/v1/memberships");

        Assert.Equal(HttpStatusCode.Forbidden, foreignIssue.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, foreignRevoke.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, ownerRevoke.StatusCode);
        Assert.Equal(HttpStatusCode.Gone, revokedAccept.StatusCode);
        Assert.Equal(HttpStatusCode.Gone, expiredAccept.StatusCode);
        Assert.Equal(HttpStatusCode.OK, sessionStillWorks.StatusCode);
        Assert.Null(await fixture.Memberships.LoadAsync(new CompanyId(company.Id), recipient.ProfileId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OldInvitationCannotOverrideMembershipDecisionEvenAfterNewRequest(bool revoke)
    {
        await using var fixture = await MembershipFixture.CreateAsync();
        var owner = await fixture.CreateAccountAsync("Owner");
        var recipient = await fixture.CreateAccountAsync("Recipient");
        var company = CreateCompany();
        var companyId = new CompanyId(company.Id);
        using var ownerClient = fixture.CreateClient(owner.Key);
        using var recipientClient = fixture.CreateClient(recipient.Key);
        await PutCompanyAsync(ownerClient, company);
        var oldInvite = await fixture.Memberships.IssueInvitationAsync(companyId, owner.ProfileId, null);
        await fixture.Memberships.RequestAsync(companyId, recipient.ProfileId, null);
        if (revoke)
        {
            await fixture.Memberships.ApproveAsync(companyId, recipient.ProfileId, owner.ProfileId);
            await fixture.Memberships.RevokeAsync(companyId, recipient.ProfileId, owner.ProfileId);
        }
        else
        {
            await fixture.Memberships.DenyAsync(companyId, recipient.ProfileId, owner.ProfileId);
        }
        using var refused = await recipientClient.PostAsync(
            $"/trade/v1/membership-invitations/{oldInvite.Token}/accept", null);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("invitation_predates_membership_decision", await refused.Content.ReadAsStringAsync());
        var rerequest = await fixture.Memberships.RequestAsync(companyId, recipient.ProfileId, null);
        Assert.Equal(MembershipMutationStatus.RequestClosed, rerequest.Status);
        var pendingRefusal = await fixture.Memberships.ConsumeInvitationAsync(oldInvite.Token!, recipient.ProfileId);
        Assert.Equal(MembershipInvitationConsumptionStatus.MembershipDecisionConflict, pendingRefusal.Status);
        Assert.Equal(
            revoke ? MembershipState.Revoked : MembershipState.Denied,
            (await fixture.Memberships.LoadAsync(companyId, recipient.ProfileId))!.State);
        Assert.Null((await fixture.Memberships.LoadInvitationAsync(oldInvite.Token!))!.ConsumedAtUtc);
        var fresh = await fixture.Memberships.IssueInvitationAsync(companyId, owner.ProfileId, null);
        Assert.Equal(MembershipInvitationConsumptionStatus.Applied,
            (await fixture.Memberships.ConsumeInvitationAsync(fresh.Token!, recipient.ProfileId)).Status);
        Assert.Equal(MembershipInvitationConsumptionStatus.Replayed,
            (await fixture.Memberships.ConsumeInvitationAsync(fresh.Token!, recipient.ProfileId)).Status);
    }

    [Fact]
    public async Task RequestApprovalGrantsOwnMembershipAndRefusesNonAdministrators()
    {
        await using var fixture = await MembershipFixture.CreateAsync();
        var owner = await fixture.CreateAccountAsync("Owner");
        var crafter = await fixture.CreateAccountAsync("Crafter");
        var outsider = await fixture.CreateAccountAsync("Outsider");
        var company = CreateCompany();
        using var ownerClient = fixture.CreateClient(owner.Key);
        using var crafterClient = fixture.CreateClient(crafter.Key);
        using var outsiderClient = fixture.CreateClient(outsider.Key);
        await PutCompanyAsync(ownerClient, company);

        using var requested = await crafterClient.PostAsJsonAsync(
            $"/trade/v1/companies/{company.Id:D}/membership-requests",
            new MembershipRequestBody("Available for commissions"));
        using var forbidden = await outsiderClient.GetAsync(
            $"/trade/v1/companies/{company.Id:D}/membership-requests");
        using var pending = await ownerClient.GetAsync(
            $"/trade/v1/companies/{company.Id:D}/membership-requests");
        using var approved = await ownerClient.PostAsync(
            $"/trade/v1/companies/{company.Id:D}/memberships/{crafter.ProfileId:D}/approve",
            null);
        var own = await crafterClient.GetFromJsonAsync<MembershipResponse[]>(
            "/trade/v1/memberships");
        using var crafterAdminAttempt = await crafterClient.GetAsync(
            $"/trade/v1/companies/{company.Id:D}/membership-requests");
        using var unknown = await outsiderClient.PostAsJsonAsync(
            $"/trade/v1/companies/{Guid.NewGuid():D}/membership-requests",
            new MembershipRequestBody(null));

        Assert.Equal(HttpStatusCode.OK, requested.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Single((await pending.Content.ReadFromJsonAsync<MembershipResponse[]>())!);
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        Assert.Single(own!);
        Assert.Equal("active", own![0].State);
        Assert.Equal("crafter", own[0].Role);
        Assert.Equal(HttpStatusCode.Forbidden, crafterAdminAttempt.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    // Leak guard: the member-readable binding list must stay closed to pending requesters and
    // outsiders, and must expose only record↔account pairs.

    [Fact]
    public async Task CrafterBindingPairsAreReadableByActiveMembersOnly()
    {
        await using var fixture = await MembershipFixture.CreateAsync();
        var owner = await fixture.CreateAccountAsync("Owner");
        var crafter = await fixture.CreateAccountAsync("Crafter");
        var requester = await fixture.CreateAccountAsync("Requester");
        var outsider = await fixture.CreateAccountAsync("Outsider");
        var company = CreateCompany();
        var record = new TradeCrafterProfile
        {
            Id = Guid.NewGuid(),
            CompanyProfileId = company.Id,
            DisplayName = "Old roster identity"
        };
        using var ownerClient = fixture.CreateClient(owner.Key);
        using var crafterClient = fixture.CreateClient(crafter.Key);
        using var requesterClient = fixture.CreateClient(requester.Key);
        using var outsiderClient = fixture.CreateClient(outsider.Key);
        await PutCompanyAsync(ownerClient, company);
        using var recordPut = await ownerClient.PutAsJsonAsync(
            $"/profile-host/objects/{ProfileSyncCollections.TradeCrafters}/{record.Id:D}",
            new ProfileSyncPutRequest
            {
                PayloadJson = JsonSerializer.Serialize(record, ProfileSyncJson.CreateOptions()),
                ExpectedRevision = 0
            });
        recordPut.EnsureSuccessStatusCode();
        foreach (var client in new[] { crafterClient, requesterClient })
        {
            using var requested = await client.PostAsJsonAsync(
                $"/trade/v1/companies/{company.Id:D}/membership-requests",
                new MembershipRequestBody(null));
            requested.EnsureSuccessStatusCode();
        }
        using var approved = await ownerClient.PostAsync(
            $"/trade/v1/companies/{company.Id:D}/memberships/{crafter.ProfileId:D}/approve",
            null);
        approved.EnsureSuccessStatusCode();
        using var connected = await ownerClient.PutAsJsonAsync(
            $"/trade/v1/companies/{company.Id:D}/legacy-crafter-bindings/{record.Id:D}",
            new LegacyCrafterBindingBody(crafter.ProfileId));
        connected.EnsureSuccessStatusCode();

        var url = $"/trade/v1/companies/{company.Id:D}/crafter-bindings";
        using var asCrafter = await crafterClient.GetAsync(url);
        using var asOwner = await ownerClient.GetAsync(url);
        using var asRequester = await requesterClient.GetAsync(url);
        using var asOutsider = await outsiderClient.GetAsync(url);
        using var crafterAdminAttempt = await crafterClient.GetAsync(
            $"/trade/v1/companies/{company.Id:D}/legacy-crafter-migration");

        Assert.Equal(HttpStatusCode.OK, asCrafter.StatusCode);
        var body = await asCrafter.Content.ReadAsStringAsync();
        var pair = Assert.Single(JsonSerializer.Deserialize<CompanyCrafterBindingResponse[]>(
            body,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!);
        Assert.Equal(record.Id, pair.LegacyCrafterId);
        Assert.Equal(crafter.ProfileId, pair.AccountProfileId);
        using (var document = JsonDocument.Parse(body))
        {
            Assert.Equal(
                ["accountProfileId", "legacyCrafterId"],
                document.RootElement[0].EnumerateObject().Select(property => property.Name).Order());
        }
        Assert.Equal(HttpStatusCode.OK, asOwner.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, asRequester.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, asOutsider.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, crafterAdminAttempt.StatusCode);
    }

    [Fact]
    public async Task CompanyMemberListIsAvailableOnlyToAdministratorsAndExcludesDiscordIds()
    {
        await using var fixture = await MembershipFixture.CreateAsync();
        var owner = await fixture.CreateAccountAsync("Owner");
        var operatorAccount = await fixture.CreateAccountAsync("Operator");
        var crafter = await fixture.CreateAccountAsync("Crafter");
        var outsider = await fixture.CreateAccountAsync("Outsider");
        var company = CreateCompany();
        using var ownerClient = fixture.CreateClient(owner.Key);
        using var operatorClient = fixture.CreateClient(operatorAccount.Key);
        using var crafterClient = fixture.CreateClient(crafter.Key);
        using var outsiderClient = fixture.CreateClient(outsider.Key);
        await PutCompanyAsync(ownerClient, company);
        await RequestAsync(operatorClient, company.Id, "Operate");
        await RequestAsync(crafterClient, company.Id, "Craft");
        foreach (var profileId in new[] { operatorAccount.ProfileId, crafter.ProfileId })
        {
            using var approved = await ownerClient.PostAsync(
                $"/trade/v1/companies/{company.Id:D}/memberships/{profileId:D}/approve",
                null);
            approved.EnsureSuccessStatusCode();
        }
        await fixture.SetRoleAsync(company.Id, operatorAccount.ProfileId, MembershipRole.Operator);

        var route = $"/trade/v1/companies/{company.Id:D}/memberships";
        using var ownerResponse = await ownerClient.GetAsync(route);
        using var operatorResponse = await operatorClient.GetAsync(route);
        using var crafterResponse = await crafterClient.GetAsync(route);
        using var outsiderResponse = await outsiderClient.GetAsync(route);
        var ownerJson = await ownerResponse.Content.ReadAsStringAsync();
        var members = JsonSerializer.Deserialize<CompanyMemberResponse[]>(
            ownerJson,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var operatorMembers = await operatorResponse.Content.ReadFromJsonAsync<CompanyMemberResponse[]>();
        var discordUserIds = await Task.WhenAll(
            new[] { owner.ProfileId, operatorAccount.ProfileId, crafter.ProfileId }
                .Select(async profileId =>
                    (await fixture.Identities.LoadByProfileAsync(profileId))!.DiscordUserId));

        Assert.Equal(HttpStatusCode.OK, ownerResponse.StatusCode);
        Assert.Equal(3, members!.Length);
        Assert.Equal(3, operatorMembers!.Length);
        Assert.Contains(members, member =>
            member.AccountProfileId == owner.ProfileId &&
            member.DisplayName == "Owner" &&
            member.Role == "owner" &&
            member.State == "active" &&
            member.RequestedAtUtc != default &&
            member.DecidedAtUtc != null &&
            member.DiscordLinked);
        Assert.Contains(members, member =>
            member.AccountProfileId == operatorAccount.ProfileId &&
            member.Role == "operator" &&
            member.DecidedAtUtc != null);
        Assert.All(discordUserIds, discordUserId =>
            Assert.DoesNotContain(discordUserId, ownerJson, StringComparison.Ordinal));
        Assert.DoesNotContain("discordUserId", ownerJson, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.Forbidden, crafterResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, outsiderResponse.StatusCode);
    }

    [Fact]
    public async Task OnlyOwnersCanRevokeAnotherOwner()
    {
        await using var fixture = await MembershipFixture.CreateAsync();
        var owner = await fixture.CreateAccountAsync("Owner");
        var secondOwner = await fixture.CreateAccountAsync("Second owner");
        var operatorAccount = await fixture.CreateAccountAsync("Operator");
        var company = CreateCompany();
        using var ownerClient = fixture.CreateClient(owner.Key);
        using var secondOwnerClient = fixture.CreateClient(secondOwner.Key);
        using var operatorClient = fixture.CreateClient(operatorAccount.Key);
        await PutCompanyAsync(ownerClient, company);
        await RequestAsync(secondOwnerClient, company.Id, string.Empty);
        await RequestAsync(operatorClient, company.Id, string.Empty);
        foreach (var profileId in new[] { secondOwner.ProfileId, operatorAccount.ProfileId })
        {
            (await ownerClient.PostAsync($"/trade/v1/companies/{company.Id:D}/memberships/{profileId:D}/approve", null)).EnsureSuccessStatusCode();
        }
        await fixture.SetRoleAsync(company.Id, secondOwner.ProfileId, MembershipRole.Owner);
        await fixture.SetRoleAsync(company.Id, operatorAccount.ProfileId, MembershipRole.Operator);

        using var refused = await operatorClient.PostAsync(
            $"/trade/v1/companies/{company.Id:D}/memberships/{secondOwner.ProfileId:D}/revoke",
            null);
        using var allowed = await ownerClient.PostAsync(
            $"/trade/v1/companies/{company.Id:D}/memberships/{secondOwner.ProfileId:D}/revoke",
            null);
        using var lastOwner = await ownerClient.PostAsync(
            $"/trade/v1/companies/{company.Id:D}/memberships/{owner.ProfileId:D}/revoke",
            null);

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, lastOwner.StatusCode);
    }

    private static TradeCompanyProfile CreateCompany() =>
        TradeCompanyProfile.CreateLocal("Sapphire Avenue", DateTime.UtcNow);

    private static async Task PutCompanyAsync(
        HttpClient client,
        TradeCompanyProfile company,
        long expectedRevision = 0,
        Guid? objectId = null)
    {
        company.UpdatedAtUtc = DateTime.UtcNow;
        using var response = await client.PutAsJsonAsync(
            $"/profile-host/objects/{ProfileSyncCollections.TradeCompanyProfiles}/{objectId ?? company.Id:D}",
            new ProfileSyncPutRequest
            {
                PayloadJson = JsonSerializer.Serialize(company, ProfileSyncJson.CreateOptions()),
                ExpectedRevision = expectedRevision
            });
        response.EnsureSuccessStatusCode();
    }

    private static async Task PutCompanyExpectingConflictAsync(
        HttpClient client,
        TradeCompanyProfile company,
        Guid? objectId = null)
    {
        company.UpdatedAtUtc = DateTime.UtcNow;
        using var response = await client.PutAsJsonAsync(
            $"/profile-host/objects/{ProfileSyncCollections.TradeCompanyProfiles}/{objectId ?? company.Id:D}",
            new ProfileSyncPutRequest
            {
                PayloadJson = JsonSerializer.Serialize(company, ProfileSyncJson.CreateOptions()),
                ExpectedRevision = 0
            });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    private static async Task<MembershipResponse> RequestAsync(
        HttpClient client,
        Guid companyId,
        string note)
    {
        using var response = await client.PostAsJsonAsync(
            $"/trade/v1/companies/{companyId:D}/membership-requests",
            new MembershipRequestBody(note));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<MembershipResponse>())!;
    }

    private sealed class MembershipFixture : IAsyncDisposable
    {
        private readonly string root;
        private readonly WebApplicationFactory<Program> application;

        private MembershipFixture(
            string root,
            WebApplicationFactory<Program> application,
            Guid? existingProfileId = null,
            Guid? existingCompanyId = null,
            Guid? secondExistingProfileId = null,
            AccountFixture? existingKeyOnlyOwner = null)
        {
            this.root = root;
            this.application = application;
            ExistingProfileId = existingProfileId;
            ExistingCompanyId = existingCompanyId;
            SecondExistingProfileId = secondExistingProfileId;
            ExistingKeyOnlyOwner = existingKeyOnlyOwner;
            Profiles = application.Services.GetRequiredService<SqliteProfileHostStore>();
            Identities = application.Services.GetRequiredService<SqliteDiscordIdentityStore>();
            Notifications = application.Services.GetRequiredService<SqliteDiscordNotificationStore>();
            Memberships = application.Services.GetRequiredService<SqliteMembershipStore>();
            Reconciler = application.Services
                .GetServices<IHostedService>()
                .OfType<FounderMembershipReconciler>()
                .Single();
        }

        public SqliteProfileHostStore Profiles { get; }
        public SqliteDiscordIdentityStore Identities { get; }
        public SqliteMembershipStore Memberships { get; }
        public SqliteDiscordNotificationStore Notifications { get; }
        public FounderMembershipReconciler Reconciler { get; }
        public Guid? ExistingProfileId { get; }
        public Guid? ExistingCompanyId { get; }
        public Guid? SecondExistingProfileId { get; }
        public AccountFixture? ExistingKeyOnlyOwner { get; }

        public static Task<MembershipFixture> CreateAsync(
            int founderReconciliationIntervalSeconds = 300)
        {
            var root = CreateRoot();
            return Task.FromResult(new MembershipFixture(
                root,
                CreateApplication(root, founderReconciliationIntervalSeconds)));
        }

        public static async Task<MembershipFixture> CreateWithAmbiguousCompanyAsync()
        {
            var root = CreateRoot();
            var store = new SqliteProfileHostStore(new ProfileHostOptions
            {
                Enabled = true,
                DatabasePath = Path.Combine(root, "profiles.db")
            });
            var first = await store.CreateProfileAsync("First holder", CancellationToken.None);
            var second = await store.CreateProfileAsync("Second holder", CancellationToken.None);
            var company = CreateCompany();
            foreach (var profileId in new[] { first.ProfileId, second.ProfileId })
            {
                var put = await store.PutObjectAsync(
                    profileId,
                    ProfileSyncCollections.TradeCompanyProfiles,
                    company.Id.ToString("D"),
                    JsonSerializer.Serialize(company, ProfileSyncJson.CreateOptions()),
                    0,
                    CancellationToken.None);
                Assert.True(put.Success);
            }

            return new MembershipFixture(
                root,
                CreateApplication(root),
                Guid.Parse(first.ProfileId),
                company.Id,
                Guid.Parse(second.ProfileId));
        }

        public HttpClient CreateClient(string key)
        {
            var client = application.CreateClient();
            client.DefaultRequestHeaders.Add("X-Profile-Key", key);
            return client;
        }

        public async Task<AccountFixture> CreateAccountAsync(string displayName)
        {
            var account = await CreateKeyOnlyAsync(displayName);
            var linked = await Identities.LinkAsync(
                account.ProfileId,
                $"{100000000000000000L + Math.Abs((long)account.ProfileId.GetHashCode()):D18}",
                displayName,
                DateTimeOffset.UtcNow);
            Assert.Equal(DiscordIdentityLinkResultStatus.Linked, linked.Status);
            return account;
        }

        public async Task<AccountFixture> CreateKeyOnlyAsync(string displayName)
        {
            var hasher = new ProfileAccessKeyHasher();
            var key = hasher.CreateAccessKey();
            var profile = await Profiles.CreateProfileAsync(displayName, CancellationToken.None);
            await Profiles.AddAccessKeyAsync(
                profile.ProfileId,
                key.StoredHash,
                CancellationToken.None);
            return new AccountFixture(Guid.Parse(profile.ProfileId), key.PlaintextKey);
        }

        public async Task SetRoleAsync(Guid companyId, Guid profileId, MembershipRole role)
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
            command.Parameters.AddWithValue("$role", role.ToString().ToLowerInvariant());
            command.Parameters.AddWithValue("$companyId", companyId.ToString("D"));
            command.Parameters.AddWithValue("$profileId", profileId.ToString("D"));
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        public async Task ExpireInvitationAsync(Guid invitationId)
        {
            await using var connection = new SqliteConnection(
                $"Data Source={Path.Combine(root, "memberships.db")}");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE company_membership_invitations
                SET expires_at_utc = $expiresAt
                WHERE invitation_id = $invitationId;
                """;
            command.Parameters.AddWithValue("$expiresAt", DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O"));
            command.Parameters.AddWithValue("$invitationId", invitationId.ToString("D"));
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        public async ValueTask DisposeAsync()
        {
            await application.DisposeAsync();
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }

        private static string CreateRoot()
        {
            var root = Path.Combine(Path.GetTempPath(), $"craft-memberships-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            return root;
        }

        private static WebApplicationFactory<Program> CreateApplication(
            string root,
            int founderReconciliationIntervalSeconds = 300) =>
            new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
                builder.ConfigureAppConfiguration((_, configuration) =>
                    configuration.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ProfileHost:Enabled"] = "true",
                        ["ProfileHost:DatabasePath"] = Path.Combine(root, "profiles.db"),
                        ["TradeMemberships:DatabasePath"] = Path.Combine(root, "memberships.db"),
                        ["TradeMemberships:FounderReconciliationIntervalSeconds"] =
                            founderReconciliationIntervalSeconds.ToString(),
                        ["Discord:Enabled"] = "true",
                        ["Discord:RuntimeBotToken"] = "contract-token",
                        ["Discord:CommissionBaseUrl"] = "https://example.test/commission/",
                        ["Discord:ApiBaseUrl"] = "https://example.test/discord/",
                        ["Discord:DatabasePath"] = Path.Combine(root, "discord-collaboration.db"),
                        ["Discord:OutboxPollSeconds"] = "60"
                    })));
    }

    private sealed record AccountFixture(Guid ProfileId, string Key);
}
