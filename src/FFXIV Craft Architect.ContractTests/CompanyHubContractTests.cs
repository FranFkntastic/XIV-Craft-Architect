using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.LodestoneLookup.Services.Identity;
using FFXIV_Craft_Architect.LodestoneLookup.Services.ProfileHosting;
using FFXIV_Craft_Architect.LodestoneLookup.Services.TradeCompanies;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FFXIV_Craft_Architect.ContractTests;

public sealed class CompanyHubContractTests
{
    [Fact]
    public async Task OperatorLoadsExactHostedWorkspaceProfileButCrafterCannot()
    {
        await using var fixture = await HubFixture.CreateAsync();
        var owner = await fixture.CreateAccountAsync("Owner");
        var operatorAccount = await fixture.CreateAccountAsync("Operator");
        var crafter = await fixture.CreateAccountAsync("Crafter");
        var company = CreateCompany();
        company.CommissionContact = "riviene-cahernaut";
        company.PaymentPolicy = new TradePaymentPolicy(
            TradePaymentContractMode.LaborStandard,
            0.17m,
            425m);
        using var ownerClient = fixture.CreateClient(owner.Key);
        using var operatorClient = fixture.CreateClient(operatorAccount.Key);
        using var crafterClient = fixture.CreateClient(crafter.Key);
        await PutCompanyAsync(ownerClient, company);
        await RequestAsync(operatorClient, company.Id);
        await RequestAsync(crafterClient, company.Id);
        using (var approved = await ownerClient.PostAsync(
                   $"/trade/v1/companies/{company.Id:D}/memberships/{operatorAccount.ProfileId:D}/approve",
                   null))
        {
            approved.EnsureSuccessStatusCode();
        }
        using (var approved = await ownerClient.PostAsync(
                   $"/trade/v1/companies/{company.Id:D}/memberships/{crafter.ProfileId:D}/approve",
                   null))
        {
            approved.EnsureSuccessStatusCode();
        }
        await fixture.SetMembershipRoleAsync(
            company.Id,
            operatorAccount.ProfileId,
            "operator");

        using var operatorResponse = await operatorClient.GetAsync(
            $"/trade/v1/companies/{company.Id:D}/workspace-profile");
        using var crafterResponse = await crafterClient.GetAsync(
            $"/trade/v1/companies/{company.Id:D}/workspace-profile");
        var profile = await operatorResponse.Content.ReadFromJsonAsync<
            TradeCompanyWorkspaceProfileResponse>();

        Assert.Equal(HttpStatusCode.OK, operatorResponse.StatusCode);
        Assert.Equal(company.Id, profile?.Id);
        Assert.Equal(company.Name, profile?.Name);
        Assert.Equal(company.CommissionContact, profile?.CommissionContact);
        Assert.Equal(company.PaymentPolicy, profile?.PaymentPolicy);
        Assert.Equal(HttpStatusCode.Unauthorized, crafterResponse.StatusCode);
    }

    [Fact]
    public async Task OperatorAdoptsExactSynchronizedDraftOnceAndCannotOverwriteCanonicalOrder()
    {
        await using var fixture = await HubFixture.CreateAsync();
        var owner = await fixture.CreateAccountAsync("Owner");
        var operatorAccount = await fixture.CreateAccountAsync("Operator");
        var company = CreateCompany();
        using var ownerClient = fixture.CreateClient(owner.Key);
        using var operatorClient = fixture.CreateClient(operatorAccount.Key);
        await PutCompanyAsync(ownerClient, company);
        await RequestAsync(operatorClient, company.Id);
        using (var approved = await ownerClient.PostAsync(
                   $"/trade/v1/companies/{company.Id:D}/memberships/{operatorAccount.ProfileId:D}/approve",
                   null))
        {
            approved.EnsureSuccessStatusCode();
        }
        await fixture.SetMembershipRoleAsync(
            company.Id,
            operatorAccount.ProfileId,
            "operator");

        var planId = Guid.NewGuid().ToString("D");
        var savedAt = DateTime.UtcNow;
        var draft = new TradeOrder
        {
            CompanyProfileId = company.Id,
            Title = "Treated Spruce Lumber x1998",
            Status = TradeOrderStatus.Draft,
            CraftPlanId = planId,
            CraftPlanSavedAtUtc = savedAt,
            CraftPlanLinkKind = TradeOrderCraftPlanLinkKind.OrderGenerated
        };
        using (var planPut = await operatorClient.PutAsJsonAsync(
                   $"/profile-host/objects/{ProfileSyncCollections.Plans}/{planId}",
                   new ProfileSyncPutRequest
                   {
                       PayloadJson = ProfileSyncPlanPayloadCodec.Serialize(new ProfileSyncPlanSnapshot
                       {
                           Id = planId,
                           SavedAt = savedAt,
                           LinkedOrderId = draft.Id,
                           PlanJson = "{\"recipe\":true}"
                       }),
                       ExpectedRevision = 0
                   }))
        {
            planPut.EnsureSuccessStatusCode();
        }
        using var sourcePut = await operatorClient.PutAsJsonAsync(
            $"/profile-host/objects/{ProfileSyncCollections.TradeOrders}/{draft.Id:D}",
            new ProfileSyncPutRequest
            {
                PayloadJson = JsonSerializer.Serialize(draft, ProfileSyncJson.CreateOptions()),
                ExpectedRevision = 0
            });
        sourcePut.EnsureSuccessStatusCode();
        var source = await sourcePut.Content.ReadFromJsonAsync<ProfileSyncPutResponse>();
        var adoptionBody = new TradeCompanyOrderAdoptionRequest(
            new CompanyRecordRevision(source!.Object!.Revision),
            $"adopt-order:{draft.Id:D}:{source.Object.Revision}");

        using var first = await operatorClient.PostAsJsonAsync(
            $"/trade/v1/companies/{company.Id:D}/orders/{draft.Id:D}/adopt",
            adoptionBody);
        using var replay = await operatorClient.PostAsJsonAsync(
            $"/trade/v1/companies/{company.Id:D}/orders/{draft.Id:D}/adopt",
            adoptionBody);
        var firstResult = await first.Content.ReadFromJsonAsync<
            TradeCompanyOrderAdoptionResponse>();
        var replayResult = await replay.Content.ReadFromJsonAsync<
            TradeCompanyOrderAdoptionResponse>();

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(firstResult?.OrderRecord.RecordRevision, replayResult?.OrderRecord.RecordRevision);
        Assert.Equal(draft.Title, JsonSerializer.Deserialize<TradeOrder>(
            firstResult!.OrderRecord.PayloadJson,
            ProfileSyncJson.CreateOptions())?.Title);
        var canonicalPlan = await fixture.Profiles.LoadObjectAsync(
            owner.ProfileId.ToString("D"),
            ProfileSyncCollections.Plans,
            planId,
            CancellationToken.None);
        Assert.NotNull(canonicalPlan);
        Assert.Equal(
            draft.Id,
            ProfileSyncPlanPayloadCodec.Deserialize(
                canonicalPlan!.PayloadJson,
                planId).LinkedOrderId);

        draft.Title = "Conflicting replacement";
        using var changedPut = await operatorClient.PutAsJsonAsync(
            $"/profile-host/objects/{ProfileSyncCollections.TradeOrders}/{draft.Id:D}",
            new ProfileSyncPutRequest
            {
                PayloadJson = JsonSerializer.Serialize(draft, ProfileSyncJson.CreateOptions()),
                ExpectedRevision = source.Object.Revision
            });
        changedPut.EnsureSuccessStatusCode();
        var changed = await changedPut.Content.ReadFromJsonAsync<ProfileSyncPutResponse>();
        using var conflict = await operatorClient.PostAsJsonAsync(
            $"/trade/v1/companies/{company.Id:D}/orders/{draft.Id:D}/adopt",
            new TradeCompanyOrderAdoptionRequest(
                new CompanyRecordRevision(changed!.Object!.Revision),
                $"adopt-order:{draft.Id:D}:{changed.Object.Revision}"));

        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        var canonical = await fixture.Profiles.LoadObjectAsync(
            owner.ProfileId.ToString("D"),
            ProfileSyncCollections.TradeOrders,
            draft.Id.ToString("D"),
            CancellationToken.None);
        Assert.Equal("Treated Spruce Lumber x1998", JsonSerializer.Deserialize<TradeOrder>(
            canonical!.PayloadJson,
            ProfileSyncJson.CreateOptions())?.Title);

        var canonicalOrder = JsonSerializer.Deserialize<TradeOrder>(
            canonical.PayloadJson,
            ProfileSyncJson.CreateOptions())!;
        canonicalOrder.Title = "Canonical company update";
        var access = new TradeCompanyAccessContext(
            new CompanyId(company.Id),
            operatorAccount.ProfileId,
            TradeCompanyRole.Operator,
            owner.ProfileId);
        var companyUpdate = await fixture.Companies.PutRecordAsync(
            access,
            TradeCompanyRecordKinds.Order,
            draft.Id.ToString("D"),
            JsonSerializer.Serialize(canonicalOrder, ProfileSyncJson.CreateOptions()),
            firstResult.OrderRecord.RecordRevision,
            $"canonical-update:{draft.Id:D}");
        Assert.True(companyUpdate.Success);
        var mirroredRevision = await fixture.Companies.MirrorOrderToGrantAsync(
            access,
            canonicalOrder);
        var mirrored = await fixture.Profiles.LoadObjectAsync(
            operatorAccount.ProfileId.ToString("D"),
            ProfileSyncCollections.TradeOrders,
            draft.Id.ToString("D"),
            CancellationToken.None);

        Assert.True(mirroredRevision.Value > changed.Object.Revision);
        Assert.Equal("Canonical company update", JsonSerializer.Deserialize<TradeOrder>(
            mirrored!.PayloadJson,
            ProfileSyncJson.CreateOptions())?.Title);
    }

    [Theory]
    [InlineData("anonymous")]
    [InlineData("non-member")]
    [InlineData("pending")]
    public async Task TeaserWhitelistsOnlyLandingFields(string viewer)
    {
        await using var fixture = await HubFixture.CreateAsync();
        var owner = await fixture.CreateAccountAsync("Owner");
        var company = CreateCompany(showCount: true);
        using var ownerClient = fixture.CreateClient(owner.Key);
        await PutCompanyAsync(ownerClient, company);
        await PutOrderAsync(ownerClient, CreateOrder(company.Id));
        HttpClient client;
        if (viewer == "anonymous")
        {
            client = fixture.Application.CreateClient();
        }
        else
        {
            var account = await fixture.CreateAccountAsync(viewer);
            client = fixture.CreateClient(account.Key);
            if (viewer == "pending")
            {
                using var requested = await client.PostAsJsonAsync(
                    $"/trade/v1/companies/{company.Id:D}/membership-requests",
                    new MembershipRequestBody(null));
                requested.EnsureSuccessStatusCode();
            }
        }

        using var response = await client.GetAsync("/trade/v1/companies/sapphire-avenue/hub");
        var json = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"kind\":\"teaser\"", json);
        Assert.DoesNotContain("openCommissions", json, StringComparison.Ordinal);
        Assert.DoesNotContain("assignments", json, StringComparison.Ordinal);
        Assert.DoesNotContain("roster", json, StringComparison.Ordinal);
        Assert.DoesNotContain("output", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("payment", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("termsVersion", json, StringComparison.Ordinal);
        Assert.DoesNotContain("deliveryInstructions", json, StringComparison.Ordinal);
        Assert.DoesNotContain("publicBriefId", json, StringComparison.Ordinal);
        Assert.DoesNotContain("projectionRevision", json, StringComparison.Ordinal);
        Assert.DoesNotContain("completedQuantity", json, StringComparison.Ordinal);
        Assert.DoesNotContain("readyQuantity", json, StringComparison.Ordinal);
        Assert.DoesNotContain("acceptedQuantity", json, StringComparison.Ordinal);
        Assert.DoesNotContain("settlementState", json, StringComparison.Ordinal);
        Assert.DoesNotContain("profileRevision", json, StringComparison.Ordinal);
        Assert.DoesNotContain("updates", json, StringComparison.Ordinal);
        Assert.DoesNotContain("canReportProgress", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TeaserShowsOpenCountOnlyWhenEnabled()
    {
        await using var fixture = await HubFixture.CreateAsync();
        var owner = await fixture.CreateAccountAsync("Owner");
        var hidden = CreateCompany(showCount: false);
        var shown = CreateCompany("Count visible", showCount: true);
        using var client = fixture.CreateClient(owner.Key);
        await PutCompanyAsync(client, hidden);
        await PutCompanyAsync(client, shown);
        await PutOrderAsync(client, CreateOrder(hidden.Id));
        await PutOrderAsync(client, CreateOrder(shown.Id));

        using var hiddenResponse = await fixture.Application.CreateClient().GetAsync(
            "/trade/v1/companies/sapphire-avenue/hub");
        using var shownResponse = await fixture.Application.CreateClient().GetAsync(
            "/trade/v1/companies/count-visible/hub");
        using var hiddenJson = JsonDocument.Parse(await hiddenResponse.Content.ReadAsStringAsync());
        using var shownJson = JsonDocument.Parse(await shownResponse.Content.ReadAsStringAsync());

        Assert.Equal(JsonValueKind.Null, hiddenJson.RootElement.GetProperty("openCommissionCount").ValueKind);
        Assert.Equal(1, shownJson.RootElement.GetProperty("openCommissionCount").GetInt32());
    }

    [Fact]
    public async Task ActiveMemberGetsHubAndOwnerGetsPendingCount()
    {
        await using var fixture = await HubFixture.CreateAsync();
        var owner = await fixture.CreateAccountAsync("Owner");
        var member = await fixture.CreateAccountAsync("Member");
        var pending = await fixture.CreateAccountAsync("Pending");
        var company = CreateCompany();
        company.Updates =
        [
            new TradeCompanyUpdate
            {
                Title = "Workshop schedule",
                Body = "Please finish starred work **first**.",
                AuthorDisplayName = "Owner",
                PublishedAtUtc = DateTime.UtcNow,
                IsPinned = true
            }
        ];
        using var ownerClient = fixture.CreateClient(owner.Key);
        using var memberClient = fixture.CreateClient(member.Key);
        using var pendingClient = fixture.CreateClient(pending.Key);
        await PutCompanyAsync(ownerClient, company);
        await PutOrderAsync(ownerClient, CreateOrder(company.Id));
        await PutOrderAsync(ownerClient, CreateAssignedOrder(company.Id, member.ProfileId));
        await RequestAsync(memberClient, company.Id);
        await RequestAsync(pendingClient, company.Id);
        using var approved = await ownerClient.PostAsync(
            $"/trade/v1/companies/{company.Id:D}/memberships/{member.ProfileId:D}/approve",
            null);
        approved.EnsureSuccessStatusCode();

        using var memberResponse = await memberClient.GetAsync("/trade/v1/companies/sapphire-avenue/hub");
        using var ownerResponse = await ownerClient.GetAsync("/trade/v1/companies/sapphire-avenue/hub");
        using var memberJson = JsonDocument.Parse(await memberResponse.Content.ReadAsStringAsync());
        using var ownerJson = JsonDocument.Parse(await ownerResponse.Content.ReadAsStringAsync());

        Assert.Equal("hub", memberJson.RootElement.GetProperty("kind").GetString());
        Assert.Equal(1, memberJson.RootElement.GetProperty("profileRevision").GetInt64());
        var update = Assert.Single(memberJson.RootElement.GetProperty("updates").EnumerateArray());
        Assert.Equal("Workshop schedule", update.GetProperty("title").GetString());
        Assert.True(update.GetProperty("isPinned").GetBoolean());
        var commission = Assert.Single(memberJson.RootElement.GetProperty("openCommissions").EnumerateArray());
        Assert.Equal(1, commission.GetProperty("termsVersion").GetInt32());
        Assert.Equal("Deliver to the workshop.", commission.GetProperty("deliveryInstructions").GetString());
        Assert.Equal("brief", commission.GetProperty("publicBriefId").GetString());
        Assert.Equal(7, commission.GetProperty("projectionRevision").GetInt64());
        Assert.Equal("notdue", commission.GetProperty("settlementState").GetString());
        var output = Assert.Single(commission.GetProperty("outputs").EnumerateArray());
        Assert.Equal(80, output.GetProperty("completedQuantity").GetInt32());
        Assert.Equal(60, output.GetProperty("readyQuantity").GetInt32());
        Assert.Equal(40, output.GetProperty("acceptedQuantity").GetInt32());
        Assert.False(commission.GetProperty("canWork").GetBoolean());
        Assert.False(commission.GetProperty("canReportProgress").GetBoolean());
        Assert.Equal(JsonValueKind.Null, commission.GetProperty("workBlockedReason").ValueKind);
        var assignment = Assert.Single(memberJson.RootElement.GetProperty("assignments").EnumerateArray());
        Assert.True(assignment.GetProperty("canWork").GetBoolean());
        Assert.True(assignment.GetProperty("canReportProgress").GetBoolean());
        Assert.False(assignment.GetProperty("canDeclareReadiness").GetBoolean());
        Assert.Equal(JsonValueKind.Null, assignment.GetProperty("workBlockedReason").ValueKind);
        Assert.Equal("hub", ownerJson.RootElement.GetProperty("kind").GetString());
        Assert.Equal(1, ownerJson.RootElement.GetProperty("pendingMembershipRequestCount").GetInt32());
    }

    [Fact]
    public async Task DiscordLinkedAssignmentResolvesFromGuidLinkOnlyForCurrentParticipant()
    {
        await using var fixture = await HubFixture.CreateAsync();
        var owner = await fixture.CreateAccountAsync("Owner");
        var assigned = await fixture.CreateAccountAsync("Assigned crafter");
        var other = await fixture.CreateAccountAsync("Other crafter");
        var company = CreateCompany();
        var order = CreateAssignedOrder(company.Id, assigned.ProfileId);
        using var ownerClient = fixture.CreateClient(owner.Key);
        using var assignedClient = fixture.CreateClient(assigned.Key);
        using var otherClient = fixture.CreateClient(other.Key);
        await PutCompanyAsync(ownerClient, company);
        await PutOrderAsync(ownerClient, order);
        await RequestAsync(assignedClient, company.Id);
        await RequestAsync(otherClient, company.Id);
        using var assignedApproved = await ownerClient.PostAsync(
            $"/trade/v1/companies/{company.Id:D}/memberships/{assigned.ProfileId:D}/approve",
            null);
        using var otherApproved = await ownerClient.PostAsync(
            $"/trade/v1/companies/{company.Id:D}/memberships/{other.ProfileId:D}/approve",
            null);
        assignedApproved.EnsureSuccessStatusCode();
        otherApproved.EnsureSuccessStatusCode();

        using var assignedResponse = await assignedClient.GetAsync(
            $"/trade/v1/companies/{company.Id:D}/hub");
        using var otherResponse = await otherClient.GetAsync(
            $"/trade/v1/companies/{company.Id:D}/hub");
        using var assignedJson = JsonDocument.Parse(
            await assignedResponse.Content.ReadAsStringAsync());
        using var otherJson = JsonDocument.Parse(
            await otherResponse.Content.ReadAsStringAsync());

        Assert.Equal("hub", assignedJson.RootElement.GetProperty("kind").GetString());
        var assignment = Assert.Single(
            assignedJson.RootElement.GetProperty("assignments").EnumerateArray());
        Assert.Equal(order.Id.ToString("D"), assignment.GetProperty("commissionId").GetString());
        Assert.Empty(otherJson.RootElement.GetProperty("assignments").EnumerateArray());

        using var revoked = await ownerClient.PostAsync(
            $"/trade/v1/companies/{company.Id:D}/memberships/{assigned.ProfileId:D}/revoke",
            null);
        revoked.EnsureSuccessStatusCode();
        using var revokedResponse = await assignedClient.GetAsync(
            $"/trade/v1/companies/{company.Id:D}/hub");
        using var revokedJson = JsonDocument.Parse(
            await revokedResponse.Content.ReadAsStringAsync());
        Assert.Equal("teaser", revokedJson.RootElement.GetProperty("kind").GetString());
        Assert.False(revokedJson.RootElement.TryGetProperty("assignments", out _));
    }

    [Fact]
    public async Task OwnerCanPublishHubWhileMemberCannotMutateIt()
    {
        await using var fixture = await HubFixture.CreateAsync();
        var owner = await fixture.CreateAccountAsync("Owner");
        var member = await fixture.CreateAccountAsync("Member");
        var company = CreateCompany();
        company.Updates =
        [
            new TradeCompanyUpdate
            {
                Title = "Old pin",
                Body = "Old announcement.",
                AuthorDisplayName = "Owner",
                PublishedAtUtc = DateTime.UtcNow.AddDays(-1),
                IsPinned = true
            }
        ];
        using var ownerClient = fixture.CreateClient(owner.Key);
        using var memberClient = fixture.CreateClient(member.Key);
        await PutCompanyAsync(ownerClient, company);
        await RequestAsync(memberClient, company.Id);
        using var approved = await ownerClient.PostAsync(
            $"/trade/v1/companies/{company.Id:D}/memberships/{member.ProfileId:D}/approve",
            null);
        approved.EnsureSuccessStatusCode();

        using var memberAttempt = await memberClient.PostAsJsonAsync(
            $"/trade/v1/companies/{company.Id:D}/hub/updates",
            new { ExpectedProfileRevision = 1, Title = "Nope", Body = "Nope", IsPinned = false });
        using var published = await ownerClient.PostAsJsonAsync(
            $"/trade/v1/companies/{company.Id:D}/hub/updates",
            new
            {
                ExpectedProfileRevision = 1,
                Title = "  Priority work  ",
                Body = "[unsafe](javascript:alert) **Bring materials.**",
                IsPinned = true
            });
        using var stale = await ownerClient.PostAsJsonAsync(
            $"/trade/v1/companies/{company.Id:D}/hub/updates",
            new { ExpectedProfileRevision = 1, Title = "Stale", Body = "Stale", IsPinned = false });
        using var themed = await ownerClient.PutAsJsonAsync(
            $"/trade/v1/companies/{company.Id:D}/hub/theme",
            new
            {
                ExpectedProfileRevision = 2,
                Accent = "violet",
                BannerStyle = "pattern",
                Emblem = "workshop",
                Tagline = "  Built together  ",
                About = "A **member** company.",
                ShowOpenCommissionCount = true
            });
        using var hub = await memberClient.GetAsync($"/trade/v1/companies/{company.Id:D}/hub");
        var json = await hub.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(json);
        var updates = document.RootElement.GetProperty("updates").EnumerateArray().ToArray();

        Assert.Equal(HttpStatusCode.Forbidden, memberAttempt.StatusCode);
        Assert.Equal(HttpStatusCode.OK, published.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(HttpStatusCode.OK, themed.StatusCode);
        Assert.Equal(3, document.RootElement.GetProperty("profileRevision").GetInt64());
        var theme = document.RootElement.GetProperty("theme");
        Assert.Equal("violet", theme.GetProperty("accent").GetString());
        Assert.Equal("pattern", theme.GetProperty("bannerStyle").GetString());
        Assert.Equal("workshop", theme.GetProperty("emblem").GetString());
        Assert.Equal("Built together", theme.GetProperty("tagline").GetString());
        Assert.True(theme.GetProperty("showOpenCommissionCount").GetBoolean());
        Assert.Equal(2, updates.Length);
        Assert.Single(updates, item => item.GetProperty("isPinned").GetBoolean());
        Assert.Equal("Priority work", updates[0].GetProperty("title").GetString());
        Assert.Equal("Owner", updates[0].GetProperty("authorDisplayName").GetString());
        Assert.Contains("**Bring materials.**", updates[0].GetProperty("body").GetString());
        Assert.DoesNotContain("javascript:", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RevokedMemberGetsTeaser()
    {
        await using var fixture = await HubFixture.CreateAsync();
        var owner = await fixture.CreateAccountAsync("Owner");
        var member = await fixture.CreateAccountAsync("Member");
        var company = CreateCompany();
        using var ownerClient = fixture.CreateClient(owner.Key);
        using var memberClient = fixture.CreateClient(member.Key);
        await PutCompanyAsync(ownerClient, company);
        await RequestAsync(memberClient, company.Id);
        using var approved = await ownerClient.PostAsync(
            $"/trade/v1/companies/{company.Id:D}/memberships/{member.ProfileId:D}/approve",
            null);
        using var revoked = await ownerClient.PostAsync(
            $"/trade/v1/companies/{company.Id:D}/memberships/{member.ProfileId:D}/revoke",
            null);

        using var response = await memberClient.GetAsync("/trade/v1/companies/sapphire-avenue/hub");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal("teaser", json.RootElement.GetProperty("kind").GetString());
        Assert.Equal("none", json.RootElement.GetProperty("standing").GetProperty("state").GetString());
    }

    [Fact]
    public async Task AssignmentAttentionIsMemberScopedAndClearsOnlyAfterExplicitOpen()
    {
        await using var fixture = await HubFixture.CreateAsync();
        var owner = await fixture.CreateAccountAsync("Owner");
        var member = await fixture.CreateAccountAsync("Member");
        var company = CreateCompany();
        var order = CreateAssignedOrderWithCommissionerUpdate(company.Id, member.ProfileId);
        using var ownerClient = fixture.CreateClient(owner.Key);
        using var memberClient = fixture.CreateClient(member.Key);
        await PutCompanyAsync(ownerClient, company);
        await PutOrderAsync(ownerClient, order);
        await RequestAsync(memberClient, company.Id);
        using var approved = await ownerClient.PostAsync(
            $"/trade/v1/companies/{company.Id:D}/memberships/{member.ProfileId:D}/approve",
            null);
        approved.EnsureSuccessStatusCode();

        using var first = await memberClient.GetAsync($"/trade/v1/companies/{company.Id:D}/hub");
        using var second = await memberClient.GetAsync($"/trade/v1/companies/{company.Id:D}/hub");
        using var firstJson = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        using var secondJson = JsonDocument.Parse(await second.Content.ReadAsStringAsync());
        var firstAttention = Assert.Single(firstJson.RootElement
            .GetProperty("assignments").EnumerateArray())
            .GetProperty("unreadCommissionerUpdate");
        var secondAttention = Assert.Single(secondJson.RootElement
            .GetProperty("assignments").EnumerateArray())
            .GetProperty("unreadCommissionerUpdate");

        Assert.Equal(9, firstAttention.GetProperty("revision").GetInt64());
        Assert.StartsWith("Please stage the finished batch at the workshop bell.",
            firstAttention.GetProperty("text").GetString());
        Assert.Equal(9, secondAttention.GetProperty("revision").GetInt64());

        using var future = await memberClient.PostAsJsonAsync(
            $"/trade/v1/companies/{company.Id:D}/hub/commissions/{order.Id:D}/attention/read",
            new CompanyHubAttentionReadRequest(11));
        Assert.Equal(HttpStatusCode.Conflict, future.StatusCode);

        using var marked = await memberClient.PostAsJsonAsync(
            $"/trade/v1/companies/{company.Id:D}/hub/commissions/{order.Id:D}/attention/read",
            new CompanyHubAttentionReadRequest(9));
        marked.EnsureSuccessStatusCode();
        using var replay = await memberClient.PostAsJsonAsync(
            $"/trade/v1/companies/{company.Id:D}/hub/commissions/{order.Id:D}/attention/read",
            new CompanyHubAttentionReadRequest(8));
        replay.EnsureSuccessStatusCode();
        using var replayJson = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
        Assert.Equal(9, replayJson.RootElement.GetProperty("readRevision").GetInt64());

        using var cleared = await memberClient.GetAsync($"/trade/v1/companies/{company.Id:D}/hub");
        using var clearedJson = JsonDocument.Parse(await cleared.Content.ReadAsStringAsync());
        var clearedAttention = Assert.Single(clearedJson.RootElement
            .GetProperty("assignments").EnumerateArray())
            .GetProperty("unreadCommissionerUpdate");
        Assert.Equal(JsonValueKind.Null, clearedAttention.ValueKind);

        using var ownerAttempt = await ownerClient.PostAsJsonAsync(
            $"/trade/v1/companies/{company.Id:D}/hub/commissions/{order.Id:D}/attention/read",
            new CompanyHubAttentionReadRequest(9));
        Assert.Equal(HttpStatusCode.NotFound, ownerAttempt.StatusCode);
    }

    [Fact]
    public async Task HostOwnerCanMarkAssignedAttentionReadBeforeFounderReconciliation()
    {
        await using var fixture = await HubFixture.CreateAsync();
        var owner = await fixture.CreateAccountAsync("Host owner");
        var company = CreateCompany();
        var order = CreateAssignedOrderWithCommissionerUpdate(company.Id, owner.ProfileId);
        using var ownerClient = fixture.CreateClient(owner.Key);
        await PutCompanyAsync(ownerClient, company);
        await PutOrderAsync(ownerClient, order);
        await fixture.DeleteMembershipAsync(company.Id, owner.ProfileId);

        using var hub = await ownerClient.GetAsync($"/trade/v1/companies/{company.Id:D}/hub");
        hub.EnsureSuccessStatusCode();
        using var hubJson = JsonDocument.Parse(await hub.Content.ReadAsStringAsync());
        Assert.Equal(
            "owner",
            hubJson.RootElement.GetProperty("standing").GetProperty("role").GetString());
        Assert.Single(hubJson.RootElement.GetProperty("assignments").EnumerateArray());

        using var marked = await ownerClient.PostAsJsonAsync(
            $"/trade/v1/companies/{company.Id:D}/hub/commissions/{order.Id:D}/attention/read",
            new CompanyHubAttentionReadRequest(9));
        marked.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task HostileThemeIsClampedAndSanitizedOnProjection()
    {
        await using var fixture = await HubFixture.CreateAsync();
        var owner = await fixture.CreateAccountAsync("Owner");
        var company = CreateCompany();
        var payload = $"{{\"id\":\"{company.Id:D}\",\"name\":\"Sapphire Avenue\",\"createdAtUtc\":\"2026-01-01T00:00:00Z\",\"landing\":{{\"accent\":\"malicious\",\"bannerStyle\":999,\"emblem\":\"evil\",\"tagline\":\"{new string('x', 200)}\",\"about\":\"[bad](javascript:alert) **safe** [good](https://example.test/path)\"}}}}";
        using var client = fixture.CreateClient(owner.Key);
        using var put = await client.PutAsJsonAsync(
            $"/profile-host/objects/{ProfileSyncCollections.TradeCompanyProfiles}/{company.Id:D}",
            new ProfileSyncPutRequest { PayloadJson = payload, ExpectedRevision = 0 });
        put.EnsureSuccessStatusCode();

        using var response = await fixture.Application.CreateClient().GetAsync(
            "/trade/v1/companies/sapphire-avenue/hub");
        var json = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(json);
        var theme = document.RootElement.GetProperty("theme");

        Assert.Equal("deep-blue", theme.GetProperty("accent").GetString());
        Assert.Equal("gradient", theme.GetProperty("bannerStyle").GetString());
        Assert.Equal("star", theme.GetProperty("emblem").GetString());
        Assert.Equal(120, theme.GetProperty("tagline").GetString()!.Length);
        Assert.DoesNotContain("javascript:", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("https://example.test/path", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SlugCollisionUsesOrdinalAndGuidResolves()
    {
        await using var fixture = await HubFixture.CreateAsync();
        var firstOwner = await fixture.CreateAccountAsync("First owner");
        var secondOwner = await fixture.CreateAccountAsync("Second owner");
        var first = CreateCompany();
        var second = CreateCompany();
        second.CreatedAtUtc = first.CreatedAtUtc.AddMinutes(1);
        using var firstClient = fixture.CreateClient(firstOwner.Key);
        using var secondClient = fixture.CreateClient(secondOwner.Key);
        await PutCompanyAsync(firstClient, first);
        await PutCompanyAsync(secondClient, second);

        using var bare = await fixture.Application.CreateClient().GetAsync("/trade/v1/companies/sapphire-avenue/hub");
        using var ordinal = await fixture.Application.CreateClient().GetAsync("/trade/v1/companies/sapphire-avenue-2/hub");
        using var guid = await fixture.Application.CreateClient().GetAsync(
            $"/trade/v1/companies/{second.Id:D}/hub");
        using var bareJson = JsonDocument.Parse(await bare.Content.ReadAsStringAsync());
        using var ordinalJson = JsonDocument.Parse(await ordinal.Content.ReadAsStringAsync());
        using var guidJson = JsonDocument.Parse(await guid.Content.ReadAsStringAsync());

        Assert.Equal(first.Id.ToString("D"), bareJson.RootElement.GetProperty("companyId").GetString());
        Assert.Equal(second.Id.ToString("D"), ordinalJson.RootElement.GetProperty("companyId").GetString());
        Assert.Equal(second.Id.ToString("D"), guidJson.RootElement.GetProperty("companyId").GetString());
        Assert.Equal("sapphire-avenue-2", guidJson.RootElement.GetProperty("slug").GetString());
    }

    [Fact]
    public async Task CachedDirectoryInvalidatesAfterCompanyRenameWithoutExpandingTeaser()
    {
        await using var fixture = await HubFixture.CreateAsync();
        var owner = await fixture.CreateAccountAsync("Owner");
        var company = CreateCompany(showCount: true);
        using var ownerClient = fixture.CreateClient(owner.Key);
        await PutCompanyAsync(ownerClient, company);
        await PutOrderAsync(ownerClient, CreateOrder(company.Id));

        using var initial = await fixture.Application.CreateClient().GetAsync(
            "/trade/v1/companies/sapphire-avenue/hub");
        company.Name = "Moonlit Provisioners";
        await PutCompanyAsync(ownerClient, company, expectedRevision: 1);
        using var oldSlug = await fixture.Application.CreateClient().GetAsync(
            "/trade/v1/companies/sapphire-avenue/hub");
        using var renamed = await fixture.Application.CreateClient().GetAsync(
            "/trade/v1/companies/moonlit-provisioners/hub");
        var json = await renamed.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, initial.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, oldSlug.StatusCode);
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        Assert.Contains("\"kind\":\"teaser\"", json);
        Assert.DoesNotContain("openCommissions", json, StringComparison.Ordinal);
        Assert.DoesNotContain("assignments", json, StringComparison.Ordinal);
        Assert.DoesNotContain("roster", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CachedTeaserMaintainsWhitelist()
    {
        await using var fixture = await HubFixture.CreateAsync();
        var owner = await fixture.CreateAccountAsync("Owner");
        var company = CreateCompany(showCount: true);
        using var ownerClient = fixture.CreateClient(owner.Key);
        await PutCompanyAsync(ownerClient, company);
        await PutOrderAsync(ownerClient, CreateOrder(company.Id));

        using var first = await fixture.Application.CreateClient().GetAsync(
            "/trade/v1/companies/sapphire-avenue/hub");
        using var second = await fixture.Application.CreateClient().GetAsync(
            "/trade/v1/companies/sapphire-avenue/hub");
        var json = await second.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Contains("\"kind\":\"teaser\"", json);
        Assert.DoesNotContain("openCommissions", json, StringComparison.Ordinal);
        Assert.DoesNotContain("assignments", json, StringComparison.Ordinal);
        Assert.DoesNotContain("roster", json, StringComparison.Ordinal);
        Assert.DoesNotContain("output", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("payment", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("termsVersion", json, StringComparison.Ordinal);
        Assert.DoesNotContain("deliveryInstructions", json, StringComparison.Ordinal);
        Assert.DoesNotContain("publicBriefId", json, StringComparison.Ordinal);
        Assert.DoesNotContain("projectionRevision", json, StringComparison.Ordinal);
        Assert.DoesNotContain("completedQuantity", json, StringComparison.Ordinal);
        Assert.DoesNotContain("readyQuantity", json, StringComparison.Ordinal);
        Assert.DoesNotContain("acceptedQuantity", json, StringComparison.Ordinal);
    }

    private static TradeCompanyProfile CreateCompany(string name = "Sapphire Avenue", bool showCount = false)
    {
        var company = TradeCompanyProfile.CreateLocal(name, DateTime.UtcNow);
        company.Landing = new CompanyLandingTheme
        {
            Tagline = "Coordinated craft.",
            About = "A **workshop** for members.",
            ShowOpenCommissionCount = showCount
        };
        return company;
    }

    private static TradeOrder CreateOrder(Guid companyId)
    {
        var now = DateTime.UtcNow;
        var orderId = Guid.NewGuid();
        var outputLineId = Guid.NewGuid();
        var terms = new CompanyCommissionTermsVersion
        {
            Version = 1,
            CreatedAtUtc = now,
            CreatedBy = new("owner", CompanyCommissionActorKind.Commissioner),
            Outputs = [new(outputLineId, 100, "Rarefied Sykon Bavarois", 120, false)],
            Payment = new(CompanyCommissionPaymentSchedule.OnDelivery, "Delivery", 0, 0, 180000, 180000),
            DeliveryInstructions = "Deliver to the workshop.",
            PricingEvidence = new("test", "test", "test", now)
        };
        return new TradeOrder
        {
            Id = orderId,
            CompanyProfileId = companyId,
            Title = "Member commission",
            Status = TradeOrderStatus.ReadyToAssign,
            CompanyCommission = new TradeCompanyCommission
            {
                CommissionId = orderId,
                CompanyId = new CompanyId(companyId),
                CommissionerActorId = "owner",
                Reference = "SA-1",
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                CurrentTermsVersion = 1,
                TermsVersions = [terms],
                PublicMetadata = new() { PublicBriefId = "brief", ViewState = CompanyCommissionPublicViewState.Published },
                ActiveClaimCapabilityRevision = 1,
                Gates = new(
                    new(CompanyCommissionClearanceState.NotRequired),
                    new(CompanyCommissionClearanceState.NotRequired),
                    new(CompanyCommissionClearanceState.NotRequired, [])),
                OutputProgress = [new(
                    outputLineId,
                    100,
                    120,
                    80,
                    60,
                    40,
                    now,
                    new("crafter", CompanyCommissionActorKind.Crafter))],
                DeliveryReadiness = new(false),
                SettlementState = CompanyCommissionSettlementState.NotDue,
                Activity = [new CompanyCommissionActivityEvent
                {
                    EventId = Guid.NewGuid(),
                    CommissionId = orderId,
                    CommissionRevision = 7,
                    Actor = new("crafter", CompanyCommissionActorKind.Crafter),
                    SourceSurface = CompanyCommissionSourceSurface.TradeArchitect,
                    CreatedAtUtc = now,
                    Kind = CompanyCommissionActivityKind.ProgressReported,
                    TermsVersion = 1
                }]
            }
        };
    }

    private static TradeOrder CreateAssignedOrder(Guid companyId, Guid crafterId)
    {
        var order = CreateOrder(companyId);
        order.Title = "Assigned member commission";
        order.CompanyCommission = order.CompanyCommission! with
        {
            ActiveClaim = new CompanyCommissionClaim(
                Guid.NewGuid(),
                1,
                DateTime.UtcNow,
                crafterId,
                null),
            ParticipantAcknowledgedTermsVersion = 1
        };
        return order;
    }

    private static TradeOrder CreateAssignedOrderWithCommissionerUpdate(
        Guid companyId,
        Guid crafterId)
    {
        var order = CreateAssignedOrder(companyId, crafterId);
        var commission = order.CompanyCommission!;
        var claimedAt = commission.ActiveClaim!.ClaimedAtUtc;
        order.Status = TradeOrderStatus.Assigned;
        order.CompanyCommission = commission with
        {
            Activity =
            [
                new CompanyCommissionActivityEvent
                {
                    EventId = Guid.NewGuid(),
                    CommissionId = order.Id,
                    CommissionRevision = 8,
                    Actor = new("owner", CompanyCommissionActorKind.Commissioner),
                    SourceSurface = CompanyCommissionSourceSurface.TradeArchitect,
                    CreatedAtUtc = claimedAt,
                    Kind = CompanyCommissionActivityKind.ClaimAccepted,
                    TermsVersion = 1
                },
                new CompanyCommissionActivityEvent
                {
                    EventId = Guid.NewGuid(),
                    CommissionId = order.Id,
                    CommissionRevision = 9,
                    Actor = new("owner", CompanyCommissionActorKind.Commissioner),
                    SourceSurface = CompanyCommissionSourceSurface.TradeArchitect,
                    CreatedAtUtc = claimedAt.AddSeconds(1),
                    Kind = CompanyCommissionActivityKind.CommentAdded,
                    TermsVersion = 1,
                    Comment = "Please stage the finished batch at the workshop bell."
                },
                new CompanyCommissionActivityEvent
                {
                    EventId = Guid.NewGuid(),
                    CommissionId = order.Id,
                    CommissionRevision = 10,
                    Actor = new("owner", CompanyCommissionActorKind.Commissioner),
                    SourceSurface = CompanyCommissionSourceSurface.TradeArchitect,
                    CreatedAtUtc = claimedAt.AddSeconds(2),
                    Kind = CompanyCommissionActivityKind.CommentAdded,
                    Visibility = CompanyCommissionActivityVisibility.CompanyOnly,
                    TermsVersion = 1,
                    Comment = "Private operator note."
                }
            ]
        };
        return order;
    }

    private static async Task PutCompanyAsync(
        HttpClient client,
        TradeCompanyProfile company,
        long expectedRevision = 0)
    {
        using var response = await client.PutAsJsonAsync(
            $"/profile-host/objects/{ProfileSyncCollections.TradeCompanyProfiles}/{company.Id:D}",
            new ProfileSyncPutRequest
            {
                PayloadJson = JsonSerializer.Serialize(company, ProfileSyncJson.CreateOptions()),
                ExpectedRevision = expectedRevision
            });
        response.EnsureSuccessStatusCode();
    }

    private static async Task PutOrderAsync(HttpClient client, TradeOrder order)
    {
        using var response = await client.PutAsJsonAsync(
            $"/profile-host/objects/{ProfileSyncCollections.TradeOrders}/{order.Id:D}",
            new ProfileSyncPutRequest
            {
                PayloadJson = JsonSerializer.Serialize(order, ProfileSyncJson.CreateOptions()),
                ExpectedRevision = 0
            });
        response.EnsureSuccessStatusCode();
    }

    private static async Task RequestAsync(HttpClient client, Guid companyId)
    {
        using var response = await client.PostAsJsonAsync(
            $"/trade/v1/companies/{companyId:D}/membership-requests",
            new MembershipRequestBody(null));
        response.EnsureSuccessStatusCode();
    }

    private sealed class HubFixture : IAsyncDisposable
    {
        private readonly string root;
        private readonly WebApplicationFactory<Program> application;

        private HubFixture(string root, WebApplicationFactory<Program> application)
        {
            this.root = root;
            this.application = application;
            Profiles = application.Services.GetRequiredService<SqliteProfileHostStore>();
            Identities = application.Services.GetRequiredService<SqliteDiscordIdentityStore>();
            Companies = application.Services.GetRequiredService<ProfileHostedTradeCompanyService>();
        }

        public WebApplicationFactory<Program> Application => application;
        public SqliteProfileHostStore Profiles { get; }
        public SqliteDiscordIdentityStore Identities { get; }
        public ProfileHostedTradeCompanyService Companies { get; }

        public async Task DeleteMembershipAsync(Guid companyId, Guid profileId)
        {
            await using var connection = new SqliteConnection(
                $"Data Source={Path.Combine(root, "memberships.db")}");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                DELETE FROM company_memberships
                WHERE company_id = $companyId AND account_profile_id = $profileId;
                """;
            command.Parameters.AddWithValue("$companyId", companyId.ToString("D"));
            command.Parameters.AddWithValue("$profileId", profileId.ToString("D"));
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        public async Task SetMembershipRoleAsync(
            Guid companyId,
            Guid profileId,
            string role)
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
            command.Parameters.AddWithValue("$role", role);
            command.Parameters.AddWithValue("$companyId", companyId.ToString("D"));
            command.Parameters.AddWithValue("$profileId", profileId.ToString("D"));
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        public static Task<HubFixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), $"craft-hub-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var application = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
                builder.ConfigureAppConfiguration((_, configuration) =>
                    configuration.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ProfileHost:Enabled"] = "true",
                        ["ProfileHost:DatabasePath"] = Path.Combine(root, "profiles.db"),
                        ["ProfileHost:ArchiveBackupDirectory"] = Path.Combine(root, "archive"),
                        ["TradeMemberships:DatabasePath"] = Path.Combine(root, "memberships.db")
                    })));
            return Task.FromResult(new HubFixture(root, application));
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
            var linked = await Identities.LinkAsync(
                profileId,
                $"{100000000000000000L + Math.Abs((long)profileId.GetHashCode()):D18}",
                displayName,
                DateTimeOffset.UtcNow);
            Assert.Equal(DiscordIdentityLinkResultStatus.Linked, linked.Status);
            return new Account(profileId, key.PlaintextKey);
        }

        public async ValueTask DisposeAsync()
        {
            await application.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed record Account(Guid ProfileId, string Key);
}
