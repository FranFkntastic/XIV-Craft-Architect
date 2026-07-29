using System.Net;
using System.Net.Http.Json;
using FFXIV_Craft_Architect.Core.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace FFXIV_Craft_Architect.ContractTests;

public sealed class CommissionBriefContractTests
{
    [Fact]
    public async Task Publication_IsImmutablePublicEvidenceUntilCapabilityRevokesIt()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"ca-commission-{Guid.NewGuid():N}.db");
        var application = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, configuration) =>
                {
                    configuration.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["CommissionBriefs:Enabled"] = "true",
                        ["CommissionBriefs:DatabasePath"] = databasePath
                    });
                });
            });
        using var client = application.CreateClient();
        var source = new CommissionBriefDocument
        {
            CompanyName = "Sapphire Avenue",
            Title = "Shark-class Stern ×40",
            Reference = "CA-260729-TEST",
            Contact = "commission-operator",
            Outputs = [new CommissionBriefOutput(21792, "Shark-class Stern", 40, true)],
            CrafterMaterials =
            [
                new CommissionBriefMaterial(
                    10371,
                    "Cobalt Ingot",
                    78,
                    false,
                    79_194,
                    6_177_132)
            ],
            Payment = new CommissionBriefPayment(
                "Labor standard",
                6_177_174,
                617_717,
                1_752_000,
                8_546_891,
                10,
                2_920,
                600)
        };
        var ownership = new TradeCompanyPublicationOwnership(
            new CompanyId(Guid.Parse("018fdc85-9b7a-7c31-87ed-6f9bdb4a4444")),
            Guid.Parse("cc58c224-d6e6-402b-bcdd-e7b45dd00b44"),
            new CompanyRecordRevision(17));

        using var productionClient = application.CreateClient();
        productionClient.DefaultRequestHeaders.Host = "xivcraftarchitect.com";
        using var productionCreateResponse = await productionClient.PostAsJsonAsync(
            "/xivdata/commission-briefs",
            new CommissionBriefCreateRequest { Brief = source, Ownership = ownership });
        using var createResponse = await client.PostAsJsonAsync(
            "/xivdata/commission-briefs",
            new CommissionBriefCreateRequest { Brief = source, Ownership = ownership });
        createResponse.EnsureSuccessStatusCode();
        var created = (await createResponse.Content.ReadFromJsonAsync<CommissionBriefCreateResponse>())!;
        var published = await client.GetFromJsonAsync<PublishedCommissionBrief>(
            $"/xivdata/commission-briefs/{created.PublicId}");

        using var badRevoke = new HttpRequestMessage(HttpMethod.Delete, $"/xivdata/commission-briefs/{created.PublicId}");
        badRevoke.Headers.Add("X-Commission-Editor", "wrong-capability");
        using var badRevokeResponse = await client.SendAsync(badRevoke);
        using var stillPublic = await client.GetAsync($"/xivdata/commission-briefs/{created.PublicId}");

        using var revoke = new HttpRequestMessage(HttpMethod.Delete, $"/xivdata/commission-briefs/{created.PublicId}");
        revoke.Headers.Add("X-Commission-Editor", created.EditorToken);
        using var revokeResponse = await client.SendAsync(revoke);
        using var revoked = await client.GetAsync($"/xivdata/commission-briefs/{created.PublicId}");

        Assert.False(string.IsNullOrWhiteSpace(created.EditorToken));
        Assert.Equal(HttpStatusCode.NotFound, productionCreateResponse.StatusCode);
        Assert.Equal(1, created.Version);
        Assert.Equal(source.Title, published?.Brief.Title);
        Assert.Equal(source.Payment.Total, published?.Brief.Payment.Total);
        Assert.Equal(10m, published?.Brief.Payment.MaterialAdjustmentPercent);
        Assert.Equal(2_920, published?.Brief.Payment.CraftSynthCount);
        Assert.Equal(79_194m, published?.Brief.CrafterMaterials.Single().UnitCost);
        Assert.Equal(ownership, published?.Ownership);
        Assert.Equal(HttpStatusCode.Unauthorized, badRevokeResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, stillPublic.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, revokeResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, revoked.StatusCode);

        await application.DisposeAsync();
        SqliteConnection.ClearAllPools();
        if (File.Exists(databasePath))
        {
            File.Delete(databasePath);
        }
    }
}
