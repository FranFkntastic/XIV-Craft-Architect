using FFXIV_Craft_Architect.Web.Services;

namespace FFXIV_Craft_Architect.Tests;

public class CancellableOperationServiceTests
{
    [Fact]
    public void Start_SameWorkflowCancelsPreviousLeaseAndLatestCompletionOwnsStatus()
    {
        var appState = new AppState();
        using var service = new CancellableOperationService(appState);

        using var first = service.Start(
            CancellableOperationWorkflow.MarketAnalysis,
            "Market Analysis",
            "First operation");
        using var second = service.Start(
            CancellableOperationWorkflow.MarketAnalysis,
            "Market Analysis",
            "Second operation");

        Assert.True(first.Token.IsCancellationRequested);
        Assert.False(first.IsCurrent);
        Assert.True(second.IsCurrent);
        Assert.False(first.Complete("first complete"));
        Assert.True(second.Complete("second complete"));
        Assert.Equal("second complete", appState.StatusMessage);
        Assert.False(appState.IsBusy);
    }

    [Fact]
    public void ActiveWorkflows_TracksOnlyCurrentLeases()
    {
        var appState = new AppState();
        using var service = new CancellableOperationService(appState);
        using var market = service.Start(
            CancellableOperationWorkflow.MarketAnalysis,
            "Market Analysis",
            "Analyzing");
        using var route = service.Start(
            CancellableOperationWorkflow.ProcurementAnalysis,
            "Procurement Analysis",
            "Routing");

        Assert.Equal(
            [CancellableOperationWorkflow.MarketAnalysis, CancellableOperationWorkflow.ProcurementAnalysis],
            service.ActiveWorkflows);

        Assert.True(route.Complete("Route complete"));
        Assert.Equal([CancellableOperationWorkflow.MarketAnalysis], service.ActiveWorkflows);

        service.Cancel(CancellableOperationWorkflow.MarketAnalysis);
        Assert.Empty(service.ActiveWorkflows);
    }

    [Fact]
    public void Cancel_ActiveLeaseUsesNeutralStatusWithoutFailure()
    {
        var appState = new AppState();
        using var service = new CancellableOperationService(appState);
        using var lease = service.Start(
            CancellableOperationWorkflow.ProcurementAnalysis,
            "Procurement Analysis",
            "Running");

        Assert.True(lease.Cancel());

        Assert.True(lease.Token.IsCancellationRequested);
        Assert.Equal("Ready", appState.StatusMessage);
        Assert.False(appState.IsBusy);
    }

    [Fact]
    public void CancelWorkflow_CancelsOutstandingLease()
    {
        var appState = new AppState();
        using var service = new CancellableOperationService(appState);
        using var lease = service.Start(
            CancellableOperationWorkflow.PriceRefresh,
            "Price Refresh",
            "Refreshing");

        service.Cancel(CancellableOperationWorkflow.PriceRefresh);

        Assert.True(lease.Token.IsCancellationRequested);
        Assert.False(lease.IsCurrent);
    }

    [Fact]
    public void Start_DifferentWorkflowDoesNotCancelPreviousTokenButAppStateStatusRemainsGuarded()
    {
        var appState = new AppState();
        using var service = new CancellableOperationService(appState);
        using var market = service.Start(
            CancellableOperationWorkflow.MarketAnalysis,
            "Market Analysis",
            "Analyzing");
        using var procurement = service.Start(
            CancellableOperationWorkflow.ProcurementAnalysis,
            "Procurement Analysis",
            "Routing");

        Assert.False(market.Token.IsCancellationRequested);
        Assert.False(market.ReportStatus("stale market progress"));
        Assert.True(procurement.ReportStatus("procurement progress"));
        Assert.Equal("procurement progress", appState.StatusMessage);
    }

    [Fact]
    public void CancelPlanDependentOperations_CancelsAnalysisRefreshAndTradePricingWorkflowsButKeepsBuildActive()
    {
        var appState = new AppState();
        using var service = new CancellableOperationService(appState);
        using var build = service.Start(
            CancellableOperationWorkflow.RecipeBuild,
            "Recipe Build",
            "Building");
        using var priceRefresh = service.Start(
            CancellableOperationWorkflow.PriceRefresh,
            "Price Refresh",
            "Refreshing");
        using var market = service.Start(
            CancellableOperationWorkflow.MarketAnalysis,
            "Market Analysis",
            "Analyzing");
        using var procurement = service.Start(
            CancellableOperationWorkflow.ProcurementAnalysis,
            "Procurement Analysis",
            "Routing");
        using var itemRefresh = service.Start(
            CancellableOperationWorkflow.ItemMarketRefresh,
            "Item Refresh",
            "Refreshing item");
        using var tradePricing = service.Start(
            CancellableOperationWorkflow.TradeOrderPricing,
            "Trade Order Pricing",
            "Pricing order");

        service.CancelPlanDependentOperations();

        Assert.False(build.Token.IsCancellationRequested);
        Assert.True(build.IsCurrent);
        Assert.True(priceRefresh.Token.IsCancellationRequested);
        Assert.True(market.Token.IsCancellationRequested);
        Assert.True(procurement.Token.IsCancellationRequested);
        Assert.True(itemRefresh.Token.IsCancellationRequested);
        Assert.True(tradePricing.Token.IsCancellationRequested);
        Assert.False(tradePricing.IsCurrent);
    }

    [Fact]
    public void ShouldReportError_ReturnsFalseForOwnCancellationAndTrueForFailures()
    {
        var appState = new AppState();
        using var service = new CancellableOperationService(appState);
        using var lease = service.Start(
            CancellableOperationWorkflow.RecipeBuild,
            "Recipe Build",
            "Building");

        Assert.True(lease.ShouldReportError(new InvalidOperationException("boom")));

        lease.Cancel();

        Assert.False(lease.ShouldReportError(new OperationCanceledException(lease.Token)));
        Assert.False(lease.ShouldReportError(new InvalidOperationException("boom")));
    }
}
