using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Web.Services;
using Microsoft.JSInterop;

namespace FFXIV_Craft_Architect.Tests;

public class IndexedDbServiceAutoSaveTests
{
    [Fact]
    public async Task LoadPlanSummariesAsync_UsesSummaryEndpoint()
    {
        var jsRuntime = new RecordingJsRuntime();
        var service = new IndexedDbService(jsRuntime);

        var summaries = await service.LoadPlanSummariesAsync();

        Assert.Single(summaries);
        Assert.Equal("IndexedDB.loadPlanSummaries", jsRuntime.LastIdentifier);
        Assert.Equal(0, jsRuntime.LoadAllPlansCallCount);
    }

    [Fact]
    public async Task AutoSaveStateAsync_SkipsSecondSaveWhenStateIsClean()
    {
        var jsRuntime = new RecordingJsRuntime();
        var service = new IndexedDbService(jsRuntime);
        var appState = new AppState
        {
            ProjectItems =
            [
                new ProjectItem
                {
                    Id = 123,
                    Name = "Saved Item",
                    Quantity = 10
                }
            ]
        };

        var firstSave = await service.AutoSaveStateAsync(appState);
        var secondSave = await service.AutoSaveStateAsync(appState);

        Assert.True(firstSave);
        Assert.False(secondSave);
        Assert.Equal(1, jsRuntime.SavePlanCallCount);
    }

    [Fact]
    public async Task AutoSaveStateAsync_ExplicitSaveWaitsForInFlightSaveAndWritesNewDirtyState()
    {
        var jsRuntime = new RecordingJsRuntime(manualCompletion: true);
        var service = new IndexedDbService(jsRuntime);
        var appState = new AppState
        {
            ProjectItems =
            [
                new ProjectItem
                {
                    Id = 123,
                    Name = "Saved Item",
                    Quantity = 10
                }
            ]
        };

        var firstSave = service.AutoSaveStateAsync(appState);
        await jsRuntime.WaitForSavePlanCallCountAsync(1);

        appState.ShoppingPlans.Add(new DetailedShoppingPlan { ItemId = 123, QuantityNeeded = 10 });
        appState.MarketItemAnalyses.Add(new MarketItemAnalysis { ItemId = 123, QuantityNeeded = 10 });
        appState.NotifyShoppingListChanged();

        var secondSave = service.AutoSaveStateAsync(appState);
        Assert.False(secondSave.IsCompleted);

        jsRuntime.CompleteNextSave();
        Assert.True(await firstSave);

        await jsRuntime.WaitForSavePlanCallCountAsync(2);
        jsRuntime.CompleteNextSave();

        Assert.True(await secondSave);
        Assert.Equal(2, jsRuntime.SavePlanCallCount);
    }

    [Fact]
    public async Task SaveMarketAnalysisAsync_UsesPatchEndpointWithoutLoadingFullPlan()
    {
        var jsRuntime = new RecordingJsRuntime();
        var service = new IndexedDbService(jsRuntime);

        var saved = await service.SaveMarketAnalysisAsync(
            "plan-id",
            [new DetailedShoppingPlan { ItemId = 100, QuantityNeeded = 2 }],
            [new MarketItemAnalysis { ItemId = 100, QuantityNeeded = 2 }],
            RecommendationMode.MaximizeValue,
            MarketAcquisitionLens.BulkValue);

        Assert.True(saved);
        Assert.Equal("IndexedDB.patchMarketAnalysis", jsRuntime.LastIdentifier);
        Assert.Equal(0, jsRuntime.LoadPlanCallCount);
    }

    private sealed class RecordingJsRuntime : IJSRuntime
    {
        private readonly bool _manualCompletion;
        private readonly Queue<TaskCompletionSource<bool>> _pendingSaves = new();

        public RecordingJsRuntime(bool manualCompletion = false)
        {
            _manualCompletion = manualCompletion;
        }

        public int SavePlanCallCount { get; private set; }
        public int LoadPlanCallCount { get; private set; }
        public int LoadAllPlansCallCount { get; private set; }
        public string? LastIdentifier { get; private set; }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            LastIdentifier = identifier;

            if (identifier == "IndexedDB.savePlan")
            {
                SavePlanCallCount++;
                if (!_manualCompletion)
                {
                    return new ValueTask<TValue>((TValue)(object)true);
                }

                var saveCompletion = new TaskCompletionSource<bool>();
                _pendingSaves.Enqueue(saveCompletion);
                return new ValueTask<TValue>(CompleteSaveAsync<TValue>(saveCompletion.Task));
            }

            if (identifier == "IndexedDB.loadPlanSummaries")
            {
                var summaries = new List<StoredPlanSummary>
                {
                    new()
                    {
                        Id = "saved-plan",
                        Name = "Saved Plan",
                        DataCenter = "Aether",
                        ItemCount = 2
                    }
                };

                return new ValueTask<TValue>((TValue)(object)summaries);
            }

            if (identifier == "IndexedDB.loadAllPlans")
            {
                LoadAllPlansCallCount++;
                return new ValueTask<TValue>((TValue)(object)new List<StoredPlan>());
            }

            if (identifier == "IndexedDB.loadPlan")
            {
                LoadPlanCallCount++;
                return new ValueTask<TValue>((TValue)(object?)null!);
            }

            if (identifier == "IndexedDB.patchMarketAnalysis")
            {
                return new ValueTask<TValue>((TValue)(object)true);
            }

            throw new InvalidOperationException($"Unexpected JS invocation: {identifier}");
        }

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args)
        {
            return InvokeAsync<TValue>(identifier, args);
        }

        public void CompleteNextSave()
        {
            _pendingSaves.Dequeue().SetResult(true);
        }

        public async Task WaitForSavePlanCallCountAsync(int expectedCount)
        {
            while (SavePlanCallCount < expectedCount)
            {
                await Task.Delay(10);
            }
        }

        private static async Task<TValue> CompleteSaveAsync<TValue>(Task<bool> saveTask)
        {
            var result = await saveTask;
            return (TValue)(object)result;
        }
    }
}
