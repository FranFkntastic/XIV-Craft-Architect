using System.Net;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Core.Services.Interfaces;
using Microsoft.Extensions.Logging;
using Moq;

namespace FFXIV_Craft_Architect.Tests;

public class RecipeCalculationServiceTests
{
    [Fact]
    public async Task BuildPlanAsync_ReusedCachedNode_RecalculatesChildQuantitiesForRequestedQuantity()
    {
        var service = CreateService();

        var plan = await service.BuildPlanAsync(
            [
                (100, "Root Craft", 1, false),
                (100, "Root Craft", 3, false)
            ],
            "Aether",
            string.Empty);

        Assert.Equal(2, plan.RootItems.Count);
        Assert.Equal(2, Assert.Single(plan.RootItems[0].Children).Quantity);
        Assert.Equal(6, Assert.Single(plan.RootItems[1].Children).Quantity);
    }

    [Fact]
    public async Task BuildPlanAsync_StandardCraft_UsesSharedRecipeResolverSelection()
    {
        var service = CreateService();

        var plan = await service.BuildPlanAsync(
            [(300, "Resolver Selected Craft", 2, false)],
            "Aether",
            string.Empty);

        var root = Assert.Single(plan.RootItems);
        Assert.Equal(20, root.RecipeLevel);
        Assert.Equal("Armorer", root.Job);
        Assert.Equal(2, root.Yield);
        var child = Assert.Single(root.Children);
        Assert.Equal(301, child.ItemId);
        Assert.Equal(2, child.Quantity);
    }

    [Fact]
    public async Task BuildPlanAsync_WithDiagnostics_RecordsRecipeBuildSubphases()
    {
        var service = CreateService();
        var diagnostics = new RecordingRecipePlanBuildDiagnostics();

        await service.BuildPlanAsync(
            [(300, "Resolver Selected Craft", 2, false)],
            "Aether",
            string.Empty,
            diagnostics: diagnostics);

        Assert.Contains(("build-plan.discover-tree", "Completed"), diagnostics.Phases);
        Assert.Contains(("build-plan.fetch-level-0", "Completed"), diagnostics.Phases);
        Assert.Contains(("build-plan.fetch-level-1", "Completed"), diagnostics.Phases);
        Assert.Contains(("build-plan.build-tree-from-cache", "Completed"), diagnostics.Phases);
        Assert.Contains(("build-plan.apply-vendor-prices-from-cache", "Completed"), diagnostics.Phases);
    }

    private static RecipeCalculationService CreateService()
    {
        var garlandService = new GarlandService(
            new HttpClient(new GarlandItemHandler()),
            Mock.Of<ILogger<GarlandService>>());

        return new RecipeCalculationService(
            garlandService,
            new StubVendorCacheService(),
            Mock.Of<ILogger<RecipeCalculationService>>());
    }

    private sealed class GarlandItemHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var itemId = request.RequestUri?.Segments.LastOrDefault()?.Replace(".json", string.Empty, StringComparison.Ordinal) ?? string.Empty;
            var json = itemId switch
            {
                "100" => """
                    {
                      "item": {
                        "id": 100,
                        "name": "Root Craft",
                        "craft": [
                          {
                            "id": "1000",
                            "rlvl": 90,
                            "yield": 1,
                            "ingredients": [
                              { "id": 200, "amount": 2, "name": "Child Material" }
                            ]
                          }
                        ]
                      }
                    }
                    """,
                "200" => """
                    {
                      "item": {
                        "id": 200,
                        "name": "Child Material",
                        "craft": []
                      }
                    }
                    """,
                "300" => """
                    {
                      "item": {
                        "id": 300,
                        "name": "Resolver Selected Craft",
                        "craft": [
                          {
                            "id": "3000",
                            "rlvl": 30,
                            "job": 2,
                            "yield": 1,
                            "ingredients": [
                              { "id": 302, "amount": 5, "name": "Wrong Child" }
                            ]
                          },
                          {
                            "id": "3001",
                            "rlvl": 20,
                            "job": 3,
                            "yield": 2,
                            "ingredients": [
                              { "id": 301, "amount": 2, "name": "Selected Child" }
                            ]
                          }
                        ]
                      }
                    }
                    """,
                "301" => """
                    {
                      "item": {
                        "id": 301,
                        "name": "Selected Child",
                        "craft": []
                      }
                    }
                    """,
                "302" => """
                    {
                      "item": {
                        "id": 302,
                        "name": "Wrong Child",
                        "craft": []
                      }
                    }
                    """,
                _ => """{"item":{"id":0,"name":"Unknown","craft":[]}}"""
            };

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json)
            });
        }
    }

    private sealed class StubVendorCacheService : IVendorCacheService
    {
        public int Count => 0;

        public void Clear()
        {
        }

        public VendorCacheEntry? Get(int itemId)
        {
            return null;
        }

        public Task<VendorCacheEntry?> GetOrFetchAsync(int itemId, CancellationToken ct = default)
        {
            return Task.FromResult<VendorCacheEntry?>(null);
        }

        public Task<Dictionary<int, VendorCacheEntry>> GetOrFetchBatchAsync(
            IEnumerable<int> itemIds,
            CancellationToken ct = default)
        {
            return Task.FromResult(new Dictionary<int, VendorCacheEntry>());
        }

        public Task LoadAsync(CancellationToken ct = default)
        {
            return Task.CompletedTask;
        }

        public Task SaveAsync(CancellationToken ct = default)
        {
            return Task.CompletedTask;
        }

        public void Set(int itemId, VendorCacheEntry entry)
        {
        }
    }

    private sealed class RecordingRecipePlanBuildDiagnostics : IRecipePlanBuildDiagnosticRecorder
    {
        public List<(string Name, string Status)> Phases { get; } = [];

        public T RunPhase<T>(string name, Func<T> action)
        {
            try
            {
                var result = action();
                Phases.Add((name, "Completed"));
                return result;
            }
            catch
            {
                Phases.Add((name, "Failed"));
                throw;
            }
        }

        public void RunPhase(string name, Action action)
        {
            RunPhase(
                name,
                () =>
                {
                    action();
                    return true;
                });
        }

        public async Task<T> RunPhaseAsync<T>(
            string name,
            Func<CancellationToken, Task<T>> action,
            CancellationToken cancellationToken)
        {
            try
            {
                var result = await action(cancellationToken);
                Phases.Add((name, "Completed"));
                return result;
            }
            catch
            {
                Phases.Add((name, "Failed"));
                throw;
            }
        }

        public async Task RunPhaseAsync(
            string name,
            Func<CancellationToken, Task> action,
            CancellationToken cancellationToken)
        {
            await RunPhaseAsync(
                name,
                async ct =>
                {
                    await action(ct);
                    return true;
                },
                cancellationToken);
        }
    }
}
