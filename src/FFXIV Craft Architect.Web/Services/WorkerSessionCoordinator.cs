using System.Text.Json;
using FFXIV_Craft_Architect.Core.Engine;
using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Web.Services;

/// <summary>
/// Main-thread command facade for the Worker-owned durable session.
/// </summary>
public sealed class WorkerSessionCoordinator : IAsyncDisposable
{
    private readonly CraftArchitectEngineHost _engineHost;
    private readonly WorkerProjectionStore _projections;
    private readonly ExperimentalProcurementEngineCapability _capability;

    public WorkerSessionCoordinator(
        CraftArchitectEngineHost engineHost,
        WorkerProjectionStore projections,
        ExperimentalProcurementEngineCapability capability)
    {
        _engineHost = engineHost;
        _projections = projections;
        _capability = capability;
    }

    public bool IsEnabled => _capability.IsExecutionEnabled;

    public async Task<WorkerSessionShellProjection?> BootstrapAsync(
        CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
        {
            return null;
        }

        var result = await _engineHost.BootstrapSessionAsync(cancellationToken);
        if (!result.Accepted || !_projections.TryPublish(result))
        {
            throw new InvalidOperationException(
                result.Message ?? "The Worker did not publish a valid startup projection.");
        }
        return _projections.Shell;
    }

    public async Task<StoredPlan?> ExportStoredPlanAsync(
        string planId,
        string planName,
        bool includeSourcePlanIdentity = true,
        CancellationToken cancellationToken = default)
    {
        var result = await _engineHost.ExportSessionAsync(
            _projections.Shell.Revision,
            new WorkerSessionExportRequest(
                planId,
                planName,
                includeSourcePlanIdentity,
                IncludeLegacyMarketAnalysisFields: true),
            cancellationToken);
        var export = result.Projection.Deserialize<WorkerSessionExportProjection>(
            EngineJsonSerializerOptions.CreateWire());
        if (!result.Accepted || export is null)
        {
            throw CreateConflict(result);
        }

        return export.StoredPlan;
    }

    public async Task ReplaceStoredPlanAsync(
        StoredPlan storedPlan,
        bool trackStoredPlanIdentity,
        CancellationToken cancellationToken = default) =>
        await ReplaceStoredPlanCoreAsync(
            storedPlan,
            trackStoredPlanIdentity,
            cancellationToken);

    public async Task ClearSessionAsync(
        CancellationToken cancellationToken = default) =>
        await ReplaceStoredPlanCoreAsync(
            storedPlan: null,
            trackStoredPlanIdentity: false,
            cancellationToken);

    private async Task ReplaceStoredPlanCoreAsync(
        StoredPlan? storedPlan,
        bool trackStoredPlanIdentity,
        CancellationToken cancellationToken)
    {
        var result = await _engineHost.ReplaceSessionAsync(
            _projections.Shell.Revision,
            storedPlan,
            trackStoredPlanIdentity,
            cancellationToken);
        if (!result.Accepted || !_projections.TryPublish(result))
        {
            await RefreshAfterConflictAsync(result, cancellationToken);
            throw CreateConflict(result);
        }

        var recipe = await _engineHost.GetRecipeProjectionAsync(
            result.Revision,
            cancellationToken);
        _projections.TryPublishRecipe(recipe);
        var market = await _engineHost.GetMarketProjectionAsync(
            result.Revision,
            cancellationToken);
        _projections.TryPublishMarket(market);
        var procurement = await _engineHost.GetProcurementProjectionAsync(
            result.Revision,
            cancellationToken);
        _projections.TryPublishProcurement(procurement);
    }

    public async Task<WorkerRecipePlannerProjection?> GetRecipeProjectionAsync(
        CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
        {
            return null;
        }

        var result = await _engineHost.GetRecipeProjectionAsync(
            _projections.Shell.Revision,
            cancellationToken);
        if (!_projections.TryPublishRecipe(result))
        {
            await RefreshAfterConflictAsync(result, cancellationToken);
            return _projections.Recipe;
        }
        return _projections.Recipe;
    }

    public async Task<WorkerAcquisitionProjection?> GetAcquisitionProjectionAsync(
        string filter,
        CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
        {
            return null;
        }

        var result = await _engineHost.GetAcquisitionProjectionAsync(
            _projections.Shell.Revision,
            filter,
            cancellationToken);
        if (!_projections.TryPublishAcquisition(result))
        {
            await RefreshAfterConflictAsync(result, cancellationToken);
            return _projections.Acquisition;
        }
        return _projections.Acquisition;
    }

    public async Task<WorkerMarketProjection?> GetMarketProjectionAsync(
        CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
        {
            return null;
        }

        var result = await _engineHost.GetMarketProjectionAsync(
            _projections.Shell.Revision,
            cancellationToken);
        if (!_projections.TryPublishMarket(result))
        {
            await RefreshAfterConflictAsync(result, cancellationToken);
            return _projections.Market;
        }
        return _projections.Market;
    }

    public async Task<WorkerProcurementProjection?> GetProcurementProjectionAsync(
        CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
        {
            return null;
        }

        var result = await _engineHost.GetProcurementProjectionAsync(
            _projections.Shell.Revision,
            cancellationToken);
        if (!_projections.TryPublishProcurement(result))
        {
            await RefreshAfterConflictAsync(result, cancellationToken);
            return _projections.Procurement;
        }
        return _projections.Procurement;
    }

    public async Task<WorkerRecipePlannerProjection> MutateProjectItemsAsync(
        WorkerProjectItemsMutation mutation,
        CancellationToken cancellationToken = default)
    {
        var result = await _engineHost.MutateProjectItemsAsync(
            _projections.Shell.Revision,
            mutation,
            cancellationToken);
        if (!_projections.TryPublishMutation<WorkerRecipePlannerProjection>(
                result,
                out var projection) ||
            projection is null)
        {
            await RefreshAfterConflictAsync(result, cancellationToken);
            throw CreateConflict(result);
        }

        return projection;
    }

    public async Task<WorkerRecipeBuildOutcome> BuildRecipeAsync(
        WorkerRecipeBuildRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await _engineHost.BuildRecipeAsync(
            _projections.Shell.Revision,
            request,
            cancellationToken);
        if (!_projections.TryPublishMutation<WorkerRecipeBuildOutcome>(
                result,
                out var outcome) ||
            outcome is null)
        {
            await RefreshAfterConflictAsync(result, cancellationToken);
            throw CreateConflict(result);
        }

        return outcome;
    }

    public async Task<WorkerRecipePlannerProjection> MutateAcquisitionAsync(
        WorkerAcquisitionMutation mutation,
        CancellationToken cancellationToken = default)
    {
        var result = await _engineHost.MutateAcquisitionAsync(
            _projections.Shell.Revision,
            mutation,
            cancellationToken);
        if (!_projections.TryPublishMutation<WorkerRecipePlannerProjection>(
                result,
                out var projection) ||
            projection is null)
        {
            await RefreshAfterConflictAsync(result, cancellationToken);
            throw CreateConflict(result);
        }

        return projection;
    }

    public async Task<WorkerAcquisitionProjection> MutateAcquisitionAndProjectAsync(
        WorkerAcquisitionMutation mutation,
        string filter,
        CancellationToken cancellationToken = default)
    {
        await MutateAcquisitionAsync(mutation, cancellationToken);
        return await GetAcquisitionProjectionAsync(filter, cancellationToken)
            ?? throw new InvalidOperationException("The Worker did not publish acquisition evaluation.");
    }

    public async Task<WorkerMarketAnalysisOutcome> RunMarketAnalysisAsync(
        WorkerMarketAnalysisRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await _engineHost.RunMarketAnalysisAsync(
            _projections.Shell.Revision,
            request,
            cancellationToken);
        if (!_projections.TryPublishMutation<WorkerMarketAnalysisOutcome>(
                result,
                out var outcome) ||
            outcome is null)
        {
            await RefreshAfterConflictAsync(result, cancellationToken);
            throw CreateConflict(result);
        }

        return outcome;
    }

    public async Task<WorkerMarketProjection> ApplyMarketLensAsync(
        MarketAcquisitionLens lens,
        CancellationToken cancellationToken = default)
    {
        var result = await _engineHost.ApplyMarketLensAsync(
            _projections.Shell.Revision,
            new WorkerMarketLensMutation(lens),
            cancellationToken);
        if (!_projections.TryPublishMutation<WorkerMarketProjection>(
                result,
                out var market) ||
            market is null)
        {
            await RefreshAfterConflictAsync(result, cancellationToken);
            throw CreateConflict(result);
        }

        return market;
    }

    public async Task<WorkerMarketItemRefreshOutcome> RefreshMarketItemAsync(
        WorkerMarketItemRefreshRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await _engineHost.RefreshMarketItemAsync(
            _projections.Shell.Revision,
            request,
            cancellationToken);
        if (!_projections.TryPublishMutation<WorkerMarketItemRefreshOutcome>(
                result,
                out var outcome) ||
            outcome is null)
        {
            await RefreshAfterConflictAsync(result, cancellationToken);
            throw CreateConflict(result);
        }

        return outcome;
    }

    public async Task<WorkerProcurementOutcome> RunProcurementAsync(
        WorkerProcurementRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await _engineHost.RunProcurementAsync(
            _projections.Shell.Revision,
            request,
            cancellationToken);
        if (!_projections.TryPublishMutation<WorkerProcurementOutcome>(
                result,
                out var outcome) ||
            outcome is null)
        {
            await RefreshAfterConflictAsync(result, cancellationToken);
            throw CreateConflict(result);
        }

        return outcome;
    }

    public async Task<WorkerProcurementProjection> SelectProcurementToleranceAsync(
        int travelTolerance,
        CancellationToken cancellationToken = default)
    {
        var result = await _engineHost.SelectProcurementToleranceAsync(
            _projections.Shell.Revision,
            travelTolerance,
            cancellationToken);
        if (!_projections.TryPublishMutation<WorkerProcurementProjection>(
                result,
                out var procurement) ||
            procurement is null)
        {
            await RefreshAfterConflictAsync(result, cancellationToken);
            throw CreateConflict(result);
        }

        return procurement;
    }

    private async Task RefreshAfterConflictAsync(
        WorkerSessionResultEnvelope result,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(result.RejectionCode, "stale-revision", StringComparison.Ordinal))
        {
            return;
        }

        var shell = await _engineHost.GetShellProjectionAsync(
            result.Revision,
            cancellationToken);
        _projections.TryPublish(shell);
        var recipe = await _engineHost.GetRecipeProjectionAsync(
            result.Revision,
            cancellationToken);
        _projections.TryPublishRecipe(recipe);
    }

    private static InvalidOperationException CreateConflict(
        WorkerSessionResultEnvelope result) =>
        new(
            result.Message ??
            "The plan changed before this edit was accepted. The current Worker projection has been restored.");

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}
