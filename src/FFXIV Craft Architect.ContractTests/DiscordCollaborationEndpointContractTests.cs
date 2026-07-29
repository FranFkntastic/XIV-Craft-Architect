using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace FFXIV_Craft_Architect.ContractTests;

public sealed class DiscordCollaborationEndpointContractTests
{
    [Fact]
    public async Task OperatorRoutes_FailClosedUntilCanonicalAccessResolverIsIntegrated()
    {
        await using var application = new WebApplicationFactory<Program>();
        using var client = application.CreateClient();

        using var response = await client.GetAsync(
            "/trade/v1/companies/018fdc85-9b7a-7c31-87ed-6f9bdb4a8888/discord/claims");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
