using System.Net;
using System.Net.Http.Json;
using FFXIV_Craft_Architect.Core.Integrations.WorkshopHost;
using FFXIV_Craft_Architect.LodestoneLookup.Services.CraftAppraisal;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FFXIV_Craft_Architect.ContractTests;

public sealed class CraftAppraisalApiContractTests
{
    [Fact]
    public async Task Appraise_ReturnsHostedQuoteContractAndCaOwnedPlanLink()
    {
        await using var application = CreateApplication(new StaticCoordinator(new CraftAppraisalQuote
        {
            ItemId = 7017,
            ItemName = "Varnish",
            RequestedQuantity = 4,
            EstimatedUnitCost = 577,
            EstimatedTotalCost = 2308,
            QuotedAtUtc = DateTimeOffset.Parse("2026-07-30T12:00:00Z"),
            Source = "CraftArchitectHosted",
            PlanId = new string('a', 64),
            PlanUrl = "https://dev.xivcraftarchitect.com/?appraisalPlan=https%3A%2F%2Fdev.xivcraftarchitect.com%2Fapi%2Fcraft%2Fplans%2Faaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        }));
        using var client = application.CreateClient();

        using var response = await client.PostAsJsonAsync("/craft/appraise", new CraftAppraisalRequest
        {
            ItemId = 7017,
            ItemName = "Varnish",
            Quantity = 4,
        });
        var quote = await response.Content.ReadFromJsonAsync<CraftAppraisalQuote>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("CraftArchitectHosted", quote?.Source);
        Assert.StartsWith("https://dev.xivcraftarchitect.com/?appraisalPlan=", quote?.PlanUrl);
    }

    [Fact]
    public async Task Appraise_RejectsQuantityBeyondConfiguredBoundBeforeCompute()
    {
        var coordinator = new StaticCoordinator(new CraftAppraisalQuote());
        await using var application = CreateApplication(coordinator);
        using var client = application.CreateClient();

        using var response = await client.PostAsJsonAsync("/craft/appraise", new CraftAppraisalRequest
        {
            ItemId = 7017,
            ItemName = "Varnish",
            Quantity = 1000,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, coordinator.CallCount);
    }

    [Fact]
    public async Task PlanStore_UsesContentAddressedIdentity()
    {
        var root = Path.Combine(Path.GetTempPath(), "CraftAppraisalPlanStore.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new CraftAppraisalPlanStore(new CraftAppraisalApiOptions
            {
                PlanDirectory = root,
            });

            var first = await store.SaveAsync("""{"name":"Varnish"}""", CancellationToken.None);
            var second = await store.SaveAsync("""{"name":"Varnish"}""", CancellationToken.None);

            Assert.Equal(first, second);
            Assert.Equal("""{"name":"Varnish"}""", await store.ReadAsync(first, CancellationToken.None));
            Assert.Single(Directory.EnumerateFiles(root, "*.craftplan"));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static WebApplicationFactory<Program> CreateApplication(
        IHostedCraftAppraisalCoordinator coordinator)
    {
        var contentRoot = Path.Combine(
            Path.GetTempPath(),
            "CraftAppraisalApiContract.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(contentRoot);
        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseContentRoot(contentRoot);
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<CraftAppraisalApiOptions>();
                    services.AddSingleton(new CraftAppraisalApiOptions
                    {
                        Enabled = true,
                        CacheDirectory = Path.Combine(contentRoot, "cache"),
                        PlanDirectory = Path.Combine(contentRoot, "plans"),
                        PublicAppOrigin = "https://dev.xivcraftarchitect.com",
                        PublicApiOrigin = "https://dev.xivcraftarchitect.com/api",
                    });
                    services.RemoveAll<IHostedCraftAppraisalCoordinator>();
                    services.AddSingleton(coordinator);
                });
            });
    }

    private sealed class StaticCoordinator(CraftAppraisalQuote quote) : IHostedCraftAppraisalCoordinator
    {
        public int CallCount { get; private set; }

        public bool IsAvailable => true;

        public Task<CraftAppraisalQuote> AppraiseAsync(
            CraftAppraisalRequest request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(quote);
        }
    }
}
