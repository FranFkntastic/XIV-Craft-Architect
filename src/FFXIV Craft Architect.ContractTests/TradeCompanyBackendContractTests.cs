using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.LodestoneLookup.Services.TradeCompanies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FFXIV_Craft_Architect.ContractTests;

public sealed class TradeCompanyBackendContractTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Provisioning_IsExplicitAndEnvironmentBindingDoesNotDefineCompanyIdentity()
    {
        await using var fixture = TradeCompanyFixture.Create("canonical-a");
        using var unprovisioned = fixture.Application.CreateClient();
        using var denied = await unprovisioned.PostAsJsonAsync(
            "/trade-company/v1/companies",
            new TradeCompanyCreateRequest { DisplayName = "Sapphire Avenue" });

        var provisioned = await fixture.ProvisionAsync("Sapphire Avenue");
        using var authenticated = fixture.CreateCompanyClient(provisioned.AccessKey);
        var company = await authenticated.GetFromJsonAsync<TradeCompanyIdentity>(
            $"/trade-company/v1/companies/{provisioned.Company.CompanyId}");
        var meta = await unprovisioned.GetFromJsonAsync<TradeCompanyMetaResponse>(
            "/trade-company/v1/meta");

        await using var replacement = fixture.CreateReplacementApplication("canonical-b");
        using var replacementClient = replacement.CreateClient();
        replacementClient.DefaultRequestHeaders.Add("X-Trade-Company-Key", provisioned.AccessKey);
        var replacementRead = await replacementClient.GetFromJsonAsync<TradeCompanyIdentity>(
            $"/trade-company/v1/companies/{provisioned.Company.CompanyId}");

        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        Assert.Equal(provisioned.Company.CompanyId, company?.CompanyId);
        Assert.Equal(provisioned.Company.CompanyId, replacementRead?.CompanyId);
        Assert.Equal("canonical-a", meta?.EnvironmentId);
        Assert.Equal(SqliteTradeCompanyStore.CurrentSchemaVersion, meta?.SchemaVersion);
        Assert.Equal(CompanyRevision.None, company?.Revision);
    }

    [Fact]
    public async Task EnabledServiceWithoutEnvironmentIdentityFailsClosed()
    {
        await using var fixture = TradeCompanyFixture.Create("unconfigured");
        using var client = fixture.Application.CreateClient();
        client.DefaultRequestHeaders.Add(
            "X-Trade-Company-Provisioning-Key",
            "contract-provisioning-key");

        var meta = await client.GetFromJsonAsync<TradeCompanyMetaResponse>(
            "/trade-company/v1/meta");
        using var create = await client.PostAsJsonAsync(
            "/trade-company/v1/companies",
            new TradeCompanyCreateRequest { DisplayName = "Must Not Exist" });

        Assert.False(meta?.Enabled);
        Assert.Equal(0, meta?.SchemaVersion);
        Assert.Equal(HttpStatusCode.NotFound, create.StatusCode);
    }

    [Fact]
    public async Task CompanyGrant_CannotSelectOrMutateAnotherTenant()
    {
        await using var fixture = TradeCompanyFixture.Create();
        var first = await fixture.ProvisionAsync("First Company");
        var second = await fixture.ProvisionAsync("Second Company");
        using var firstClient = fixture.CreateCompanyClient(first.AccessKey);
        using var secondClient = fixture.CreateCompanyClient(second.AccessKey);

        var firstMutation = await PutAsync(
            firstClient,
            first.Company.CompanyId,
            TradeCompanyRecordKinds.Profile,
            "company-profile",
            "{\"owner\":\"first\"}",
            CompanyRecordRevision.None,
            CompanyRevision.None,
            "first-profile-0001");
        var secondMutation = await PutAsync(
            secondClient,
            second.Company.CompanyId,
            TradeCompanyRecordKinds.Profile,
            "company-profile",
            "{\"owner\":\"second\"}",
            CompanyRecordRevision.None,
            CompanyRevision.None,
            "second-profile-0001");

        using var crossTenantRead = await firstClient.GetAsync(
            $"/trade-company/v1/companies/{second.Company.CompanyId}/changes?afterRevision=0");
        using var crossTenantWrite = await firstClient.PutAsJsonAsync(
            $"/trade-company/v1/companies/{second.Company.CompanyId}/records/profile/company-profile",
            new TradeCompanyRecordPutRequest
            {
                PayloadJson = "{\"owner\":\"intruder\"}",
                ExpectedRecordRevision = secondMutation.Record!.RecordRevision,
                ExpectedCompanyRevision = secondMutation.Record.CompanyRevision,
                IdempotencyKey = "cross-tenant-0001"
            });
        var firstChanges = await firstClient.GetFromJsonAsync<TradeCompanyChangeSet>(
            $"/trade-company/v1/companies/{first.Company.CompanyId}/changes?afterRevision=0");
        var secondChanges = await secondClient.GetFromJsonAsync<TradeCompanyChangeSet>(
            $"/trade-company/v1/companies/{second.Company.CompanyId}/changes?afterRevision=0");

        Assert.Equal(HttpStatusCode.NotFound, crossTenantRead.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, crossTenantWrite.StatusCode);
        Assert.Equal("{\"owner\":\"first\"}", Assert.Single(firstChanges!.Records).PayloadJson);
        Assert.Equal("{\"owner\":\"second\"}", Assert.Single(secondChanges!.Records).PayloadJson);
        Assert.Equal(firstMutation.Record!.CompanyRevision, firstChanges.CompanyRevision);
    }

    [Fact]
    public async Task RolesFenceMutationsAndGrantManagement()
    {
        await using var fixture = TradeCompanyFixture.Create();
        var company = await fixture.ProvisionAsync("Role Company");
        using var ownerClient = fixture.CreateCompanyClient(company.AccessKey);
        var readOnly = await CreateGrantAsync(ownerClient, company.Company.CompanyId, TradeCompanyRole.ReadOnly);
        var tradeOperator = await CreateGrantAsync(ownerClient, company.Company.CompanyId, TradeCompanyRole.Operator);
        using var readOnlyClient = fixture.CreateCompanyClient(readOnly.AccessKey);
        using var operatorClient = fixture.CreateCompanyClient(tradeOperator.AccessKey);

        using var readAllowed = await readOnlyClient.GetAsync(
            $"/trade-company/v1/companies/{company.Company.CompanyId}");
        using var readOnlyWrite = await readOnlyClient.PutAsJsonAsync(
            $"/trade-company/v1/companies/{company.Company.CompanyId}/records/order/order-1",
            NewPutRequest("{\"title\":\"blocked\"}", "readonly-write-0001"));
        using var operatorWrite = await operatorClient.PutAsJsonAsync(
            $"/trade-company/v1/companies/{company.Company.CompanyId}/records/order/order-1",
            NewPutRequest("{\"title\":\"allowed\"}", "operator-write-0001"));
        using var operatorGrantAttempt = await operatorClient.PostAsJsonAsync(
            $"/trade-company/v1/companies/{company.Company.CompanyId}/grants",
            new TradeCompanyGrantCreateRequest { Role = TradeCompanyRole.ReadOnly });
        var grants = await ownerClient.GetFromJsonAsync<IReadOnlyList<TradeCompanyGrantRecord>>(
            $"/trade-company/v1/companies/{company.Company.CompanyId}/grants");

        Assert.Equal(HttpStatusCode.OK, readAllowed.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, readOnlyWrite.StatusCode);
        Assert.Equal(HttpStatusCode.OK, operatorWrite.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, operatorGrantAttempt.StatusCode);
        Assert.Equal(3, grants?.Count);
    }

    [Fact]
    public async Task OptimisticRevisionsAndIdempotencyFailClosed()
    {
        await using var fixture = TradeCompanyFixture.Create();
        var company = await fixture.ProvisionAsync("Revision Company");
        using var client = fixture.CreateCompanyClient(company.AccessKey);
        var first = await PutAsync(
            client,
            company.Company.CompanyId,
            TradeCompanyRecordKinds.Order,
            "order-1",
            "{\"title\":\"Authoritative\"}",
            CompanyRecordRevision.None,
            CompanyRevision.None,
            "order-create-0001");

        using var staleResponse = await client.PutAsJsonAsync(
            $"/trade-company/v1/companies/{company.Company.CompanyId}/records/order/order-1",
            NewPutRequest("{\"title\":\"Stale\"}", "order-stale-0001"));
        var stale = await staleResponse.Content.ReadFromJsonAsync<TradeCompanyMutationResult>();

        using var replayResponse = await client.PutAsJsonAsync(
            $"/trade-company/v1/companies/{company.Company.CompanyId}/records/order/order-1",
            NewPutRequest("{\"title\":\"Authoritative\"}", "order-create-0001"));
        var replay = await replayResponse.Content.ReadFromJsonAsync<TradeCompanyMutationResult>();

        using var reusedKeyResponse = await client.PutAsJsonAsync(
            $"/trade-company/v1/companies/{company.Company.CompanyId}/records/order/order-1",
            NewPutRequest("{\"title\":\"Different\"}", "order-create-0001"));
        var changes = await client.GetFromJsonAsync<TradeCompanyChangeSet>(
            $"/trade-company/v1/companies/{company.Company.CompanyId}/changes?afterRevision=0");

        Assert.Equal(HttpStatusCode.Conflict, staleResponse.StatusCode);
        Assert.Equal(TradeCompanyMutationStatus.Conflict, stale?.Status);
        Assert.Equal(first.Record?.RecordRevision, stale?.CurrentRecord?.RecordRevision);
        Assert.Equal(TradeCompanyMutationStatus.Replayed, replay?.Status);
        Assert.Equal(HttpStatusCode.BadRequest, reusedKeyResponse.StatusCode);
        Assert.Equal(1, changes?.CompanyRevision.Value);
        Assert.Equal("{\"title\":\"Authoritative\"}", Assert.Single(changes!.Records).PayloadJson);
    }

    [Fact]
    public async Task PublicationOwnershipRequiresCanonicalCompanyAndOrderRevision()
    {
        await using var fixture = TradeCompanyFixture.Create();
        var company = await fixture.ProvisionAsync("Publication Company");
        var other = await fixture.ProvisionAsync("Other Company");
        using var client = fixture.CreateCompanyClient(company.AccessKey);
        var ownership = new TradeCompanyPublicationOwnership(
            company.Company.CompanyId,
            Guid.NewGuid(),
            new CompanyRecordRevision(9));

        var applied = await PutAsync(
            client,
            company.Company.CompanyId,
            TradeCompanyRecordKinds.Publication,
            "public_brief_0001",
            JsonSerializer.Serialize(ownership, JsonOptions),
            CompanyRecordRevision.None,
            CompanyRevision.None,
            "publication-bind-0001");
        var service = fixture.Application.Services.GetRequiredService<ITradeCompanyService>();
        var resolved = await service.ResolvePublicationOwnershipAsync("public_brief_0001");

        var invalidOwnership = ownership with { CompanyId = other.Company.CompanyId };
        using var invalid = await client.PutAsJsonAsync(
            $"/trade-company/v1/companies/{company.Company.CompanyId}/records/publication/public_brief_0002",
            NewPutRequest(
                JsonSerializer.Serialize(invalidOwnership, JsonOptions),
                "publication-bind-0002",
                expectedCompanyRevision: applied.Record!.CompanyRevision));

        Assert.Equal(ownership, resolved);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Fact]
    public async Task GrantRevocationProtectsLastOwnerAndTakesEffectImmediately()
    {
        await using var fixture = TradeCompanyFixture.Create();
        var company = await fixture.ProvisionAsync("Owner Company");
        using var firstOwnerClient = fixture.CreateCompanyClient(company.AccessKey);

        using var lastOwnerAttempt = await firstOwnerClient.DeleteAsync(
            $"/trade-company/v1/companies/{company.Company.CompanyId}/grants/{company.OwnerGrant.GrantId}");
        var secondOwner = await CreateGrantAsync(
            firstOwnerClient,
            company.Company.CompanyId,
            TradeCompanyRole.Owner);
        using var revokeFirst = await firstOwnerClient.DeleteAsync(
            $"/trade-company/v1/companies/{company.Company.CompanyId}/grants/{company.OwnerGrant.GrantId}");
        using var revokedClient = fixture.CreateCompanyClient(company.AccessKey);
        using var revokedRead = await revokedClient.GetAsync(
            $"/trade-company/v1/companies/{company.Company.CompanyId}");
        using var secondOwnerClient = fixture.CreateCompanyClient(secondOwner.AccessKey);
        using var activeRead = await secondOwnerClient.GetAsync(
            $"/trade-company/v1/companies/{company.Company.CompanyId}");

        Assert.Equal(HttpStatusCode.Conflict, lastOwnerAttempt.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, revokeFirst.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, revokedRead.StatusCode);
        Assert.Equal(HttpStatusCode.OK, activeRead.StatusCode);
    }

    [Fact]
    public async Task UnsupportedProtocolCannotMutateCompanyState()
    {
        await using var fixture = TradeCompanyFixture.Create();
        var company = await fixture.ProvisionAsync("Protocol Company");
        using var client = fixture.CreateCompanyClient(company.AccessKey);

        using var response = await client.PutAsJsonAsync(
            $"/trade-company/v1/companies/{company.Company.CompanyId}/records/order/order-1",
            new TradeCompanyRecordPutRequest
            {
                PayloadJson = "{\"title\":\"future\"}",
                IdempotencyKey = "future-client-0001",
                ProtocolVersion = TradeCompanyProtocol.CurrentVersion + 1
            });
        var changes = await client.GetFromJsonAsync<TradeCompanyChangeSet>(
            $"/trade-company/v1/companies/{company.Company.CompanyId}/changes?afterRevision=0");

        Assert.Equal(HttpStatusCode.UpgradeRequired, response.StatusCode);
        Assert.Equal(CompanyRevision.None, changes?.CompanyRevision);
        Assert.Empty(changes!.Records);
    }

    [Fact]
    public async Task FutureDatabaseSchemaFailsClosed()
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            $"ca-trade-company-future-{Guid.NewGuid():N}.db");
        await using (var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                create table trade_company_schema_migrations (
                    version integer primary key,
                    applied_at_utc text not null
                );
                insert into trade_company_schema_migrations(version, applied_at_utc)
                values (999, '2026-07-29T00:00:00.0000000Z');
                """;
            await command.ExecuteNonQueryAsync();
        }

        var store = new SqliteTradeCompanyStore(
            new TradeCompanyOptions { DatabasePath = databasePath },
            new TradeCompanyAccessKeyHasher());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.GetSchemaVersionAsync());

        SqliteConnection.ClearAllPools();
        File.Delete(databasePath);
    }

    private static async Task<TradeCompanyMutationResult> PutAsync(
        HttpClient client,
        CompanyId companyId,
        string kind,
        string recordId,
        string payloadJson,
        CompanyRecordRevision expectedRecordRevision,
        CompanyRevision expectedCompanyRevision,
        string idempotencyKey)
    {
        using var response = await client.PutAsJsonAsync(
            $"/trade-company/v1/companies/{companyId}/records/{kind}/{recordId}",
            NewPutRequest(
                payloadJson,
                idempotencyKey,
                expectedRecordRevision,
                expectedCompanyRevision));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TradeCompanyMutationResult>())!;
    }

    private static TradeCompanyRecordPutRequest NewPutRequest(
        string payloadJson,
        string idempotencyKey,
        CompanyRecordRevision? expectedRecordRevision = null,
        CompanyRevision? expectedCompanyRevision = null) =>
        new()
        {
            PayloadJson = payloadJson,
            ExpectedRecordRevision = expectedRecordRevision ?? CompanyRecordRevision.None,
            ExpectedCompanyRevision = expectedCompanyRevision ?? CompanyRevision.None,
            IdempotencyKey = idempotencyKey
        };

    private static async Task<TradeCompanyGrantCreateResponse> CreateGrantAsync(
        HttpClient ownerClient,
        CompanyId companyId,
        TradeCompanyRole role)
    {
        using var response = await ownerClient.PostAsJsonAsync(
            $"/trade-company/v1/companies/{companyId}/grants",
            new TradeCompanyGrantCreateRequest { Role = role });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TradeCompanyGrantCreateResponse>())!;
    }

    private sealed class TradeCompanyFixture : IAsyncDisposable
    {
        private const string ProvisioningKey = "contract-provisioning-key";
        private readonly string databasePath;

        private TradeCompanyFixture(
            string databasePath,
            WebApplicationFactory<Program> application)
        {
            this.databasePath = databasePath;
            Application = application;
        }

        public WebApplicationFactory<Program> Application { get; }

        public static TradeCompanyFixture Create(string environmentId = "contract")
        {
            var databasePath = Path.Combine(
                Path.GetTempPath(),
                $"ca-trade-company-{Guid.NewGuid():N}.db");
            return new TradeCompanyFixture(
                databasePath,
                CreateApplication(databasePath, environmentId));
        }

        public WebApplicationFactory<Program> CreateReplacementApplication(string environmentId) =>
            CreateApplication(databasePath, environmentId);

        public async Task<TradeCompanyProvisionResponse> ProvisionAsync(string displayName)
        {
            using var client = Application.CreateClient();
            client.DefaultRequestHeaders.Add("X-Trade-Company-Provisioning-Key", ProvisioningKey);
            using var response = await client.PostAsJsonAsync(
                "/trade-company/v1/companies",
                new TradeCompanyCreateRequest { DisplayName = displayName });
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<TradeCompanyProvisionResponse>())!;
        }

        public HttpClient CreateCompanyClient(string accessKey)
        {
            var client = Application.CreateClient();
            client.DefaultRequestHeaders.Add("X-Trade-Company-Key", accessKey);
            return client;
        }

        public async ValueTask DisposeAsync()
        {
            await Application.DisposeAsync();
            SqliteConnection.ClearAllPools();
            if (File.Exists(databasePath))
            {
                File.Delete(databasePath);
            }
        }

        private static WebApplicationFactory<Program> CreateApplication(
            string databasePath,
            string environmentId) =>
            new WebApplicationFactory<Program>()
                .WithWebHostBuilder(builder =>
                {
                    builder.ConfigureAppConfiguration((_, configuration) =>
                    {
                        configuration.AddInMemoryCollection(new Dictionary<string, string?>
                        {
                            ["TradeCompany:Enabled"] = "true",
                            ["TradeCompany:DatabasePath"] = databasePath,
                            ["TradeCompany:EnvironmentId"] = environmentId,
                            ["TradeCompany:ProvisioningKey"] = ProvisioningKey
                        });
                    });
                });
    }
}
