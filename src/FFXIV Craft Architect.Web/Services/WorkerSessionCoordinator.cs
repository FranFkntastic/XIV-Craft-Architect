using System.Text.Json;
using System.Text.RegularExpressions;
using FFXIV_Craft_Architect.Core.Engine;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Core.Services.Interfaces;

namespace FFXIV_Craft_Architect.Web.Services;

/// <summary>
/// Main-thread command facade for the Worker-owned durable session.
/// </summary>
public sealed class WorkerSessionCoordinator : IAsyncDisposable
{
    private static readonly TimeSpan HistoricalDetailCacheMaxAge =
        TimeSpan.FromDays(3650);
    private readonly CraftArchitectEngineHost _engineHost;
    private readonly WorkerProjectionStore _projections;
    private readonly CraftArchitectEngineCapability _capability;
    private readonly IMarketEvidenceReconciliationService _marketEvidenceReconciliation;
    private readonly IMarketCacheService _marketCache;
    private readonly IUniversalisService _universalis;
    private readonly SemaphoreSlim _crossTabProjectionGate = new(1, 1);
    private bool _disposed;

    public WorkerSessionCoordinator(
        CraftArchitectEngineHost engineHost,
        WorkerProjectionStore projections,
        CraftArchitectEngineCapability capability,
        IMarketEvidenceReconciliationService marketEvidenceReconciliation,
        IMarketCacheService marketCache,
        IUniversalisService universalis)
    {
        _engineHost = engineHost;
        _projections = projections;
        _capability = capability;
        _marketEvidenceReconciliation = marketEvidenceReconciliation;
        _marketCache = marketCache;
        _universalis = universalis;
        _engineHost.CrossTabSessionProjectionReceived += OnCrossTabSessionProjectionReceived;
    }

    public bool IsEnabled => _capability.IsExecutionEnabled;
    public long CurrentRevision => _projections.Shell.Revision;
    internal bool IsOperationCurrent(Guid operationId) =>
        _projections.Operation is
        {
            IsActive: true,
            OperationId: var activeOperationId
        } &&
        activeOperationId == operationId;

    public async Task<WorkerSessionOperationLease> BeginOperationAsync(
        WorkerSessionOperationKind kind,
        string intentKey,
        string statusMessage,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(intentKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(statusMessage);
        var operationId = Guid.NewGuid();
        WorkerSessionResultEnvelope result = null!;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            result = await _engineHost.BeginOperationAsync(
                _projections.Shell.Revision,
                new WorkerSessionOperationBeginRequest(
                    operationId,
                    kind,
                    intentKey,
                    statusMessage),
                cancellationToken);
            if (!string.Equals(
                    result.RejectionCode,
                    "stale-revision",
                    StringComparison.Ordinal))
            {
                break;
            }

            await RefreshAfterConflictAsync(result, cancellationToken);
        }

        if (!result.Accepted || !_projections.TryPublish(result))
        {
            throw CreateConflict(result);
        }

        var operation = _projections.Operation;
        if (operation is not
            {
                IsActive: true,
                Disposition: WorkerSessionOperationDisposition.Acquired
                    or WorkerSessionOperationDisposition.Current
            } ||
            operation.OperationId != operationId)
        {
            throw new WorkerSessionOperationBusyException(
                operation?.StatusMessage ?? "Another plan update is already running.");
        }

        return new WorkerSessionOperationLease(this, operationId, kind, intentKey);
    }

    internal async Task<bool> RenewOperationAsync(
        Guid operationId,
        CancellationToken cancellationToken)
    {
        var result = await _engineHost.RenewOperationAsync(
            _projections.Shell.Revision,
            operationId,
            cancellationToken);
        if (string.Equals(result.RejectionCode, "stale-revision", StringComparison.Ordinal))
        {
            await RefreshAfterConflictAsync(result, cancellationToken);
            result = await _engineHost.RenewOperationAsync(
                _projections.Shell.Revision,
                operationId,
                cancellationToken);
        }

        return result.Accepted &&
               _projections.TryPublish(result) &&
               _projections.Operation is
               {
                   IsActive: true,
                   OperationId: var activeOperationId
               } &&
               activeOperationId == operationId;
    }

    internal Task CompleteOperationAsync(
        Guid operationId,
        CancellationToken cancellationToken) =>
        EndOperationAsync(operationId, complete: true, cancellationToken);

    internal Task AbortOperationAsync(
        Guid operationId,
        CancellationToken cancellationToken) =>
        EndOperationAsync(operationId, complete: false, cancellationToken);

    private async Task EndOperationAsync(
        Guid operationId,
        bool complete,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = complete
                ? await _engineHost.CompleteOperationAsync(
                    _projections.Shell.Revision,
                    operationId,
                    cancellationToken)
                : await _engineHost.AbortOperationAsync(
                    _projections.Shell.Revision,
                    operationId,
                    cancellationToken);
            if (string.Equals(
                    result.RejectionCode,
                    "stale-revision",
                    StringComparison.Ordinal))
            {
                await RefreshAfterConflictAsync(result, cancellationToken);
                result = complete
                    ? await _engineHost.CompleteOperationAsync(
                        _projections.Shell.Revision,
                        operationId,
                        cancellationToken)
                    : await _engineHost.AbortOperationAsync(
                        _projections.Shell.Revision,
                        operationId,
                        cancellationToken);
            }

            if (result.Accepted)
            {
                _projections.TryPublish(result);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task<TResult> RunWithOperationAsync<TResult>(
        WorkerSessionOperationKind kind,
        string intentKey,
        string statusMessage,
        Guid? operationId,
        CancellationToken cancellationToken,
        Func<Guid, Task<TResult>> run)
    {
        if (operationId is { } existingOperationId)
        {
            return await run(existingOperationId);
        }

        await using var operation = await BeginOperationAsync(
            kind,
            intentKey,
            statusMessage,
            cancellationToken);
        try
        {
            var result = await run(operation.OperationId);
            await operation.CompleteAsync(cancellationToken);
            return result;
        }
        catch
        {
            await operation.AbortAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<WorkerSessionShellProjection?> BootstrapAsync(
        CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
        {
            return null;
        }

        var result = await _engineHost.BootstrapSessionAsync(cancellationToken);
        if (!result.Accepted)
        {
            throw new InvalidOperationException(
                result.Message ?? "The Worker did not publish a valid startup projection.");
        }

        var publishedShell = result.Projection.Deserialize<WorkerSessionShellProjection>(
            EngineJsonSerializerOptions.CreateWire());
        if (publishedShell is null || publishedShell.Revision != result.Revision)
        {
            throw new InvalidOperationException(
                "The Worker did not publish a valid startup projection.");
        }

        if (!_projections.TryPublish(result) &&
            _projections.Shell.Revision < publishedShell.Revision)
        {
            throw new InvalidOperationException(
                "The Worker startup projection could not be reconciled.");
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
                includeSourcePlanIdentity),
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
        CancellationToken cancellationToken = default,
        Guid? operationId = null) =>
        await ReplaceStoredPlanCoreAsync(
            storedPlan,
            trackStoredPlanIdentity,
            cancellationToken,
            operationId);

    public async Task ClearSessionAsync(
        CancellationToken cancellationToken = default) =>
        await ReplaceStoredPlanCoreAsync(
            storedPlan: null,
            trackStoredPlanIdentity: false,
            cancellationToken,
            operationId: null);

    private async Task ReplaceStoredPlanCoreAsync(
        StoredPlan? storedPlan,
        bool trackStoredPlanIdentity,
        CancellationToken cancellationToken,
        Guid? operationId)
    {
        var result = await _engineHost.ReplaceSessionAsync(
            _projections.Shell.Revision,
            storedPlan,
            trackStoredPlanIdentity,
            cancellationToken,
            operationId);
        if (!result.Accepted || !_projections.TryPublish(result))
        {
            await RefreshAfterConflictAsync(result, cancellationToken);
            throw CreateConflict(result);
        }

        var recipe = await _engineHost.GetRecipeProjectionAsync(
            result.Revision,
            cancellationToken);
        _projections.TryPublishRecipe(recipe);
        await RefreshAcquisitionProjectionAsync("All", cancellationToken);
        var market = await _engineHost.GetMarketProjectionAsync(
            result.Revision,
            includeDetails: false,
            cancellationToken: cancellationToken);
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

        var cached = _projections.Recipe;
        if (cached?.Revision == _projections.Shell.Revision)
        {
            return cached;
        }

        var result = await _engineHost.GetRecipeProjectionAsync(
            _projections.Shell.Revision,
            cancellationToken);
        if (!_projections.TryPublishRecipe(result))
        {
            await RefreshAfterConflictAsync(result, cancellationToken);
            result = await _engineHost.GetRecipeProjectionAsync(
                _projections.Shell.Revision,
                cancellationToken);
            _projections.TryPublishRecipe(result);
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

        var cached = _projections.Acquisition;
        if (cached?.Revision == _projections.Shell.Revision &&
            string.Equals(cached.Filter, filter, StringComparison.OrdinalIgnoreCase))
        {
            return cached;
        }

        var result = await _engineHost.GetAcquisitionProjectionAsync(
            _projections.Shell.Revision,
            filter,
            cancellationToken);
        if (!_projections.TryPublishAcquisition(result))
        {
            await RefreshAfterConflictAsync(result, cancellationToken);
            result = await _engineHost.GetAcquisitionProjectionAsync(
                _projections.Shell.Revision,
                filter,
                cancellationToken);
            _projections.TryPublishAcquisition(result);
        }
        return _projections.Acquisition;
    }

    public async Task<WorkerMarketProjection?> GetMarketProjectionAsync(
        CancellationToken cancellationToken = default,
        bool includeDetails = false,
        int? worldDetailItemId = null)
    {
        if (!IsEnabled)
        {
            return null;
        }

        var cached = _projections.Market;
        if (cached?.Revision == _projections.Shell.Revision &&
            (!includeDetails ||
             (worldDetailItemId.HasValue
                 ? cached.ShoppingPlans.Any(plan => plan.ItemId == worldDetailItemId.Value) &&
                   cached.ItemAnalyses.Any(analysis =>
                       analysis.ItemId == worldDetailItemId.Value &&
                       HasCompleteMarketDetail(analysis))
                 : cached.ShoppingPlans.Count > 0)) &&
            (!worldDetailItemId.HasValue ||
             cached.Items.Any(item =>
                 item.ItemId == worldDetailItemId.Value &&
                 (item.WorldCount == 0 || item.Worlds.Count > 0))))
        {
            return cached;
        }

        var result = await _engineHost.GetMarketProjectionAsync(
            _projections.Shell.Revision,
            includeDetails,
            worldDetailItemId,
            cancellationToken: cancellationToken);
        var market = DeserializeMarketProjection(result);
        if (market is null)
        {
            await RefreshAfterConflictAsync(result, cancellationToken);
            result = await _engineHost.GetMarketProjectionAsync(
                _projections.Shell.Revision,
                includeDetails,
                worldDetailItemId,
                cancellationToken: cancellationToken);
            market = DeserializeMarketProjection(result);
        }

        if (market is null)
        {
            throw CreateConflict(result);
        }

        if (includeDetails && worldDetailItemId.HasValue)
        {
            market = await HydrateSelectedMarketDetailsAsync(
                market,
                worldDetailItemId.Value,
                cancellationToken);
        }

        if (!_projections.TryPublishMarket(market))
        {
            throw CreateConflict(result);
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

        var cached = _projections.Procurement;
        if (cached?.Revision == _projections.Shell.Revision)
        {
            return cached;
        }

        var result = await _engineHost.GetProcurementProjectionAsync(
            _projections.Shell.Revision,
            cancellationToken);
        if (!_projections.TryPublishProcurement(result))
        {
            await RefreshAfterConflictAsync(result, cancellationToken);
            result = await _engineHost.GetProcurementProjectionAsync(
                _projections.Shell.Revision,
                cancellationToken);
            _projections.TryPublishProcurement(result);
        }
        return _projections.Procurement;
    }

    public async Task<WorkerTradeProjection?> GetTradeProjectionAsync(
        bool includeCraftLabor = false,
        CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
        {
            return null;
        }

        var result = await _engineHost.GetTradeProjectionAsync(
            _projections.Shell.Revision,
            includeCraftLabor,
            cancellationToken);
        if (string.Equals(
                result.RejectionCode,
                "stale-revision",
                StringComparison.Ordinal))
        {
            await RefreshAfterConflictAsync(result, cancellationToken);
            result = await _engineHost.GetTradeProjectionAsync(
                _projections.Shell.Revision,
                includeCraftLabor,
                cancellationToken);
        }

        if (!result.Accepted)
        {
            await RefreshAfterConflictAsync(result, cancellationToken);
            throw CreateConflict(result);
        }

        return result.Projection.Deserialize<WorkerTradeProjection>(
            EngineJsonSerializerOptions.CreateWire())
            ?? throw new InvalidOperationException(
                "The Worker did not publish a valid Trade projection.");
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

    public async Task<WorkerSessionShellProjection> MutatePlanIdentityAsync(
        string planId,
        string planName,
        CancellationToken cancellationToken = default,
        Guid? operationId = null)
    {
        var result = await _engineHost.MutatePlanIdentityAsync(
            _projections.Shell.Revision,
            new WorkerPlanIdentityMutation(planId, planName),
            cancellationToken,
            operationId);
        if (!_projections.TryPublishMutation<WorkerSessionShellProjection>(
                result,
                out var shell) ||
            shell is null)
        {
            await RefreshAfterConflictAsync(result, cancellationToken);
            throw CreateConflict(result);
        }

        await RefreshRecipeProjectionAsync(cancellationToken);
        return shell;
    }

    public async Task<WorkerSessionShellProjection> MutateActiveContextAsync(
        WorkerActiveContextMutation mutation,
        CancellationToken cancellationToken = default,
        Guid? operationId = null)
    {
        var result = await _engineHost.MutateActiveContextAsync(
            _projections.Shell.Revision,
            mutation,
            cancellationToken,
            operationId);
        if (!_projections.TryPublishMutation<WorkerSessionShellProjection>(
                result,
                out var shell) ||
            shell is null)
        {
            await RefreshAfterConflictAsync(result, cancellationToken);
            throw CreateConflict(result);
        }

        await RefreshRecipeProjectionAsync(cancellationToken);
        await RefreshAcquisitionProjectionAsync("All", cancellationToken);
        return shell;
    }

    public async Task<WorkerRecipeBuildOutcome> BuildRecipeAsync(
        WorkerRecipeBuildRequest request,
        CancellationToken cancellationToken = default,
        Guid? operationId = null)
    {
        var result = await _engineHost.BuildRecipeAsync(
            _projections.Shell.Revision,
            request,
            cancellationToken,
            operationId);
        if (!_projections.TryPublishMutation<WorkerRecipeBuildOutcome>(
                result,
                out var outcome) ||
            outcome is null)
        {
            await RefreshAfterConflictAsync(result, cancellationToken);
            throw CreateConflict(result);
        }

        await RefreshAcquisitionProjectionAsync("All", cancellationToken);
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
        if (string.Equals(result.RejectionCode, "stale-revision", StringComparison.Ordinal))
        {
            await RefreshAfterConflictAsync(result, cancellationToken);
            result = await _engineHost.MutateAcquisitionAsync(
                _projections.Shell.Revision,
                mutation,
                cancellationToken);
        }

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
        CancellationToken cancellationToken = default,
        Action<string, double?>? reportStatus = null,
        Guid? operationId = null)
        => await RunWithOperationAsync(
            WorkerSessionOperationKind.MarketAnalysis,
            $"market:{_projections.Shell.Revision}:{request.Scope}:{request.SelectedRegion}",
            request.ForceRefreshData
                ? "Refreshing market prices..."
                : "Analyzing market prices...",
            operationId,
            cancellationToken,
            activeOperationId => RunMarketAnalysisCoreAsync(
                request,
                activeOperationId,
                cancellationToken,
                reportStatus));

    private async Task<WorkerMarketAnalysisOutcome> RunMarketAnalysisCoreAsync(
        WorkerMarketAnalysisRequest request,
        Guid operationId,
        CancellationToken cancellationToken,
        Action<string, double?>? reportStatus)
    {
        reportStatus?.Invoke(
            request.ForceRefreshData
                ? "Fetching current market listings..."
                : "Checking saved market prices...",
            10);
        var market = _projections.Market ??
            await GetMarketProjectionAsync(cancellationToken) ??
            throw new InvalidOperationException(
                "The Worker did not publish the active market-analysis candidates.");
        if (market.CandidateItems.Count == 0)
        {
            throw new InvalidOperationException(
                "The active plan does not contain any market-analysis candidates.");
        }

        var dataCenters = MarketFetchScopeResolver.GetDataCenters(
            request.Scope,
            request.SelectedDataCenter,
            request.SelectedRegion,
            request.SelectedRegions);
        var expectedWorlds = await GetExpectedWorldsAsync(
            dataCenters,
            cancellationToken);
        var evidenceRequests = market.CandidateItems
            .SelectMany(item => dataCenters.Select(dataCenter => (item.ItemId, dataCenter)))
            .ToList();
        var cacheProgress = reportStatus is null
            ? null
            : new ImmediateProgress<string>(message =>
                ReportMarketCacheProgress(message, reportStatus));
        var fetchedCount = request.ForceRefreshData
            ? await _marketCache.RefreshRequestedAsync(
                evidenceRequests,
                progress: cacheProgress,
                ct: cancellationToken)
            : await _marketCache.EnsurePopulatedAsync(
                evidenceRequests,
                progress: cacheProgress,
                ct: cancellationToken);

        // Raw listings are intentionally read and released one item at a time.
        // A regional Crasher plan spans hundreds of item/data-center pairs; loading
        // all of those payloads into WASM at once can exhaust the browser heap.
        var analyses = new List<MarketItemAnalysis>(market.CandidateItems.Count);
        var shoppingPlans = new List<DetailedShoppingPlan>(market.CandidateItems.Count);
        var unavailableItemIds = new HashSet<int>();
        for (var itemIndex = 0; itemIndex < market.CandidateItems.Count; itemIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = market.CandidateItems[itemIndex];
            reportStatus?.Invoke(
                $"Comparing prices and availability ({itemIndex + 1:N0} of {market.CandidateItems.Count:N0} materials)...",
                50 + (30d * (itemIndex + 1) / market.CandidateItems.Count));
            var reconciliation = await _marketEvidenceReconciliation.ReconcileAsync(
                new MarketEvidenceReconciliationRequest
                {
                    Items = [item],
                    PublishedAnalyses = request.ForceRefreshData
                        ? []
                        : market.ItemAnalyses
                            .Where(candidate => candidate.ItemId == item.ItemId)
                            .ToArray(),
                    PublishedShoppingPlans = request.ForceRefreshData
                        ? []
                        : market.ShoppingPlans
                            .Where(candidate => candidate.ItemId == item.ItemId)
                            .ToArray(),
                    Scope = request.Scope,
                    SelectedDataCenter = request.SelectedDataCenter,
                    SelectedRegion = request.SelectedRegion,
                    RequestedDataCenters = dataCenters,
                    Lens = request.Lens,
                    CacheAlreadyPopulated = true,
                    ExpectedWorldsByDataCenter = expectedWorlds
                },
                ct: cancellationToken,
                executionOptions: MarketAnalysisExecutionOptions.Interactive);
            foreach (var analysis in reconciliation.Analyses)
            {
                CompactMarketAnalysisForPublication(analysis);
            }
            analyses.AddRange(reconciliation.Analyses);
            shoppingPlans.AddRange(reconciliation.ShoppingPlans);
            unavailableItemIds.UnionWith(reconciliation.UnavailableItemIds);
        }

        if (shoppingPlans.Count == 0)
        {
            throw new InvalidOperationException(
                $"The market source returned no usable evidence for {market.CandidateItems.Count:N0} items.");
        }

        var publicationOperationId = Guid.NewGuid();
        var publicationBaseRevision = market.Revision;
        WorkerSessionResultEnvelope? result = null;
        var publicationItemIds = analyses
            .Select(analysis => analysis.ItemId)
            .Concat(shoppingPlans.Select(plan => plan.ItemId))
            .Distinct()
            .ToArray();
        const int publicationBatchSize = 4;
        for (var offset = 0; offset < publicationItemIds.Length; offset += publicationBatchSize)
        {
            var itemIds = publicationItemIds
                .Skip(offset)
                .Take(publicationBatchSize)
                .ToHashSet();
            var isFirst = offset == 0;
            var isFinal = offset + publicationBatchSize >= publicationItemIds.Length;
            var publishedCount = Math.Min(
                offset + publicationBatchSize,
                publicationItemIds.Length);
            reportStatus?.Invoke(
                $"Applying the best purchase options ({publishedCount:N0} of {publicationItemIds.Length:N0} materials)...",
                80 + (15d * publishedCount / publicationItemIds.Length));
            var publicationRequest = new WorkerMarketEvidencePublicationRequest(
                    publicationOperationId,
                    publicationBaseRevision,
                    request.Scope,
                    request.SelectedDataCenter,
                    request.SelectedRegion,
                    request.Lens,
                    analyses.Where(analysis => itemIds.Contains(analysis.ItemId)).ToArray(),
                    shoppingPlans.Where(plan => itemIds.Contains(plan.ItemId)).ToArray(),
                    isFinal ? unavailableItemIds : new HashSet<int>(),
                    isFinal ? fetchedCount : 0,
                    ResetStaging: isFirst,
                    CompleteStaging: isFinal,
                    RequestedDataCenters: dataCenters);
            result = isFinal
                ? await _engineHost.PublishMarketEvidenceAsync(
                    publicationBaseRevision,
                    publicationRequest,
                    cancellationToken,
                    operationId)
                : await _engineHost.StageMarketEvidenceAsync(
                    publicationBaseRevision,
                    publicationRequest,
                    cancellationToken,
                    operationId);
            if (!result.Accepted)
            {
                await RefreshAfterConflictAsync(result, cancellationToken);
                throw CreateConflict(result);
            }
        }

        if (result is null)
        {
            throw new InvalidOperationException(
                "The market analysis produced no evidence publication batches.");
        }
        if (!_projections.TryPublishMutation<WorkerMarketEvidenceCommitProjection>(
                result,
                out var commit) ||
            commit is null)
        {
            await RefreshAfterConflictAsync(result, cancellationToken);
            throw CreateConflict(result);
        }

        var marketResult = await _engineHost.GetMarketProjectionAsync(
            _projections.Shell.Revision,
            includeDetails: false,
            cancellationToken: cancellationToken);
        if (!_projections.TryPublishMarket(marketResult) ||
            _projections.Market is null)
        {
            await RefreshAfterConflictAsync(marketResult, cancellationToken);
            throw CreateConflict(marketResult);
        }

        reportStatus?.Invoke("Updating your plan with the new prices...", 98);
        await RefreshRecipeProjectionAsync(cancellationToken);
        await RefreshAcquisitionProjectionAsync("All", cancellationToken);
        return new WorkerMarketAnalysisOutcome(
            Published: true,
            commit.AnalyzedCount,
            commit.ChangedDecisionCount,
            commit.FetchedCount,
            _projections.Market);
    }

    public static void CompactMarketAnalysisForPublication(MarketItemAnalysis analysis)
    {
        foreach (var world in analysis.Worlds)
        {
            if (world.Listings.Count == 0)
            {
                continue;
            }

            var retainedSortIndexes = SelectCoverageListings(
                    world.Listings,
                    analysis.QuantityNeeded,
                    static _ => true)
                .Concat(SelectCoverageListings(
                    world.Listings,
                    analysis.QuantityNeeded,
                    static listing => listing.IsHq))
                .Select(listing => listing.SortIndex)
                .ToHashSet();
            world.Listings.RemoveAll(listing =>
                !retainedSortIndexes.Contains(listing.SortIndex));
        }
    }

    private static IEnumerable<AnalyzedMarketListing> SelectCoverageListings(
        IEnumerable<AnalyzedMarketListing> listings,
        int quantityNeeded,
        Func<AnalyzedMarketListing, bool> predicate)
    {
        var remaining = Math.Max(0, quantityNeeded);
        foreach (var listing in listings
                     .Where(predicate)
                     .Where(listing => listing.Quantity > 0 && listing.PricePerUnit > 0)
                     .OrderBy(listing => listing.SortIndex))
        {
            yield return listing;
            remaining -= listing.Quantity;
            if (remaining <= 0)
            {
                yield break;
            }
        }
    }

    public async Task<WorkerMarketProjection> ApplyMarketLensAsync(
        MarketAcquisitionLens lens,
        CancellationToken cancellationToken = default,
        Guid? operationId = null)
        => await RunWithOperationAsync(
            WorkerSessionOperationKind.MarketAnalysis,
            $"market-lens:{_projections.Shell.Revision}:{lens}",
            "Updating market recommendations...",
            operationId,
            cancellationToken,
            activeOperationId => ApplyMarketLensCoreAsync(
                lens,
                activeOperationId,
                cancellationToken));

    private async Task<WorkerMarketProjection> ApplyMarketLensCoreAsync(
        MarketAcquisitionLens lens,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        var result = await _engineHost.ApplyMarketLensAsync(
            _projections.Shell.Revision,
            new WorkerMarketLensMutation(lens),
            cancellationToken,
            operationId);
        if (!_projections.TryPublishMutation<WorkerMarketProjection>(
                result,
                out var market) ||
            market is null)
        {
            await RefreshAfterConflictAsync(result, cancellationToken);
            throw CreateConflict(result);
        }

        await RefreshRecipeProjectionAsync(cancellationToken);
        await RefreshAcquisitionProjectionAsync("All", cancellationToken);
        return market;
    }

    public async Task<WorkerMarketItemRefreshOutcome> RefreshMarketItemAsync(
        WorkerMarketItemRefreshRequest request,
        CancellationToken cancellationToken = default,
        Guid? operationId = null)
        => await RunWithOperationAsync(
            WorkerSessionOperationKind.ItemMarketRefresh,
            $"market-item:{_projections.Shell.Revision}:{request.ItemId}",
            $"Refreshing {request.ItemName}...",
            operationId,
            cancellationToken,
            activeOperationId => RefreshMarketItemCoreAsync(
                request,
                activeOperationId,
                cancellationToken));

    private async Task<WorkerMarketItemRefreshOutcome> RefreshMarketItemCoreAsync(
        WorkerMarketItemRefreshRequest request,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        var market = _projections.Market ??
            await GetMarketProjectionAsync(cancellationToken) ??
            throw new InvalidOperationException(
                "The Worker did not publish the active market-analysis candidates.");
        var item = market.CandidateItems.FirstOrDefault(candidate =>
            candidate.ItemId == request.ItemId)
            ?? throw new InvalidOperationException(
                $"{request.ItemName} is no longer part of the active market analysis.");
        var expectedWorlds = await GetExpectedWorldsAsync(
            request.RequestedDataCenters is { Count: > 0 }
                ? request.RequestedDataCenters
                : MarketFetchScopeResolver.GetDataCenters(
                    request.Scope,
                    request.SelectedDataCenter,
                    request.SelectedRegion),
            cancellationToken);

        MarketItemAnalysis analysis;
        DetailedShoppingPlan shoppingPlan;
        if (!string.IsNullOrWhiteSpace(request.TargetDataCenter) &&
            !string.IsNullOrWhiteSpace(request.TargetWorldName))
        {
            var worldResult = await _marketEvidenceReconciliation.ReconcileWorldAsync(
                new MarketWorldEvidenceReconciliationRequest
                {
                    Item = item,
                    DataCenter = request.TargetDataCenter,
                    WorldName = request.TargetWorldName,
                    ObservedEvidence = request.ObservedEvidence,
                    Scope = request.Scope,
                    SelectedDataCenter = request.SelectedDataCenter,
                    SelectedRegion = request.SelectedRegion,
                    RequestedDataCenters = request.RequestedDataCenters ??
                        Array.Empty<string>(),
                    Lens = request.Lens,
                    ExpectedWorldsByDataCenter = expectedWorlds
                },
                ct: cancellationToken,
                executionOptions: MarketAnalysisExecutionOptions.Interactive);
            analysis = worldResult.Analysis;
            shoppingPlan = worldResult.ShoppingPlan;
        }
        else
        {
            var reconciliation = await _marketEvidenceReconciliation.ReconcileAsync(
                new MarketEvidenceReconciliationRequest
                {
                    Items = [item],
                    PublishedAnalyses = market.ItemAnalyses
                        .Where(candidate => candidate.ItemId == request.ItemId)
                        .ToArray(),
                    PublishedShoppingPlans = market.ShoppingPlans
                        .Where(candidate => candidate.ItemId == request.ItemId)
                        .ToArray(),
                    Scope = request.Scope,
                    SelectedDataCenter = request.SelectedDataCenter,
                    SelectedRegion = request.SelectedRegion,
                    RequestedDataCenters = request.RequestedDataCenters ??
                        Array.Empty<string>(),
                    Lens = request.Lens,
                    ExpectedWorldsByDataCenter = expectedWorlds,
                    Policy = MarketEvidenceReconciliationPolicy.ForcedRefresh()
                },
                ct: cancellationToken,
                executionOptions: MarketAnalysisExecutionOptions.Interactive);
            analysis = reconciliation.Analyses.SingleOrDefault()
                ?? throw new InvalidOperationException(
                    $"The market source returned no usable evidence for {request.ItemName}.");
            shoppingPlan = reconciliation.ShoppingPlans.SingleOrDefault()
                ?? throw new InvalidOperationException(
                    $"The market source returned no purchase plan for {request.ItemName}.");
        }

        var result = await _engineHost.PublishMarketItemEvidenceAsync(
            market.Revision,
            new WorkerMarketItemEvidencePublicationRequest(
                request.ItemId,
                request.Scope,
                request.SelectedDataCenter,
                request.SelectedRegion,
                request.Lens,
                CloneAndCompactMarketAnalysisForPublication(analysis),
                shoppingPlan,
                request.RequestedDataCenters),
            cancellationToken,
            operationId);
        if (!_projections.TryPublishMutation<WorkerMarketItemRefreshOutcome>(
                result,
                out var outcome) ||
            outcome is null)
        {
            await RefreshAfterConflictAsync(result, cancellationToken);
            throw CreateConflict(result);
        }

        var marketWithDetails = _projections.AttachMarketDetails(
            [shoppingPlan],
            [analysis]);
        await RefreshRecipeProjectionAsync(cancellationToken);
        await RefreshAcquisitionProjectionAsync("All", cancellationToken);
        return outcome with { Market = marketWithDetails };
    }

    private async Task<WorkerMarketProjection> HydrateSelectedMarketDetailsAsync(
        WorkerMarketProjection market,
        int itemId,
        CancellationToken cancellationToken)
    {
        var selectedPlan = market.ShoppingPlans
            .FirstOrDefault(plan => plan.ItemId == itemId);
        var selectedAnalysis = market.ItemAnalyses
            .FirstOrDefault(analysis => analysis.ItemId == itemId);
        if (selectedPlan is null)
        {
            return market with
            {
                ShoppingPlans = Array.Empty<DetailedShoppingPlan>(),
                ItemAnalyses = Array.Empty<MarketItemAnalysis>()
            };
        }

        if (selectedAnalysis is not null && HasCompleteMarketDetail(selectedAnalysis))
        {
            return market with
            {
                ShoppingPlans = [selectedPlan],
                ItemAnalyses = [selectedAnalysis]
            };
        }

        var item = market.CandidateItems.FirstOrDefault(candidate => candidate.ItemId == itemId);
        if (item is null)
        {
            return market with
            {
                ShoppingPlans = [selectedPlan],
                ItemAnalyses = Array.Empty<MarketItemAnalysis>()
            };
        }

        var expectedWorlds = await GetExpectedWorldsAsync(
            market.RequestedDataCenters is { Count: > 0 }
                ? market.RequestedDataCenters
                : MarketFetchScopeResolver.GetDataCenters(
                    market.Scope,
                    market.SelectedDataCenter,
                    market.SelectedRegion),
            cancellationToken);
        var reconciliation = await _marketEvidenceReconciliation.ReconcileAsync(
            new MarketEvidenceReconciliationRequest
            {
                Items = [item],
                PublishedAnalyses = [],
                PublishedShoppingPlans = [],
                Scope = market.Scope,
                SelectedDataCenter = market.SelectedDataCenter,
                SelectedRegion = market.SelectedRegion,
                RequestedDataCenters = market.RequestedDataCenters ??
                    Array.Empty<string>(),
                Lens = market.Lens,
                CacheAlreadyPopulated = true,
                // This is display hydration for already-published evidence, not a
                // pricing decision. Read the retained raw snapshot even after it
                // ages out of recommendation reuse so its bands and rows continue
                // to describe the canonical market summary without refetching.
                Policy = new MarketEvidenceReconciliationPolicy
                {
                    ReusableCacheMaxAge = HistoricalDetailCacheMaxAge
                },
                ExpectedWorldsByDataCenter = expectedWorlds
            },
            ct: cancellationToken,
            executionOptions: MarketAnalysisExecutionOptions.Interactive);
        var hydratedAnalysis = reconciliation.Analyses
            .FirstOrDefault(analysis => analysis.ItemId == itemId);
        return market with
        {
            ShoppingPlans = [selectedPlan],
            ItemAnalyses = hydratedAnalysis is not null &&
                           HasCompleteMarketDetail(hydratedAnalysis)
                ? [hydratedAnalysis]
                : Array.Empty<MarketItemAnalysis>()
        };
    }

    private static WorkerMarketProjection? DeserializeMarketProjection(
        WorkerSessionResultEnvelope result)
    {
        if (!result.Accepted ||
            result.Projection.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        var market = result.Projection.Deserialize<WorkerMarketProjection>(
            EngineJsonSerializerOptions.CreateWire());
        return market?.Revision == result.Revision ? market : null;
    }

    public static bool HasCompleteMarketDetail(MarketItemAnalysis analysis)
    {
        if (analysis.RequestedDataCenters.Count > 0 &&
            analysis.PresentDataCenters.Count == 0)
        {
            return false;
        }

        var summarizedListings = analysis.ScopePriceBands.Sum(band => band.ListingCount);
        var retainedListings = analysis.Worlds.Sum(world =>
            world.Listings.Count(listing =>
                listing.Quantity > 0 &&
                listing.PricePerUnit > 0));
        return summarizedListings == retainedListings;
    }

    public static MarketItemAnalysis CloneAndCompactMarketAnalysisForPublication(
        MarketItemAnalysis analysis)
    {
        var clone = JsonSerializer.SerializeToElement(
                analysis,
                EngineJsonSerializerOptions.CreateWire())
            .Deserialize<MarketItemAnalysis>(EngineJsonSerializerOptions.CreateWire())
            ?? throw new InvalidOperationException(
                $"Market analysis for item {analysis.ItemId} could not be copied for publication.");
        CompactMarketAnalysisForPublication(clone);
        return clone;
    }

    private async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>>
        GetExpectedWorldsAsync(
            IReadOnlyList<string> dataCenters,
            CancellationToken cancellationToken)
    {
        var worldData = await _universalis.GetWorldDataAsync(cancellationToken);
        return dataCenters
            .Where(dataCenter => worldData.DataCenterToWorlds.ContainsKey(dataCenter))
            .ToDictionary(
                dataCenter => dataCenter,
                dataCenter =>
                    (IReadOnlyList<string>)worldData.DataCenterToWorlds[dataCenter],
                StringComparer.OrdinalIgnoreCase);
    }

    public async Task<WorkerProcurementOutcome> RunProcurementAsync(
        WorkerProcurementRequest request,
        CancellationToken cancellationToken = default,
        Action<string, double?>? reportStatus = null,
        Guid? operationId = null)
        => await RunWithOperationAsync(
            WorkerSessionOperationKind.ProcurementAnalysis,
            $"procurement:{_projections.Shell.Revision}",
            "Building your procurement route...",
            operationId,
            cancellationToken,
            activeOperationId => RunProcurementCoreAsync(
                request,
                activeOperationId,
                cancellationToken,
                reportStatus));

    private async Task<WorkerProcurementOutcome> RunProcurementCoreAsync(
        WorkerProcurementRequest request,
        Guid operationId,
        CancellationToken cancellationToken,
        Action<string, double?>? reportStatus)
    {
        reportStatus?.Invoke(
            "Comparing travel and price tradeoffs...",
            70);
        var result = await _engineHost.RunProcurementAsync(
            _projections.Shell.Revision,
            request,
            cancellationToken,
            operationId);
        reportStatus?.Invoke("Preparing your shopping route...", 95);
        if (!_projections.TryPublishMutation<WorkerProcurementOutcome>(
                result,
                out var outcome) ||
            outcome is null)
        {
            await RefreshAfterConflictAsync(result, cancellationToken);
            throw CreateConflict(result);
        }

        await RefreshRecipeProjectionAsync(cancellationToken);
        await RefreshAcquisitionProjectionAsync("All", cancellationToken);
        return outcome;
    }

    private static void ReportMarketCacheProgress(
        string message,
        Action<string, double?> reportStatus)
    {
        var fetchProgress = Regex.Match(
            message,
            @"^Fetching (?<current>\d+)/(?<total>\d+) market entries");
        if (fetchProgress.Success &&
            int.TryParse(fetchProgress.Groups["current"].Value, out var current) &&
            int.TryParse(fetchProgress.Groups["total"].Value, out var total) &&
            total > 0)
        {
            reportStatus(
                $"Fetching current market listings ({current:N0} of {total:N0})...",
                20 + (30d * current / total));
            return;
        }

        var dataCenterFetch = Regex.Match(
            message,
            @"^Loading (?<count>\d+) market items from (?<dataCenter>.+)\.\.\.$");
        if (dataCenterFetch.Success)
        {
            reportStatus(
                $"Fetching current listings from {dataCenterFetch.Groups["dataCenter"].Value}...",
                25);
            return;
        }

        if (message.StartsWith("Fetching market data", StringComparison.Ordinal))
        {
            reportStatus("Fetching current market listings...", 20);
            return;
        }

        if (message.StartsWith("Local market cache is ready", StringComparison.Ordinal))
        {
            reportStatus("Using saved market prices...", 40);
            return;
        }

        if (message.StartsWith("Removing stale", StringComparison.Ordinal) ||
            message.StartsWith("Reducing local", StringComparison.Ordinal))
        {
            reportStatus("Tidying saved market prices...", 15);
            return;
        }

        reportStatus("Checking saved market prices...", 10);
    }

    private sealed class ImmediateProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    public async Task<WorkerProcurementProjection> SelectProcurementToleranceAsync(
        int travelTolerance,
        CancellationToken cancellationToken = default)
        => await RunWithOperationAsync(
            WorkerSessionOperationKind.ProcurementAnalysis,
            $"procurement-tolerance:{_projections.Shell.Revision}:{travelTolerance}",
            "Updating the procurement route...",
            operationId: null,
            cancellationToken,
            operationId => SelectProcurementToleranceCoreAsync(
                travelTolerance,
                operationId,
                cancellationToken));

    private async Task<WorkerProcurementProjection> SelectProcurementToleranceCoreAsync(
        int travelTolerance,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        var result = await _engineHost.SelectProcurementToleranceAsync(
            _projections.Shell.Revision,
            travelTolerance,
            cancellationToken,
            operationId);
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
    }

    private async Task RefreshRecipeProjectionAsync(
        CancellationToken cancellationToken)
    {
        var recipe = await _engineHost.GetRecipeProjectionAsync(
            _projections.Shell.Revision,
            cancellationToken);
        if (!_projections.TryPublishRecipe(recipe))
        {
            await RefreshAfterConflictAsync(recipe, cancellationToken);
            throw CreateConflict(recipe);
        }
    }

    private async Task RefreshAcquisitionProjectionAsync(
        string filter,
        CancellationToken cancellationToken)
    {
        var acquisition = await _engineHost.GetAcquisitionProjectionAsync(
            _projections.Shell.Revision,
            filter,
            cancellationToken);
        if (!_projections.TryPublishAcquisition(acquisition))
        {
            await RefreshAfterConflictAsync(acquisition, cancellationToken);
            throw CreateConflict(acquisition);
        }
    }

    private static InvalidOperationException CreateConflict(
        WorkerSessionResultEnvelope result) =>
        new(
            result.Message ??
            "The plan changed before this edit was accepted. The current Worker projection has been restored.");

    private void OnCrossTabSessionProjectionReceived(
        object? sender,
        WorkerSessionShellProjection shell)
    {
        if (_disposed || !_projections.TryPublishCrossTabShell(shell))
        {
            return;
        }

        _ = ReconcileCrossTabProjectionsAsync(shell.Revision);
    }

    private async Task ReconcileCrossTabProjectionsAsync(long revision)
    {
        await _crossTabProjectionGate.WaitAsync();
        try
        {
            if (_disposed || _projections.Shell.Revision != revision)
            {
                return;
            }

            var recipe = await _engineHost.GetRecipeProjectionAsync(revision);
            if (!_projections.TryPublishRecipe(recipe))
            {
                return;
            }
            var acquisition = await _engineHost.GetAcquisitionProjectionAsync(
                revision,
                "All");
            if (!_projections.TryPublishAcquisition(acquisition))
            {
                return;
            }
            var market = await _engineHost.GetMarketProjectionAsync(
                revision,
                includeDetails: false);
            if (!_projections.TryPublishMarket(market))
            {
                return;
            }
            var procurement = await _engineHost.GetProcurementProjectionAsync(revision);
            _projections.TryPublishProcurement(procurement);
        }
        catch (Exception ex) when (!_disposed)
        {
            Console.Error.WriteLine(
                $"Cross-tab Worker projection reconciliation failed: {ex.Message}");
        }
        finally
        {
            _crossTabProjectionGate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }
        _disposed = true;
        _engineHost.CrossTabSessionProjectionReceived -= OnCrossTabSessionProjectionReceived;
        return ValueTask.CompletedTask;
    }
}
