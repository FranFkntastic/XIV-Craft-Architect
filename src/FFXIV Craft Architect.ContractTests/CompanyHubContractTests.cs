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
    public async Task MaterialSourcesMarginsScopeAndBatchSizesSurviveSettingsSyncAndMarginItemSaves()
    {
        await using var fixture = await HubFixture.CreateAsync();
        var owner = await fixture.CreateAccountAsync("Owner");
        var company = CreateCompany();
        using var client = fixture.CreateClient(owner.Key);
        await PutCompanyAsync(client, company);
        TradeMaterialSource[] sources =
        [
            new(5057, TradeMaterialRoute.Vendor),
            new(5371, TradeMaterialRoute.SetPrice, 0)
        ];
        string[] regions = ["North America", "Europe"];
        var workspace = $"/trade/v1/companies/{company.Id:D}/workspace-profile";

        // A company-wide Margins scope that is silently dropped reverts every operator's margins
        // to the narrow commission scope with no visible error.
        async Task<long> PutWorkspaceAsync(long revision, bool withSources) =>
            await ReadRevisionAsync(await client.PutAsJsonAsync(workspace, new TradeCompanyWorkspaceProfileUpdateRequest(
                revision, company.Name, "Settings edit", null, company.PaymentPolicy,
                MaterialSources: withSources ? sources : null, UpdateMaterialSources: withSources,
                MarginsMarketRegions: withSources ? regions : null, UpdateMarginsMarketRegions: withSources)));

        async Task<long> SyncAsync(long revision)
        {
            using var synced = await client.PutAsJsonAsync(
                $"/profile-host/objects/{ProfileSyncCollections.TradeCompanyProfiles}/{company.Id:D}",
                new ProfileSyncPutRequest
                {
                    PayloadJson = JsonSerializer.Serialize(company, ProfileSyncJson.CreateOptions()),
                    ExpectedRevision = revision
                });
            synced.EnsureSuccessStatusCode();
            return (await synced.Content.ReadFromJsonAsync<ProfileSyncPutResponse>())!.Object!.Revision;
        }

        var revision = await PutWorkspaceAsync(1, withSources: true);
        revision = await PutWorkspaceAsync(revision, withSources: false);
        revision = await SyncAsync(revision);
        using (var margins = await client.PutAsJsonAsync(
                   $"/trade/v1/companies/{company.Id:D}/margin-items",
                   new CompanyMarginItemsUpdateRequest(revision, true, [new TradeMarginItem(5057, 120, 25)])))
        {
            margins.EnsureSuccessStatusCode();
        }

        // Batch sizes travel with the margin items and survive later Settings and sync saves.
        revision = (await client.GetFromJsonAsync<TradeCompanyWorkspaceProfileResponse>(workspace))!.Revision;
        revision = await PutWorkspaceAsync(revision, withSources: false);
        await SyncAsync(revision);

        var stored = await client.GetFromJsonAsync<TradeCompanyWorkspaceProfileResponse>(workspace);
        Assert.Equal(sources, stored!.MaterialSources);
        Assert.Equal(regions, stored.MarginsMarketRegions!);
        Assert.Equal([new TradeMarginItem(5057, 120, 25)], stored.MarginItems!);
        // An item stored before batch sizes existed reads as a batch of one.
        Assert.Equal(1, JsonSerializer.Deserialize<TradeMarginItem>(
            """{"itemId":5057,"salePrice":120}""", ProfileSyncJson.CreateOptions())!.BatchSize);
    }

    private static async Task<long> ReadRevisionAsync(HttpResponseMessage response)
    {
        using (response)
        {
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<TradeCompanyWorkspaceProfileUpdateResponse>())!.Revision;
        }
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
