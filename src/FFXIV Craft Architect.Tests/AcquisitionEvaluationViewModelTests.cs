using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Core.Services.Interfaces;
using FFXIV_Craft_Architect.ViewModels;

namespace FFXIV_Craft_Architect.Tests;

public class AcquisitionEvaluationViewModelTests
{
    [Fact]
    public async Task RefreshAsync_ProjectsCoreSnapshotRowsAndCounts()
    {
        var host = CreateHost();
        var viewModel = host.ViewModel;

        await viewModel.RefreshAsync();

        Assert.True(viewModel.HasPlan);
        Assert.Equal(2, viewModel.UniqueItemCount);
        Assert.Equal(2, viewModel.MarketCandidateCount);
        Assert.Equal(1, viewModel.ActiveProcurementItemCount);
        Assert.Equal(1, viewModel.AnalyzedCount);

        var material = Assert.Single(viewModel.Rows, row => row.ItemId == 200);
        Assert.Equal("Material", material.ItemName);
        Assert.Equal(AcquisitionSource.MarketBuyNq, material.Source);
        Assert.Equal("Market Buy (NQ)", material.SourceDisplay);
        Assert.Equal(AcquisitionSource.MarketBuyNq, material.SelectedSource);
        Assert.Contains(material.SourceOptions, option => option.Source == AcquisitionSource.MarketBuyHq);
        Assert.Contains(material.SourceOptions, option => option.Source == AcquisitionSource.VendorBuy);
        Assert.True(material.CanChangeSource);
        Assert.False(material.IsMarketHq);
        Assert.True(material.CanChangeMarketHq);
        Assert.Equal(2, material.TotalQuantity);
        Assert.Equal(2, material.ActiveQuantity);
        Assert.Contains("Siren", material.MarketEvidence);
        Assert.Equal("400g", material.EstimatedCost);
        Assert.Same(material, viewModel.SelectedRow);
        var selectedRow = Assert.IsType<AcquisitionDecisionRowViewModel>(viewModel.SelectedRow);
        Assert.Equal("2 active / 2 total", selectedRow.QuantityDisplay);
        Assert.Contains(selectedRow.OptionRows, option =>
            option.Source == AcquisitionSource.MarketBuyNq &&
            option.CostText == "400g" &&
            option.IsAvailable);
        Assert.Contains(selectedRow.OptionRows, option =>
            option.Source == AcquisitionSource.VendorBuy &&
            option.CostText == "150g" &&
            option.IsAvailable);
    }

    [Fact]
    public async Task RefreshAsync_MarksUnsupportedHqOptionAsProjectedUnavailable()
    {
        var host = CreateHost(CreateUnsupportedHqShoppingPlan());
        var viewModel = host.ViewModel;

        await viewModel.RefreshAsync();

        var material = Assert.Single(viewModel.Rows, row => row.ItemId == 200);
        var hqOption = Assert.Single(material.OptionRows, option => option.Source == AcquisitionSource.MarketBuyHq);

        Assert.Equal("16,000g", hqOption.CostText);
        Assert.False(hqOption.IsAvailable);
        Assert.True(hqOption.IsProjectedUnsupported);
        Assert.Contains("current search scope cannot fill", hqOption.Detail);
    }

    [Fact]
    public async Task CurrentFilter_ReprojectsVisibleRowsThroughLedgerCache()
    {
        var host = CreateHost();
        var viewModel = host.ViewModel;

        await viewModel.RefreshAsync();
        viewModel.CurrentFilter = CoreAcquisitionFilter.Active;

        Assert.Single(viewModel.Rows);
        Assert.Equal(200, Assert.Single(viewModel.Rows).ItemId);
    }

    [Fact]
    public async Task ChangeSourceAsync_PublishesCoreDecisionAndRefreshesRows()
    {
        var host = CreateHost();
        var viewModel = host.ViewModel;
        host.Session.PublishProcurementOverlay(
            new CraftSessionProcurementOverlay(DateTime.UtcNow, [200], "route", []),
            "route generated");

        await viewModel.RefreshAsync();
        var result = await viewModel.ChangeSourceAsync(200, AcquisitionSource.VendorBuy);

        Assert.True(result.Changed);
        Assert.Null(host.Session.ProcurementOverlay);
        var material = Assert.Single(viewModel.Rows, row => row.ItemId == 200);
        Assert.Equal(AcquisitionSource.VendorBuy, material.Source);
        Assert.Equal("Vendor Buy", material.SourceDisplay);
    }

    [Fact]
    public async Task ChangeMarketHqAsync_PublishesCoreDecisionAndRefreshesRows()
    {
        var host = CreateHost();
        var viewModel = host.ViewModel;

        await viewModel.RefreshAsync();
        var result = await viewModel.ChangeMarketHqAsync(200, isHq: true);

        Assert.True(result.Changed);
        var material = Assert.Single(viewModel.Rows, row => row.ItemId == 200);
        Assert.True(material.IsMarketHq);
        Assert.Equal(AcquisitionSource.MarketBuyHq, material.Source);
        Assert.Equal(AcquisitionSource.MarketBuyHq, material.SelectedSource);
    }

    [Fact]
    public async Task RefreshAsync_PreservesSelectedRowAcrossReprojection()
    {
        var host = CreateHost();
        var viewModel = host.ViewModel;

        await viewModel.RefreshAsync();
        var root = Assert.Single(viewModel.Rows, row => row.ItemId == 100);
        viewModel.SelectedRow = root;
        await viewModel.ChangeSourceAsync(200, AcquisitionSource.VendorBuy);

        Assert.NotNull(viewModel.SelectedRow);
        Assert.Equal(100, viewModel.SelectedRow.ItemId);
    }

    [Fact]
    public async Task RefreshAsync_WhenStaleRefreshCompletesAfterCurrentRefresh_PreservesCurrentRows()
    {
        var session = new CraftSessionState(new ImmediateCraftSessionDispatcher());
        var operationState = new CraftOperationState();
        var coordinator = new CraftOperationCoordinator(session, operationState);
        var recipeLayer = new DelayedRecipeLayerWorkflowService();
        var viewModel = new AcquisitionEvaluationViewModel(
            session,
            new CoreAcquisitionEvaluationWorkflowService(recipeLayer),
            new CoreAcquisitionEvaluationLedgerCache(),
            new CoreAcquisitionDecisionService(session, coordinator));

        var firstPlan = CreateNamedPlan(100, "Old Craft");
        session.ActivatePlan(
            firstPlan,
            [],
            new CraftSessionActiveContext(null, "Aether", null, null),
            "old plan");

        var staleRefresh = viewModel.RefreshAsync();
        Assert.True(recipeLayer.WaitingForPlan(100));

        var currentPlan = CreateNamedPlan(300, "Current Craft");
        session.ActivatePlan(
            currentPlan,
            [],
            new CraftSessionActiveContext(null, "Aether", null, null),
            "current plan");
        await viewModel.RefreshAsync();

        Assert.Contains(viewModel.Rows, row => row.ItemId == 400);

        recipeLayer.ReleasePlan(100);
        await staleRefresh;

        Assert.Contains(viewModel.Rows, row => row.ItemId == 400);
        Assert.DoesNotContain(viewModel.Rows, row => row.ItemId == 200);
        Assert.True(viewModel.HasPlan);
    }

    private static TestHost CreateHost(DetailedShoppingPlan? shoppingPlan = null)
    {
        var session = new CraftSessionState(new ImmediateCraftSessionDispatcher());
        var operationState = new CraftOperationState();
        var coordinator = new CraftOperationCoordinator(session, operationState);
        var projectionService = new RecipeDemandProjectionService();
        var recipeLayer = new StubRecipeLayerWorkflowService(projectionService);
        var workflow = new CoreAcquisitionEvaluationWorkflowService(recipeLayer);
        var decisionService = new CoreAcquisitionDecisionService(session, coordinator);
        var viewModel = new AcquisitionEvaluationViewModel(
            session,
            workflow,
            new CoreAcquisitionEvaluationLedgerCache(),
            decisionService);

        var plan = CreatePlan();
        session.ActivatePlan(
            plan,
            [],
            new CraftSessionActiveContext(null, "Aether", null, null),
            "plan loaded");

        var currentPlan = session.ActivePlan!;
        session.TryPublishMarketAnalysis(
            session.CaptureVersionStamp(),
            currentPlan,
            session.PlanSessionVersion,
            [],
            [shoppingPlan ?? CreateMaterialShoppingPlan()],
            acquisitionDecisionsChanged: false,
            "market analysis");

        return new TestHost(session, viewModel);
    }

    private static CraftingPlan CreatePlan()
    {
        var root = new PlanNode
        {
            ItemId = 100,
            Name = "Final Craft",
            Quantity = 1,
            Source = AcquisitionSource.Craft,
            CanCraft = true,
            Yield = 1
        };

        root.Children.Add(new PlanNode
        {
            ItemId = 200,
            Name = "Material",
            Quantity = 2,
            Source = AcquisitionSource.MarketBuyNq,
            CanBuyFromMarket = true,
            CanBuyFromVendor = true,
            CanBeHq = true,
            MarketPrice = 250,
            HqMarketPrice = 300,
            VendorPrice = 75,
            Parent = root
        });

        return new CraftingPlan { RootItems = [root] };
    }

    private static CraftingPlan CreateNamedPlan(int rootItemId, string rootName)
    {
        var root = new PlanNode
        {
            ItemId = rootItemId,
            Name = rootName,
            Quantity = 1,
            Source = AcquisitionSource.Craft,
            CanCraft = true,
            Yield = 1
        };

        root.Children.Add(new PlanNode
        {
            ItemId = rootItemId + 100,
            Name = $"{rootName} Material",
            Quantity = 2,
            Source = AcquisitionSource.MarketBuyNq,
            CanBuyFromMarket = true,
            MarketPrice = 10,
            Parent = root
        });

        return new CraftingPlan { RootItems = [root] };
    }

    private static DetailedShoppingPlan CreateMaterialShoppingPlan() =>
        new()
        {
            ItemId = 200,
            Name = "Material",
            QuantityNeeded = 2,
            RecommendedWorld = new WorldShoppingSummary
            {
                WorldName = "Siren",
                TotalCost = 400,
                TotalQuantityPurchased = 2
            }
        };

    private static DetailedShoppingPlan CreateUnsupportedHqShoppingPlan() =>
        new()
        {
            ItemId = 200,
            Name = "Material",
            QuantityNeeded = 2,
            HQAveragePrice = 8_000,
            WorldOptions =
            [
                new WorldShoppingSummary
                {
                    DataCenter = "Aether",
                    WorldName = "Siren",
                    Listings =
                    [
                        new ShoppingListingEntry
                        {
                            Quantity = 1,
                            PricePerUnit = 3_000,
                            IsHq = true
                        }
                    ]
                }
            ]
        };

    private sealed class StubRecipeLayerWorkflowService : ICoreRecipeLayerWorkflowService
    {
        private readonly RecipeDemandProjectionService _projectionService;

        public StubRecipeLayerWorkflowService(RecipeDemandProjectionService projectionService)
        {
            _projectionService = projectionService;
        }

        public RecipeOperationSnapshotIdentity CreateSnapshotIdentity() => RecipeOperationSnapshotIdentity.Unspecified;

        public RecipeDemandProjection BuildDemandProjection(CraftingPlan? plan) =>
            _projectionService.Build(plan, snapshot: null);

        public IReadOnlyList<MaterialAggregate> BuildMarketAnalysisCandidates(CraftingPlan? plan) =>
            BuildDemandProjection(plan).ToMarketAnalysisMaterialAggregates();

        public IReadOnlyList<MaterialAggregate> BuildActiveProcurementItems(CraftingPlan? plan) =>
            BuildDemandProjection(plan).ToActiveProcurementMaterialAggregates();

        public Task<RecipeDemandProjection?> BuildCurrentDemandProjectionAsync(
            CraftingPlan? plan,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<RecipeDemandProjection?>(BuildDemandProjection(plan));

        public Task<IReadOnlyList<MaterialAggregate>?> BuildCurrentMarketAnalysisCandidatesAsync(
            CraftingPlan? plan,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MaterialAggregate>?>(BuildMarketAnalysisCandidates(plan));

        public Task<IReadOnlyList<MaterialAggregate>?> BuildCurrentActiveProcurementItemsAsync(
            CraftingPlan? plan,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MaterialAggregate>?>(BuildActiveProcurementItems(plan));
    }

    private sealed class DelayedRecipeLayerWorkflowService : ICoreRecipeLayerWorkflowService
    {
        private readonly RecipeDemandProjectionService _projectionService = new();
        private readonly Dictionary<int, TaskCompletionSource> _delays = new();

        public RecipeOperationSnapshotIdentity CreateSnapshotIdentity() => RecipeOperationSnapshotIdentity.Unspecified;

        public RecipeDemandProjection BuildDemandProjection(CraftingPlan? plan) =>
            _projectionService.Build(plan, snapshot: null);

        public IReadOnlyList<MaterialAggregate> BuildMarketAnalysisCandidates(CraftingPlan? plan) =>
            BuildDemandProjection(plan).ToMarketAnalysisMaterialAggregates();

        public IReadOnlyList<MaterialAggregate> BuildActiveProcurementItems(CraftingPlan? plan) =>
            BuildDemandProjection(plan).ToActiveProcurementMaterialAggregates();

        public async Task<RecipeDemandProjection?> BuildCurrentDemandProjectionAsync(
            CraftingPlan? plan,
            CancellationToken cancellationToken = default)
        {
            if (plan?.RootItems.FirstOrDefault()?.ItemId == 100)
            {
                var delay = GetDelay(100);
                using (cancellationToken.Register(() => delay.TrySetCanceled(cancellationToken)))
                {
                    await delay.Task;
                }
            }

            return BuildDemandProjection(plan);
        }

        public Task<IReadOnlyList<MaterialAggregate>?> BuildCurrentMarketAnalysisCandidatesAsync(
            CraftingPlan? plan,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MaterialAggregate>?>(BuildMarketAnalysisCandidates(plan));

        public Task<IReadOnlyList<MaterialAggregate>?> BuildCurrentActiveProcurementItemsAsync(
            CraftingPlan? plan,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MaterialAggregate>?>(BuildActiveProcurementItems(plan));

        public bool WaitingForPlan(int rootItemId) => _delays.ContainsKey(rootItemId);

        public void ReleasePlan(int rootItemId) => GetDelay(rootItemId).TrySetResult();

        private TaskCompletionSource GetDelay(int rootItemId)
        {
            if (!_delays.TryGetValue(rootItemId, out var delay))
            {
                delay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _delays[rootItemId] = delay;
            }

            return delay;
        }
    }

    private sealed record TestHost(
        CraftSessionState Session,
        AcquisitionEvaluationViewModel ViewModel);
}
