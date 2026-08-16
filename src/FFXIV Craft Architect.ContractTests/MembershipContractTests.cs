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
    public async Task CompanyPutBindsFounderMembershipOnce()
    {
        await using var fixture = await MembershipFixture.CreateAsync();
        var owner = await fixture.CreateAccountAsync("Founder");
        var company = CreateCompany();

        using var client = fixture.CreateClient(owner.Key);
        await PutCompanyAsync(client, company);
        var membership = await fixture.Memberships.LoadAsync(
            new CompanyId(company.Id),
            owner.ProfileId);
        await PutCompanyAsync(client, company, expectedRevision: 1);
        var events = await fixture.Memberships.LoadEventsAsync(
            new CompanyId(company.Id),
            owner.ProfileId);

        Assert.NotNull(membership);
        Assert.Equal(MembershipRole.Owner, membership.Role);
        Assert.Equal(MembershipState.Active, membership.State);
        Assert.Single(events);
    }

    [Fact]
    public async Task NewCompanyRequiresClaimedAccount()
    {
        await using var fixture = await MembershipFixture.CreateAsync();
        var owner = await fixture.CreateKeyOnlyAsync("Key-only creator");
        using var client = fixture.CreateClient(owner.Key);
        using var response = await client.PutAsJsonAsync(
            $"/profile-host/objects/{ProfileSyncCollections.TradeCompanyProfiles}/{Guid.NewGuid():D}",
            new ProfileSyncPutRequest
            {
                PayloadJson = JsonSerializer.Serialize(CreateCompany(), ProfileSyncJson.CreateOptions()),
                ExpectedRevision = 0
            });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("company_account_required", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task StartupReconciliationBindsExistingFounderIdempotently()
    {
        await using var fixture = await MembershipFixture.CreateWithExistingCompanyAsync();
        var companyId = new CompanyId(fixture.ExistingCompanyId!.Value);
        var profileId = fixture.ExistingProfileId!.Value;

        var founder = await fixture.WaitForMembershipAsync(companyId, profileId);
        await fixture.Memberships.EnsureFounderAsync(companyId, profileId);
        var events = await fixture.Memberships.LoadEventsAsync(companyId, profileId);

        Assert.Equal(MembershipRole.Owner, founder.Role);
        Assert.Equal(MembershipState.Active, founder.State);
        Assert.Single(events);
    }

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
    public async Task KeyOnlyAccountMustSignInWithDiscordBeforeRequestingMembership()
    {
        await using var fixture = await MembershipFixture.CreateAsync();
        var owner = await fixture.CreateAccountAsync("Owner");
        var keyOnly = await fixture.CreateKeyOnlyAsync("Key-only account");
        var claimed = await fixture.CreateAccountAsync("Claimed account");
        var company = CreateCompany();
        using var ownerClient = fixture.CreateClient(owner.Key);
        using var keyOnlyClient = fixture.CreateClient(keyOnly.Key);
        using var claimedClient = fixture.CreateClient(claimed.Key);
        await PutCompanyAsync(ownerClient, company);

        using var refused = await keyOnlyClient.PostAsJsonAsync(
            $"/trade/v1/companies/{company.Id:D}/membership-requests",
            new MembershipRequestBody(null));
        using var accepted = await claimedClient.PostAsJsonAsync(
            $"/trade/v1/companies/{company.Id:D}/membership-requests",
            new MembershipRequestBody(null));
        var error = await refused.Content.ReadFromJsonAsync<MembershipErrorResponse>();

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("account_sign_in_required", error!.Error);
        Assert.Equal("Sign in with Discord before requesting membership.", error.Message);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
    }

    [Fact]
    public async Task OperatorCanConnectAndCorrectLegacyCrafterHistoryWithoutRewritingIt()
    {
        await using var fixture = await MembershipFixture.CreateAsync();
        var owner = await fixture.CreateAccountAsync("Owner");
        var returning = await fixture.CreateAccountAsync("Returning crafter");
        var company = CreateCompany();
        var legacyCrafter = new TradeCrafterProfile
        {
            Id = Guid.NewGuid(),
            CompanyProfileId = company.Id,
            DisplayName = "Old roster identity",
            WorldName = "Siren",
            LodestoneCharacterId = "49131404"
        };
        var foreignCrafter = new TradeCrafterProfile
        {
            Id = Guid.NewGuid(),
            CompanyProfileId = company.Id,
            DisplayName = "Injected foreign roster identity"
        };
        using var ownerClient = fixture.CreateClient(owner.Key);
        using var memberClient = fixture.CreateClient(returning.Key);
        await PutCompanyAsync(ownerClient, company);
        using var crafterPut = await ownerClient.PutAsJsonAsync(
            $"/profile-host/objects/{ProfileSyncCollections.TradeCrafters}/{legacyCrafter.Id:D}",
            new ProfileSyncPutRequest
            {
                PayloadJson = JsonSerializer.Serialize(legacyCrafter, ProfileSyncJson.CreateOptions()),
                ExpectedRevision = 0
            });
        crafterPut.EnsureSuccessStatusCode();
        using var foreignCrafterPut = await memberClient.PutAsJsonAsync(
            $"/profile-host/objects/{ProfileSyncCollections.TradeCrafters}/{foreignCrafter.Id:D}",
            new ProfileSyncPutRequest
            {
                PayloadJson = JsonSerializer.Serialize(foreignCrafter, ProfileSyncJson.CreateOptions()),
                ExpectedRevision = 0
            });
        foreignCrafterPut.EnsureSuccessStatusCode();
        using var requested = await memberClient.PostAsJsonAsync(
            $"/trade/v1/companies/{company.Id:D}/membership-requests",
            new MembershipRequestBody(null));
        requested.EnsureSuccessStatusCode();

        using var discovered = await ownerClient.GetAsync(
            $"/trade/v1/companies/{company.Id:D}/legacy-crafter-migration");
        var migration = await discovered.Content
            .ReadFromJsonAsync<LegacyCrafterMigrationResponse>();
        using var connected = await ownerClient.PutAsJsonAsync(
            $"/trade/v1/companies/{company.Id:D}/legacy-crafter-bindings/{legacyCrafter.Id:D}",
            new LegacyCrafterBindingBody(returning.ProfileId));
        using var replayed = await ownerClient.PutAsJsonAsync(
            $"/trade/v1/companies/{company.Id:D}/legacy-crafter-bindings/{legacyCrafter.Id:D}",
            new LegacyCrafterBindingBody(returning.ProfileId));
        using var approved = await ownerClient.PostAsync(
            $"/trade/v1/companies/{company.Id:D}/memberships/{returning.ProfileId:D}/approve",
            null);
        var bindings = await fixture.Memberships.LoadCrafterBindingsAsync(
            new CompanyId(company.Id));
        using var disconnected = await ownerClient.DeleteAsync(
            $"/trade/v1/companies/{company.Id:D}/legacy-crafter-bindings/{legacyCrafter.Id:D}");

        Assert.Equal(legacyCrafter.Id, Assert.Single(migration!.LegacyCrafters).LegacyCrafterId);
        Assert.Equal(HttpStatusCode.OK, connected.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replayed.StatusCode);
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        Assert.Single(bindings);
        Assert.Equal(HttpStatusCode.NoContent, disconnected.StatusCode);
        Assert.Empty(await fixture.Memberships.LoadCrafterBindingsAsync(new CompanyId(company.Id)));
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
    public async Task DenyRerequestApproveAndRevokePersistEveryAuditTransition()
    {
        await using var fixture = await MembershipFixture.CreateAsync();
        var owner = await fixture.CreateAccountAsync("Owner");
        var crafter = await fixture.CreateAccountAsync("Crafter");
        var company = CreateCompany();
        var companyId = new CompanyId(company.Id);
        using var ownerClient = fixture.CreateClient(owner.Key);
        using var crafterClient = fixture.CreateClient(crafter.Key);
        await PutCompanyAsync(ownerClient, company);

        var first = await RequestAsync(crafterClient, company.Id, "First request");
        var duplicate = await RequestAsync(crafterClient, company.Id, "Ignored duplicate");
        using var denied = await ownerClient.PostAsync(
            $"/trade/v1/companies/{company.Id:D}/memberships/{crafter.ProfileId:D}/deny",
            null);
        var second = await RequestAsync(crafterClient, company.Id, "Second request");
        using var approved = await ownerClient.PostAsync(
            $"/trade/v1/companies/{company.Id:D}/memberships/{crafter.ProfileId:D}/approve",
            null);
        using var revoked = await ownerClient.PostAsJsonAsync(
            $"/trade/v1/companies/{company.Id:D}/memberships/{crafter.ProfileId:D}/revoke",
            new MembershipTransitionBody("  Repeated missed handoffs  "));
        var events = await fixture.Memberships.LoadEventsAsync(companyId, crafter.ProfileId);
        var current = await fixture.Memberships.LoadAsync(companyId, crafter.ProfileId);

        Assert.Equal(first.RequestedAtUtc, duplicate.RequestedAtUtc);
        Assert.True(second.RequestedAtUtc >= first.RequestedAtUtc);
        Assert.Equal(HttpStatusCode.OK, denied.StatusCode);
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);
        Assert.Equal(MembershipState.Revoked, current!.State);
        Assert.Equal(
            new[]
            {
                MembershipState.Pending,
                MembershipState.Denied,
                MembershipState.Pending,
                MembershipState.Active,
                MembershipState.Revoked
            },
            events.Select(item => item.ToState));
        Assert.Equal(crafter.ProfileId, events[0].ActorProfileId);
        Assert.Equal(owner.ProfileId, events[^1].ActorProfileId);
        Assert.Equal(
            new[]
            {
                "First request",
                "First request",
                "Second request",
                "Second request",
                "Second request"
            },
            events.Select(item => item.RequestNote));
        Assert.All(events, item => Assert.Equal(MembershipRole.Crafter, item.Role));
        Assert.All(events, item => Assert.NotNull(item.RequestedAtUtc));
        Assert.Null(events[0].DecidedAtUtc);
        Assert.Null(events[0].DecidedByProfileId);
        Assert.Equal(owner.ProfileId, events[1].DecidedByProfileId);
        Assert.Null(events[2].DecidedAtUtc);
        Assert.Null(events[2].DecidedByProfileId);
        Assert.Equal(owner.ProfileId, events[3].DecidedByProfileId);
        Assert.Equal(owner.ProfileId, events[4].DecidedByProfileId);
        Assert.Equal("Repeated missed handoffs", events[4].Reason);
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

    [Fact]
    public async Task RetriedMembershipTransitionsReplayWithoutDuplicateEvents()
    {
        await using var fixture = await MembershipFixture.CreateAsync();
        var owner = await fixture.CreateAccountAsync("Owner");
        var approvedCrafter = await fixture.CreateAccountAsync("Approved crafter");
        var deniedCrafter = await fixture.CreateAccountAsync("Denied crafter");
        var company = CreateCompany();
        var companyId = new CompanyId(company.Id);
        using var ownerClient = fixture.CreateClient(owner.Key);
        using var approvedClient = fixture.CreateClient(approvedCrafter.Key);
        using var deniedClient = fixture.CreateClient(deniedCrafter.Key);
        await PutCompanyAsync(ownerClient, company);
        await RequestAsync(approvedClient, company.Id, "Approve me");
        await RequestAsync(deniedClient, company.Id, "Deny me");

        await AssertSuccessfulReplayAsync(
            ownerClient,
            company.Id,
            approvedCrafter.ProfileId,
            "approve");
        await AssertSuccessfulReplayAsync(
            ownerClient,
            company.Id,
            approvedCrafter.ProfileId,
            "revoke");
        await AssertSuccessfulReplayAsync(
            ownerClient,
            company.Id,
            deniedCrafter.ProfileId,
            "deny");
        var approvedEvents = await fixture.Memberships.LoadEventsAsync(
            companyId,
            approvedCrafter.ProfileId);
        var deniedEvents = await fixture.Memberships.LoadEventsAsync(
            companyId,
            deniedCrafter.ProfileId);

        Assert.Equal(3, approvedEvents.Count);
        Assert.Equal(2, deniedEvents.Count);
    }

    [Fact]
    public async Task MemberNotificationCategoriesPersistIndependentlyAndRevokeRefusesAccess()
    {
        await using var fixture = await MembershipFixture.CreateAsync();
        var owner = await fixture.CreateAccountAsync("Owner");
        var member = await fixture.CreateAccountAsync("Member");
        var company = CreateCompany();
        using var ownerClient = fixture.CreateClient(owner.Key);
        using var memberClient = fixture.CreateClient(member.Key);
        await PutCompanyAsync(ownerClient, company);
        await RequestAsync(memberClient, company.Id, "Commission updates please");
        using var approved = await ownerClient.PostAsync(
            $"/trade/v1/companies/{company.Id:D}/memberships/{member.ProfileId:D}/approve",
            null);
        approved.EnsureSuccessStatusCode();

        using var changed = await memberClient.PutAsJsonAsync(
            $"/trade/v1/companies/{company.Id:D}/membership-notifications",
            new
            {
                ActionRequired = true,
                CommissionerMessages = false,
                ProgressAndStatus = true
            });
        changed.EnsureSuccessStatusCode();
        using var loaded = await memberClient.GetAsync(
            $"/trade/v1/companies/{company.Id:D}/membership-notifications");
        var preferences = await loaded.Content.ReadFromJsonAsync<
            MembershipNotificationPreferenceResponse>();

        Assert.True(preferences!.ActionRequired);
        Assert.False(preferences.CommissionerMessages);
        Assert.True(preferences.ProgressAndStatus);

        using var revoked = await ownerClient.PostAsync(
            $"/trade/v1/companies/{company.Id:D}/memberships/{member.ProfileId:D}/revoke",
            null);
        revoked.EnsureSuccessStatusCode();
        using var deniedRead = await memberClient.GetAsync(
            $"/trade/v1/companies/{company.Id:D}/membership-notifications");
        using var deniedWrite = await memberClient.PutAsJsonAsync(
            $"/trade/v1/companies/{company.Id:D}/membership-notifications",
            new
            {
                ActionRequired = true,
                CommissionerMessages = true,
                ProgressAndStatus = true
            });

        Assert.Equal(HttpStatusCode.NotFound, deniedRead.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, deniedWrite.StatusCode);
    }

    [Fact]
    public async Task MemberNotificationTestRequiresCanonicalReadinessAndQueuesNoCommissionMutation()
    {
        await using var fixture = await MembershipFixture.CreateAsync();
        var owner = await fixture.CreateAccountAsync("Owner");
        var member = await fixture.CreateAccountAsync("Member destination");
        var company = CreateCompany();
        using var ownerClient = fixture.CreateClient(owner.Key);
        using var memberClient = fixture.CreateClient(member.Key);
        await PutCompanyAsync(ownerClient, company);
        await RequestAsync(memberClient, company.Id, string.Empty);
        using var approved = await ownerClient.PostAsync(
            $"/trade/v1/companies/{company.Id:D}/memberships/{member.ProfileId:D}/approve",
            null);
        approved.EnsureSuccessStatusCode();

        var route = await fixture.Notifications.PutRouteAsync(
            new CompanyId(company.Id),
            new DiscordNotificationRouteUpdate(
                "100000000000000001",
                DiscordNotificationDestinationMode.CommissionerDirectMessage,
                null,
                DiscordDirectMessageFallback.None,
                DiscordNotificationMentionBehavior.NoPing,
                DiscordNotificationMentionBehavior.Push,
                DiscordNotificationMentionBehavior.Push,
                0,
                $"member-test-{Guid.NewGuid():N}"),
            DateTimeOffset.UtcNow);
        Assert.True(route.Success, route.Error);

        var readiness = await memberClient.GetFromJsonAsync<
            MembershipNotificationTestReadinessResponse>(
            $"/trade/v1/companies/{company.Id:D}/membership-notifications/test-readiness");
        Assert.True(readiness!.Ready);
        Assert.Equal("Member destination", readiness.DestinationDisplayName);

        using var sent = await memberClient.PostAsync(
            $"/trade/v1/companies/{company.Id:D}/membership-notifications/test",
            null);
        Assert.Equal(HttpStatusCode.Accepted, sent.StatusCode);
        var delivery = await sent.Content.ReadFromJsonAsync<MembershipNotificationTestResponse>();
        Assert.NotNull(delivery);
        Assert.Equal("Member destination", delivery.DestinationDisplayName);
        var stored = await fixture.Notifications.LoadMemberTestDeliveryAsync(
            new CompanyId(company.Id),
            delivery.TestId,
            await fixture.LoadDiscordUserIdAsync(member.ProfileId));
        Assert.NotNull(stored);
        Assert.Equal(DiscordOutboxState.Pending, stored.State);

        using var revoked = await ownerClient.PostAsync(
            $"/trade/v1/companies/{company.Id:D}/memberships/{member.ProfileId:D}/revoke",
            null);
        revoked.EnsureSuccessStatusCode();
        using var denied = await memberClient.GetAsync(
            $"/trade/v1/companies/{company.Id:D}/membership-notifications/test/{delivery.TestId:D}");
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
    }

    [Fact]
    public async Task PeriodicReconciliationRepairsMissedFounderBinding()
    {
        await using var fixture = await MembershipFixture.CreateAsync(
            founderReconciliationIntervalSeconds: 1);
        var owner = await fixture.CreateAccountAsync("Owner");
        var company = CreateCompany();
        var companyId = new CompanyId(company.Id);
        using var client = fixture.CreateClient(owner.Key);
        await PutCompanyAsync(client, company);
        await fixture.DeleteMembershipsAsync(company.Id);

        var repaired = await fixture.WaitForMembershipAsync(companyId, owner.ProfileId);

        Assert.Equal(MembershipRole.Owner, repaired.Role);
        Assert.Equal(MembershipState.Active, repaired.State);
    }

    [Fact]
    public async Task LastActiveOwnerCannotBeRevoked()
    {
        await using var fixture = await MembershipFixture.CreateAsync();
        var owner = await fixture.CreateAccountAsync("Owner");
        var company = CreateCompany();
        var companyId = new CompanyId(company.Id);
        using var client = fixture.CreateClient(owner.Key);
        await PutCompanyAsync(client, company);

        using var response = await client.PostAsync(
            $"/trade/v1/companies/{company.Id:D}/memberships/{owner.ProfileId:D}/revoke",
            null);
        var membership = await fixture.Memberships.LoadAsync(companyId, owner.ProfileId);
        var error = await response.Content.ReadFromJsonAsync<MembershipErrorResponse>();

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("last_owner", error!.Error);
        Assert.Equal(MembershipState.Active, membership!.State);
    }

    [Fact]
    public async Task KeyOnlyHoldingProfileRetainsOwnerAccessWithoutMembershipRows()
    {
        await using var fixture = await MembershipFixture.CreateWithExistingKeyOnlyCompanyAsync();
        var owner = fixture.ExistingKeyOnlyOwner!;
        var company = CreateCompany();
        company.Id = fixture.ExistingCompanyId!.Value;
        using var client = fixture.CreateClient(owner.Key);
        await PutCompanyAsync(client, company, expectedRevision: 1);
        await fixture.DeleteMembershipsAsync(company.Id);

        using var response = await client.GetAsync(
            $"/trade/v1/companies/{company.Id:D}/membership-requests");
        var membership = await fixture.Memberships.LoadAsync(
            new CompanyId(company.Id),
            owner.ProfileId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(membership);
        Assert.Empty((await response.Content.ReadFromJsonAsync<MembershipResponse[]>())!);
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

    private static async Task AssertSuccessfulReplayAsync(
        HttpClient client,
        Guid companyId,
        Guid accountProfileId,
        string action)
    {
        var route = $"/trade/v1/companies/{companyId:D}/memberships/{accountProfileId:D}/{action}";
        using var first = await client.PostAsync(route, null);
        using var replay = await client.PostAsync(route, null);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(
            await first.Content.ReadAsStringAsync(),
            await replay.Content.ReadAsStringAsync());
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

        public static async Task<MembershipFixture> CreateWithExistingCompanyAsync()
        {
            var root = CreateRoot();
            var store = new SqliteProfileHostStore(new ProfileHostOptions
            {
                Enabled = true,
                DatabasePath = Path.Combine(root, "profiles.db")
            });
            var profile = await store.CreateProfileAsync("Existing owner", CancellationToken.None);
            var company = CreateCompany();
            var put = await store.PutObjectAsync(
                profile.ProfileId,
                ProfileSyncCollections.TradeCompanyProfiles,
                company.Id.ToString("D"),
                JsonSerializer.Serialize(company, ProfileSyncJson.CreateOptions()),
                0,
                CancellationToken.None);
            Assert.True(put.Success);
            return new MembershipFixture(
                root,
                CreateApplication(root),
                Guid.Parse(profile.ProfileId),
                company.Id);
        }

        public static async Task<MembershipFixture> CreateWithExistingKeyOnlyCompanyAsync()
        {
            var root = CreateRoot();
            var store = new SqliteProfileHostStore(new ProfileHostOptions
            {
                Enabled = true,
                DatabasePath = Path.Combine(root, "profiles.db")
            });
            var hasher = new ProfileAccessKeyHasher();
            var key = hasher.CreateAccessKey();
            var profile = await store.CreateProfileAsync("Key-only owner", CancellationToken.None);
            await store.AddAccessKeyAsync(profile.ProfileId, key.StoredHash, CancellationToken.None);
            var company = CreateCompany();
            var put = await store.PutObjectAsync(
                profile.ProfileId,
                ProfileSyncCollections.TradeCompanyProfiles,
                company.Id.ToString("D"),
                JsonSerializer.Serialize(company, ProfileSyncJson.CreateOptions()),
                0,
                CancellationToken.None);
            Assert.True(put.Success);
            return new MembershipFixture(
                root,
                CreateApplication(root),
                Guid.Parse(profile.ProfileId),
                company.Id,
                existingKeyOnlyOwner: new AccountFixture(Guid.Parse(profile.ProfileId), key.PlaintextKey));
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

        public async Task<string> LoadDiscordUserIdAsync(Guid profileId) =>
            (await Identities.LoadByProfileAsync(profileId))!.DiscordUserId;

        public async Task<CompanyMembership> WaitForMembershipAsync(
            CompanyId companyId,
            Guid profileId)
        {
            for (var attempt = 0; attempt < 150; attempt++)
            {
                var membership = await Memberships.LoadAsync(companyId, profileId);
                if (membership != null)
                {
                    return membership;
                }
                await Task.Delay(20);
            }
            throw new TimeoutException("Founder membership reconciliation did not complete.");
        }

        public async Task DeleteMembershipsAsync(Guid companyId)
        {
            await using var connection = new SqliteConnection(
                $"Data Source={Path.Combine(root, "memberships.db")}");
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await using (var events = connection.CreateCommand())
            {
                events.Transaction = (SqliteTransaction)transaction;
                events.CommandText = "DELETE FROM membership_events WHERE company_id = $companyId;";
                events.Parameters.AddWithValue("$companyId", companyId.ToString("D"));
                await events.ExecuteNonQueryAsync();
            }
            await using (var memberships = connection.CreateCommand())
            {
                memberships.Transaction = (SqliteTransaction)transaction;
                memberships.CommandText = "DELETE FROM company_memberships WHERE company_id = $companyId;";
                memberships.Parameters.AddWithValue("$companyId", companyId.ToString("D"));
                await memberships.ExecuteNonQueryAsync();
            }
            await transaction.CommitAsync();
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
