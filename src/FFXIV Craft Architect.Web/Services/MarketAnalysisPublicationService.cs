using System.Text;
using System.Text.Json;

using FFXIV_Craft_Architect.Core.Engine;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Web.Services;

public sealed record MarketAnalysisPublicationRequest(
    CraftingPlan Plan,
    long PlanSessionVersion,
    long PlanDecisionVersion,
    string? PlanId,
    string? PlanName,
    IReadOnlyList<MarketItemAnalysis> Analyses,
    List<DetailedShoppingPlan> ShoppingPlans,
    StoredRecipeOperationSnapshot? RecipeBasis,
    PublishedMarketAnalysisScopeSnapshot PublishedScope,
    int PreparedDecisionChangeCount = 0);

public sealed record MarketAnalysisPersistenceSnapshot(
    string Domain,
    long SettingsVersion,
    long PlanSessionVersion,
    long PlanDecisionVersion,
    string? PlanId,
    string? PlanName,
    string DataCenter,
    DateTime CapturedAtUtc,
    IReadOnlyList<StoredProjectItem> ProjectItems,
    CraftingPlan Plan,
    IReadOnlyList<MarketItemAnalysis> Analyses,
    IReadOnlyList<DetailedShoppingPlan> ShoppingPlans,
    StoredRecipeOperationSnapshot? RecipeBasis,
    PublishedMarketAnalysisScopeSnapshot PublishedScope,
    RecommendationMode RecommendationMode,
    MarketAcquisitionLens MarketAnalysisLens,
    MarketIntelligence MarketIntelligence,
    int PreparedDecisionChangeCount)
{
    public MarketAnalysisPublicationRequest ToPublicationRequest(CraftingPlan livePlan) =>
        new(
            livePlan,
            PlanSessionVersion,
            PlanDecisionVersion,
            PlanId,
            PlanName,
            Analyses,
            ShoppingPlans.ToList(),
            RecipeBasis,
            PublishedScope,
            PreparedDecisionChangeCount);

    public StoredPlan ToAutoSavePlan() =>
        ToAutoSavePlan(JsonSerializer.Serialize(
            StoredMarketIntelligence.FromMarketIntelligence(MarketIntelligence)));

    public async Task<StoredPlan> ToAutoSavePlanAsync(CancellationToken cancellationToken)
    {
        var storedIntelligence = StoredMarketIntelligence.FromMarketIntelligence(MarketIntelligence);
        await using var stream = new YieldingMemoryStream();
        await JsonSerializer.SerializeAsync(
            stream,
            storedIntelligence,
            new JsonSerializerOptions { DefaultBufferSize = 1024 * 1024 },
            cancellationToken);
        var marketIntelligenceJson = Encoding.UTF8.GetString(
            stream.GetBuffer(),
            0,
            checked((int)stream.Length));
        return ToAutoSavePlan(marketIntelligenceJson);
    }

    private StoredPlan ToAutoSavePlan(string marketIntelligenceJson) =>
        new()
        {
            Id = "autosave",
            Name = "AutoSave",
            CreatedAt = CapturedAtUtc,
            ModifiedAt = CapturedAtUtc,
            SavedAt = CapturedAtUtc,
            DataCenter = DataCenter,
            ProjectItems = ProjectItems.Select(item => new StoredProjectItem
            {
                Id = item.Id,
                Name = item.Name,
                IconId = item.IconId,
                Quantity = item.Quantity,
                MustBeHq = item.MustBeHq
            }).ToList(),
            PlanJson = JsonSerializer.Serialize(Plan),
            MarketIntelligenceJson = marketIntelligenceJson,
            MarketPlansJson = null,
            MarketItemAnalysesJson = null,
            MarketAnalysisRecipeBasisJson = RecipeBasis is null
                ? null
                : JsonSerializer.Serialize(RecipeBasis),
            MarketAnalysisScopeSnapshotJson = JsonSerializer.Serialize(PublishedScope),
            SavedRecommendationMode = RecommendationMode,
            SavedMarketAnalysisLens = MarketAnalysisLens,
            SourcePlanId = PlanId,
            SourcePlanName = PlanName
        };

    private sealed class YieldingMemoryStream : MemoryStream
    {
        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            Write(buffer.Span);
            await Task.Delay(1, cancellationToken);
        }

        public override async Task WriteAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            Write(buffer, offset, count);
            await Task.Delay(1, cancellationToken);
        }
    }
}

public sealed record MarketAnalysisPublication(
    MarketAnalysisPublicationRequest Request,
    int ChangedDecisionCount,
    long PublishedDecisionVersion,
    MarketAnalysisPersistenceSnapshot PersistenceSnapshot);

internal sealed record PreparedMarketAnalysisPublication(
    MarketAnalysisPublicationRequest Request,
    int ChangedDecisionCount,
    MarketAnalysisPersistenceSnapshot PersistenceSnapshot);

public interface IMarketAnalysisPublicationStore
{
    Task<bool> SaveNamedPlanAsync(MarketAnalysisPersistenceSnapshot snapshot);

    Task<bool> AutoSaveAsync(MarketAnalysisPersistenceSnapshot snapshot);
}

public sealed class MarketAnalysisPublicationStore : IMarketAnalysisPublicationStore
{
    private readonly AppState _appState;
    private readonly IMarketAnalysisPersistence _marketAnalysisPersistence;
    private readonly IndexedDbService _indexedDb;

    public MarketAnalysisPublicationStore(
        AppState appState,
        IMarketAnalysisPersistence marketAnalysisPersistence,
        IndexedDbService indexedDb)
    {
        _appState = appState;
        _marketAnalysisPersistence = marketAnalysisPersistence;
        _indexedDb = indexedDb;
    }

    public Task<bool> SaveNamedPlanAsync(MarketAnalysisPersistenceSnapshot snapshot)
    {
        return _marketAnalysisPersistence.SaveAsync(
            snapshot.PlanId!,
            snapshot.ShoppingPlans,
            snapshot.Analyses,
            snapshot.RecommendationMode,
            snapshot.MarketAnalysisLens,
            snapshot.RecipeBasis,
            snapshot.PublishedScope,
            snapshot.MarketIntelligence);
    }

    public async Task<bool> AutoSaveAsync(MarketAnalysisPersistenceSnapshot snapshot)
    {
        var lease = await _appState.BeginAutoSaveAsync();
        if (lease is null)
        {
            return false;
        }

        var saved = false;
        try
        {
            saved = await _indexedDb.SavePlanAsync(
                await snapshot.ToAutoSavePlanAsync(CancellationToken.None));
            return saved;
        }
        finally
        {
            _appState.CompleteAutoSave(saved, lease.CapturedVersions, lease.DirtyBuckets);
        }
    }
}

public sealed class MarketAnalysisPublicationService
{
    private readonly AppState _appState;
    private readonly MarketShoppingService _marketShoppingService;
    private readonly IMarketAnalysisPublicationStore _store;
    private readonly IRecipeLayerWorkflowService _recipeLayerWorkflow;
    private readonly ILogger _logger;

    public MarketAnalysisPublicationService(
        AppState appState,
        MarketShoppingService marketShoppingService,
        IMarketAnalysisPublicationStore store,
        IRecipeLayerWorkflowService recipeLayerWorkflow,
        ILogger logger)
    {
        _appState = appState;
        _marketShoppingService = marketShoppingService;
        _store = store;
        _recipeLayerWorkflow = recipeLayerWorkflow;
        _logger = logger;
    }

    public MarketAnalysisPublication? Publish(
        MarketAnalysisPublicationRequest request,
        MarketAnalysisPersistenceSnapshot persistenceSnapshot,
        CancellationToken cancellationToken)
    {
        var prepared = Prepare(request, persistenceSnapshot, cancellationToken);
        return prepared is null ? null : PublishPrepared(prepared, cancellationToken);
    }

    public MarketAnalysisPublicationRequest? PrepareForRegistration(
        MarketAnalysisPublicationRequest request,
        CancellationToken cancellationToken) =>
        PrepareForRegistration(request, cancellationToken, snapshot: true);

    private MarketAnalysisPublicationRequest? PrepareForRegistration(
        MarketAnalysisPublicationRequest request,
        CancellationToken cancellationToken,
        bool snapshot)
    {
        if (!IsCurrent(request))
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var changedDecisions = AcquisitionPlanningService.EnsureAutomaticMarketSourcesAreActionable(
            request.Plan,
            request.ShoppingPlans);
        _marketShoppingService.ApplyVendorPurchaseOverrides(request.Plan, request.ShoppingPlans);
        if (changedDecisions > 0)
        {
            _appState.NotifyPlanDecisionChanged();
        }

        var prepared = request with
        {
            PlanDecisionVersion = _appState.CurrentVersions.PlanDecisionVersion,
            PreparedDecisionChangeCount = changedDecisions
        };
        return IsCurrent(prepared)
            ? snapshot ? SnapshotRequest(prepared) : prepared
            : null;
    }

    public MarketAnalysisPersistenceSnapshot CapturePersistenceSnapshot(
        MarketAnalysisPublicationRequest request,
        DateTime? capturedAtUtc = null,
        Guid? marketIntelligenceId = null) =>
        CapturePersistenceSnapshot(
            request,
            capturedAtUtc,
            marketIntelligenceId,
            requestAlreadySnapshotted: false);

    private MarketAnalysisPersistenceSnapshot CapturePersistenceSnapshot(
        MarketAnalysisPublicationRequest request,
        DateTime? capturedAtUtc,
        Guid? marketIntelligenceId,
        bool requestAlreadySnapshotted)
    {
        if (!IsCurrent(request))
        {
            throw new InvalidOperationException("The market-analysis persistence snapshot is stale.");
        }

        var options = EngineJsonSerializerOptions.CreateWire();
        if (!requestAlreadySnapshotted)
        {
            var repairProbe = Clone(request.Plan, options);
            if (AcquisitionPlanningService.EnsureAutomaticMarketSourcesAreActionable(
                    repairProbe,
                    request.ShoppingPlans) > 0)
            {
                throw new InvalidOperationException(
                    "Automatic source repair must be completed before the engine request and settlement snapshot.");
            }
            var vendorProbe = Clone(request.ShoppingPlans, options);
            _marketShoppingService.ApplyVendorPurchaseOverrides(request.Plan, vendorProbe);
            if (!string.Equals(
                    EngineCanonicalHash.Compute(vendorProbe, options),
                    EngineCanonicalHash.Compute(request.ShoppingPlans, options),
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Vendor repair must be completed before the engine request and settlement snapshot.");
            }
        }

        var snapshotRequest = requestAlreadySnapshotted ? request : SnapshotRequest(request);
        var versions = _appState.CurrentVersions;
        var intelligence = new MarketIntelligence(
            marketIntelligenceId ?? Guid.NewGuid(),
            snapshotRequest.Analyses,
            snapshotRequest.ShoppingPlans,
            Clone(_appState.UnavailableMarketItems, options),
            new MarketIntelligencePublicationContext(
                MarketIntelligencePublicationContextKind.Known,
                snapshotRequest.PublishedScope.Scope,
                snapshotRequest.PublishedScope.SelectedDataCenter,
                snapshotRequest.PublishedScope.SelectedRegion,
                snapshotRequest.PublishedScope.RequestedDataCenters.ToArray(),
                new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase),
                null,
                false,
                _appState.RecommendationMode,
                _appState.MarketAnalysisLens,
                null,
                snapshotRequest.PlanSessionVersion,
                versions.MarketAnalysisVersion + 1,
                snapshotRequest.PublishedScope.PublishedAtUtc),
            snapshotRequest.RecipeBasis);
        var snapshot = new MarketAnalysisPersistenceSnapshot(
            "web-market-analysis-persistence-v1",
            versions.SettingsVersion,
            snapshotRequest.PlanSessionVersion,
            snapshotRequest.PlanDecisionVersion,
            snapshotRequest.PlanId,
            snapshotRequest.PlanName,
            _appState.SelectedDataCenter,
            capturedAtUtc ?? DateTime.UtcNow,
            _appState.ProjectItems.Select(item => new StoredProjectItem
            {
                Id = item.Id,
                Name = item.Name,
                IconId = item.IconId,
                Quantity = item.Quantity,
                MustBeHq = item.MustBeHq
            }).ToArray(),
            Clone(snapshotRequest.Plan, options),
            snapshotRequest.Analyses,
            snapshotRequest.ShoppingPlans,
            snapshotRequest.RecipeBasis,
            snapshotRequest.PublishedScope,
            _appState.RecommendationMode,
            _appState.MarketAnalysisLens,
            intelligence,
            snapshotRequest.PreparedDecisionChangeCount);
        return requestAlreadySnapshotted ? snapshot : SnapshotPersistence(snapshot);
    }

    internal PreparedMarketAnalysisPublication? Prepare(
        MarketAnalysisPublicationRequest request,
        MarketAnalysisPersistenceSnapshot persistenceSnapshot,
        CancellationToken cancellationToken)
    {
        if (!IsCurrent(request))
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = SnapshotRequest(request);
        var persistence = SnapshotPersistence(persistenceSnapshot);

        if (!IsCurrent(request) ||
            !string.Equals(
                ComputePublicationRequestHash(snapshot),
                ComputePublicationRequestHash(persistence.ToPublicationRequest(snapshot.Plan)),
                StringComparison.Ordinal))
        {
            return null;
        }

        return new PreparedMarketAnalysisPublication(
            snapshot,
            snapshot.PreparedDecisionChangeCount,
            persistence);
    }

    internal MarketAnalysisPublication PublishPrepared(
        PreparedMarketAnalysisPublication prepared,
        CancellationToken cancellationToken)
    {
        var request = prepared.Request;
        if (!IsCurrent(request))
        {
            throw new InvalidOperationException("The prepared market-analysis publication context became stale.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        _appState.ApplyMarketAnalysisPublication(
            request.Analyses,
            request.ShoppingPlans,
            _recipeLayerWorkflow.BuildActiveProcurementItems(request.Plan),
            acquisitionDecisionsChanged: false,
            request.RecipeBasis,
            request.PublishedScope,
            prepared.PersistenceSnapshot.MarketIntelligence.MarketIntelligenceId);
        _logger.LogInformation(
            "[stage] hot-state publication applied ({Count} analyses, {PlanCount} plans)",
            request.Analyses.Count,
            request.ShoppingPlans.Count);
        return new MarketAnalysisPublication(
            request,
            prepared.ChangedDecisionCount,
            _appState.CurrentVersions.PlanDecisionVersion,
            prepared.PersistenceSnapshot);
    }

    internal static MarketAnalysisPublicationRequest SnapshotRequest(
        MarketAnalysisPublicationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var options = EngineJsonSerializerOptions.CreateWire();
        return request with
        {
            Analyses = Clone(request.Analyses, options),
            ShoppingPlans = Clone(request.ShoppingPlans, options),
            RecipeBasis = request.RecipeBasis is null
                ? null
                : Clone(request.RecipeBasis, options),
            PublishedScope = Clone(request.PublishedScope, options)
        };
    }

    public bool ShouldPersistNamedPlan(MarketAnalysisPublication publication) =>
        !string.IsNullOrEmpty(publication.PersistenceSnapshot.PlanId);

    public async Task<bool> PersistNamedPlanAsync(MarketAnalysisPublication publication)
    {
        _logger.LogInformation(
            "[stage] analysis persistence starting (plan {PlanId})",
            publication.Request.PlanId);
        var saved = await _store.SaveNamedPlanAsync(publication.PersistenceSnapshot);
        _logger.LogInformation("[stage] analysis persistence complete");
        return saved;
    }

    public async Task<bool> AutoSaveAsync(MarketAnalysisPublication publication)
    {
        _logger.LogInformation("[stage] autosave starting");
        var saved = await _store.AutoSaveAsync(publication.PersistenceSnapshot);
        _logger.LogInformation("[stage] autosave complete");
        return saved;
    }

    public async Task<MarketAnalysisPublication?> PublishLegacyAsync(
        MarketAnalysisPublicationRequest request,
        CancellationToken cancellationToken,
        MarketAnalysisExecutionOptions? executionOptions = null)
    {
        var preparedRequest = PrepareForRegistration(request, cancellationToken, snapshot: false);
        if (preparedRequest is null)
        {
            return null;
        }
        await YieldIfInteractiveAsync(executionOptions, cancellationToken);

        var persistenceSnapshot = CapturePersistenceSnapshot(
            preparedRequest,
            capturedAtUtc: null,
            marketIntelligenceId: null,
            requestAlreadySnapshotted: true);
        await YieldIfInteractiveAsync(executionOptions, cancellationToken);

        var publication = PublishPrepared(
            new PreparedMarketAnalysisPublication(
                preparedRequest,
                preparedRequest.PreparedDecisionChangeCount,
                persistenceSnapshot),
            cancellationToken);
        await YieldIfInteractiveAsync(executionOptions, cancellationToken);

        if (ShouldPersistNamedPlan(publication))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsCurrent(publication.Request))
            {
                return null;
            }
            await PersistNamedPlanAsync(publication);
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsCurrent(publication.Request))
            {
                return null;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!IsCurrent(publication.Request))
        {
            return null;
        }

        await AutoSaveAsync(publication);
        cancellationToken.ThrowIfCancellationRequested();
        return IsCurrent(publication.Request)
            ? publication
            : null;
    }

    private static async Task YieldIfInteractiveAsync(
        MarketAnalysisExecutionOptions? executionOptions,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if ((executionOptions ?? MarketAnalysisExecutionOptions.Synchronous).YieldEveryItems > 0)
        {
            await Task.Delay(1, cancellationToken);
        }
    }

    private bool IsCurrent(MarketAnalysisPublicationRequest request) =>
        _appState.IsCurrentPlanSession(request.Plan, request.PlanSessionVersion) &&
        _appState.CurrentVersions.PlanDecisionVersion == request.PlanDecisionVersion &&
        string.Equals(_appState.CurrentPlanId, request.PlanId, StringComparison.Ordinal) &&
        string.Equals(_appState.CurrentPlanName, request.PlanName, StringComparison.Ordinal);

    private static T Clone<T>(T value, JsonSerializerOptions options) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, options), options)
        ?? throw new InvalidOperationException($"Could not snapshot publication value '{typeof(T).Name}'.");

    internal static MarketAnalysisPersistenceSnapshot SnapshotPersistence(
        MarketAnalysisPersistenceSnapshot snapshot) =>
        Clone(snapshot, EngineJsonSerializerOptions.CreateWire());

    private static string ComputePublicationRequestHash(MarketAnalysisPublicationRequest request) =>
        EngineCanonicalHash.Compute(new
        {
            request.PlanSessionVersion,
            request.PlanDecisionVersion,
            request.PlanId,
            request.PlanName,
            request.Analyses,
            request.ShoppingPlans,
            request.RecipeBasis,
            request.PublishedScope,
            request.PreparedDecisionChangeCount
        }, EngineJsonSerializerOptions.CreateWire());
}
