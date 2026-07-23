using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services.Interfaces;
using Microsoft.Extensions.Logging;

namespace FFXIV_Craft_Architect.Core.Services;

/// <summary>
/// Service for calculating optimal market board shopping plans from cached market data.
/// 
/// DATA FLOW:
/// 1. Input: List of MaterialAggregate from CraftingPlan.AggregateMaterials
/// 2. Filter: Separates vendor items, market items, and untradeable items
/// 3. For each market item:
///    - Reads from IMarketCacheService (caller must populate first)
///    - Groups listings by world
///    - Calculates ValueScore for each world (see below)
///    - Recommends best world (lowest ValueScore)
///    - Optionally calculates multi-world splits
/// 4. Output: List of DetailedShoppingPlan with recommendations
/// 
/// VALUESCORE ALGORITHM:
/// ValueScore is the primary metric for world ranking. Lower is better.
/// - Base: Total cost for needed quantity from that world
/// - Fraud filter: Excludes listings above (ModePrice × Multiplier) default 2.5x
/// - Congestion penalty: Adds 20% to congested worlds (except home)
/// - Travel penalty: Adds 15% to non-home worlds (user preference)
/// - Stock penalty: World must have sufficient quantity or score is MaxValue
/// 
/// MODE PRICE CALCULATION (anti-fraud):
/// Uses a two-pass algorithm to find the "typical" price:
/// 1. Find median of cheapest 50% of listings (fraud-resistant baseline)
/// 2. Calculate mode (most common price) from listings within 10x of baseline
/// This prevents fraudulent high-price listings from skewing the mode.
/// 
/// MULTI-WORLD SPLITS:
/// When EnableSplitWorld is true, the algorithm can recommend buying portions
/// of the needed quantity from different worlds. Uses greedy allocation
/// based on ValueScore with single-world contingency (prefers single world
/// if within 5% of optimal split cost).
/// </summary>
public class MarketShoppingService
{
    private const int MaxProcurementRouteBeamWidth = 64;

    private readonly IMarketCacheService _cacheService;
    private readonly IWorldStatusService? _worldStatusService;
    private readonly SettingsService? _settingsService;
    private readonly ILogger<MarketShoppingService>? _logger;
    private Dictionary<string, int> _worldNameToIdMapping = new();

    public MarketShoppingService(
        IMarketCacheService cacheService,
        IWorldStatusService? worldStatusService = null,
        SettingsService? settingsService = null,
        ILogger<MarketShoppingService>? logger = null)
    {
        _cacheService = cacheService;
        _worldStatusService = worldStatusService;
        _settingsService = settingsService;
        _logger = logger;
    }

    /// <summary>
    /// Sets the world name to ID mapping for travel prohibition checks.
    /// </summary>
    public void SetWorldNameToIdMapping(Dictionary<string, int> mapping)
    {
        _worldNameToIdMapping = mapping;
    }

    /// <summary>
    /// Calculate detailed shopping plans for market board items.
    /// 
    /// ALGORITHM PER ITEM:
    /// 1. Read cached market data for item from IMarketCacheService
    /// 2. Group listings by world name
    /// 3. For each world:
    ///    - Calculate total cost for needed quantity
    ///    - Calculate mode price (anti-fraud baseline)
    ///    - Filter out listings above (ModePrice × MaxPriceMultiplier)
    ///    - Calculate ValueScore (see class documentation)
    ///    - Check world status (congested, travel prohibited)
    ///    - Apply filters (exclude congested, respect blacklist)
    /// 4. Sort worlds by ValueScore (ascending)
    /// 5. Set recommended world to first viable option
    /// 6. Return DetailedShoppingPlan with all options and recommendation
    /// 
    /// PREREQUISITE:
    /// Callers MUST populate the cache first via IMarketCacheService.EnsurePopulatedAsync
    /// before calling this method. This service reads from cache only.
    /// </summary>
    /// <param name="marketItems">Materials to analyze, usually from choice-aware recipe demand projection.</param>
    /// <param name="dataCenter">Data center to analyze</param>
    /// <param name="progress">Progress reporter for UI feedback (optional)</param>
    /// <param name="ct">Cancellation token</param>
    /// <param name="mode">Recommendation mode (cost vs value optimization)</param>
    /// <param name="config">Analysis configuration (fraud detection, split world, etc.)</param>
    /// <param name="blacklistedWorlds">Worlds to exclude from recommendations. Home worlds bypass this filter.</param>
    /// <returns>List of shopping plans, one per market item</returns>
    public async Task<List<DetailedShoppingPlan>> CalculateDetailedShoppingPlansAsync(
        List<MaterialAggregate> marketItems,
        string dataCenter,
        IProgress<string>? progress = null,
        CancellationToken ct = default,
        RecommendationMode mode = RecommendationMode.MinimizeTotalCost,
        MarketAnalysisConfig? config = null,
        HashSet<string>? blacklistedWorlds = null,
        MarketAnalysisExecutionOptions? executionOptions = null)
    {
        config ??= new MarketAnalysisConfig();  // Use defaults
        blacklistedWorlds ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var requests = marketItems
            .Select(item => (item.ItemId, dataCenter))
            .ToList();
        var entries = await GetManyCachedAsync(requests);
        var evidence = new MarketEvidenceSet(
            entries,
            requests,
            MarketFetchScope.SelectedDataCenter,
            [dataCenter],
            dataCenter,
            string.Empty,
            maxAge: null,
            fetchedCount: 0,
            loadedAtUtc: DateTime.UtcNow);

        return await CalculateDetailedShoppingPlansAsync(
            new MarketAnalysisRequest
            {
                Items = marketItems,
                Evidence = evidence,
                RecommendationMode = mode,
                AnalysisConfig = config,
                BlacklistedWorlds = blacklistedWorlds
            },
            progress,
            ct,
            executionOptions);
    }

    /// <summary>
    /// Calculate shopping plans with multi-world split recommendations.
    /// Single-pass algorithm: ValueScore is the single source of truth for all decisions.
    /// </summary>
    /// <param name="blacklistedWorlds">Worlds to exclude from recommendations. Home worlds bypass this filter.</param>
    public async Task<List<DetailedShoppingPlan>> CalculateShoppingPlansWithSplitsAsync(
        List<MaterialAggregate> marketItems,
        string dataCenter,
        IProgress<string>? progress = null,
        CancellationToken ct = default,
        MarketAnalysisConfig? config = null,
        HashSet<string>? blacklistedWorlds = null,
        MarketAnalysisExecutionOptions? executionOptions = null)
    {
        config ??= new MarketAnalysisConfig();
        blacklistedWorlds ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Pass 1: Calculate basic plans for all items
        progress?.Report("Calculating base recommendations...");
        var plans = await CalculateDetailedShoppingPlansAsync(marketItems, dataCenter, progress, ct,
            RecommendationMode.MinimizeTotalCost, config, blacklistedWorlds, executionOptions);

        progress?.Report("Optimizing procurement route...");
        return await OptimizeProcurementRouteAsync(
            plans,
            config,
            config.EnableSplitWorld,
            executionOptions,
            progress,
            ct);
    }

    /// <summary>
    /// Selects active procurement recommendations globally across a set of market evidence plans.
    /// The input plans are preserved, with their active recommendation updated for final choices.
    /// </summary>
    public List<DetailedShoppingPlan> OptimizeProcurementRoute(
        IEnumerable<DetailedShoppingPlan> evidencePlans,
        MarketAnalysisConfig? config = null,
        bool includeSplitPurchases = false)
    {
        return OptimizeProcurementRouteCoreAsync(
            evidencePlans,
            config,
            includeSplitPurchases,
            MarketAnalysisExecutionOptions.Synchronous,
            progress: null,
            CancellationToken.None)
            .GetAwaiter()
            .GetResult()
            .ShoppingPlans;
    }

    public Task<List<DetailedShoppingPlan>> OptimizeProcurementRouteAsync(
        IEnumerable<DetailedShoppingPlan> evidencePlans,
        MarketAnalysisConfig? config = null,
        bool includeSplitPurchases = false,
        MarketAnalysisExecutionOptions? executionOptions = null,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        return OptimizeProcurementRouteAsyncCore();

        async Task<List<DetailedShoppingPlan>> OptimizeProcurementRouteAsyncCore()
        {
            var result = await OptimizeProcurementRouteCoreAsync(
                evidencePlans,
                config,
                includeSplitPurchases,
                executionOptions ?? MarketAnalysisExecutionOptions.Interactive,
                progress,
                ct);
            return result.ShoppingPlans;
        }
    }

    public Task<ProcurementRouteOptimizationResult> OptimizeProcurementRouteWithDecisionAsync(
        IEnumerable<DetailedShoppingPlan> evidencePlans,
        MarketAnalysisConfig? config = null,
        bool includeSplitPurchases = false,
        MarketAnalysisExecutionOptions? executionOptions = null,
        IProgress<string>? progress = null,
        CancellationToken ct = default,
        long? absoluteMaximumGilCost = null)
    {
        return OptimizeProcurementRouteCoreAsync(
            evidencePlans,
            config,
            includeSplitPurchases,
            executionOptions ?? MarketAnalysisExecutionOptions.Interactive,
            progress,
            ct,
            absoluteMaximumGilCost);
    }

    private async Task<ProcurementRouteOptimizationResult> OptimizeProcurementRouteCoreAsync(
        IEnumerable<DetailedShoppingPlan> evidencePlans,
        MarketAnalysisConfig? config,
        bool includeSplitPurchases,
        MarketAnalysisExecutionOptions executionOptions,
        IProgress<string>? progress,
        CancellationToken ct,
        long? absoluteMaximumGilCost = null)
    {
        ArgumentNullException.ThrowIfNull(evidencePlans);

        config ??= new MarketAnalysisConfig();
        var plans = evidencePlans
            .Select(ClonePlanForRouteOptimization)
            .ToList();
        if (plans.Count == 0)
        {
            return new ProcurementRouteOptimizationResult(plans, null);
        }
        var candidateWorkBudget = new MarketRouteCandidateWorkBudget(executionOptions, ct);

        var candidatePlanIndexes = plans
            .Select((plan, index) => new { Plan = plan, Index = index })
            .Where(p => !IsFixedVendorPlan(p.Plan))
            .ToList();
        var fixedVendorGilCost = plans
            .Where(IsFixedVendorPlan)
            .Select(plan => plan.RecommendedWorld?.TotalCost ?? 0)
            .Aggregate(0L, SaturatingAdd);
        var hasFixedVendorPurchases = fixedVendorGilCost > 0;
        var standaloneCandidatesByPlan = candidatePlanIndexes.ToDictionary(
            entry => entry.Index,
            entry => GeneratePurchaseCandidates(entry.Plan, currentRoute: null, candidateWorkBudget)
                .Where(candidate => candidate.IsFullyFulfilled)
                .Where(candidate => includeSplitPurchases || !candidate.IsSplitPurchase)
                .ToList());
        var cheapestEligibleCandidateByPlan = standaloneCandidatesByPlan.ToDictionary(
            pair => pair.Key,
            pair => pair.Value
                .Where(candidate => candidate.HasTrustworthyEvidence)
                .OrderBy(candidate => candidate.GilCost)
                .ThenBy(candidate => candidate.MarketEvidencePenalty)
                .ThenBy(candidate => candidate.Worlds.Count)
                .FirstOrDefault());

        var beam = new List<ProcurementRouteSearchState>
        {
            new(fixedVendorGilCost)
        };
        var routeSearchWasTruncated = candidateWorkBudget.WasTruncated;

        for (var planIndex = 0; planIndex < candidatePlanIndexes.Count; planIndex++)
        {
            ct.ThrowIfCancellationRequested();
            var planEntry = candidatePlanIndexes[planIndex];
            var nextBeam = new List<ProcurementRouteSearchState>();

            for (var stateIndex = 0; stateIndex < beam.Count; stateIndex++)
            {
                ct.ThrowIfCancellationRequested();
                var state = beam[stateIndex];
                var routeCandidates = planEntry.Plan.HqQuantityNeeded > 0
                    ? GeneratePurchaseCandidates(planEntry.Plan, state.Route, candidateWorkBudget)
                    : standaloneCandidatesByPlan.GetValueOrDefault(planEntry.Index) ?? [];
                var candidates = routeCandidates
                    .Where(c => c.IsFullyFulfilled)
                    .Where(c => includeSplitPurchases || !c.IsSplitPurchase)
                    .Where(c => c.HasTrustworthyEvidence)
                    .ToList();
                routeSearchWasTruncated |= candidateWorkBudget.WasTruncated;
                var hadEligibleCandidate = candidates.Count > 0;
                if (absoluteMaximumGilCost.HasValue)
                {
                    candidates = candidates
                        .Where(candidate =>
                            candidate.GilCost <= absoluteMaximumGilCost.Value - state.TotalGilCost)
                        .ToList();
                }
                candidates = candidates
                    .OrderBy(c => c, Comparer<MarketPurchaseCandidate>.Create(
                        (left, right) => MarketRouteScoring.CompareCandidates(left, right, state.Route, config)))
                    .ThenBy(c => c.ItemId)
                    .ThenBy(c => c.ItemName)
                    .ToList();

                if (candidates.Count == 0)
                {
                    if (absoluteMaximumGilCost.HasValue && hadEligibleCandidate)
                    {
                        continue;
                    }
                    nextBeam.Add(state.WithoutRecommendation(planEntry.Index));
                    continue;
                }

                foreach (var candidate in candidates)
                {
                    nextBeam.Add(state.WithCandidate(planEntry.Index, candidate));
                }

                var completedStates = stateIndex + 1;
                if (executionOptions.ShouldYieldAfterItem(completedStates))
                {
                    await Task.Yield();
                }
            }

            beam = PruneRouteBeam(nextBeam, config, out var stepWasTruncated);
            routeSearchWasTruncated |= stepWasTruncated;

            var completedItems = planIndex + 1;
            if (executionOptions.ShouldReportProgress(completedItems))
            {
                progress?.Report($"Optimizing procurement route {completedItems}/{candidatePlanIndexes.Count}...");
            }

            if (executionOptions.ShouldYieldAfterItem(completedItems))
            {
                await Task.Yield();
            }
        }

        var frontier = ReduceToParetoFrontier(beam, config);
        var cheapestState = frontier
            .OrderBy(state => state.TotalGilCost)
            .ThenBy(state => state.TotalEvidencePenalty)
            .ThenBy(state => state.TieBreakKey, StringComparer.Ordinal)
            .FirstOrDefault();
        var bestState = OrderRouteStates(frontier, config, absoluteMaximumGilCost: absoluteMaximumGilCost).FirstOrDefault();

        if (bestState == null)
        {
            return new ProcurementRouteOptimizationResult(plans, null, IsComplete: false);
        }

        ApplyRouteState(plans, bestState, standaloneCandidatesByPlan);

        var representativeRoutes = BuildRepresentativeRoutes(frontier, config);
        var itemDecisions = BuildItemDecisions(bestState, cheapestEligibleCandidateByPlan);
        var toleranceSelections = BuildRouteToleranceSelections(
            frontier,
            config,
            plans,
            standaloneCandidatesByPlan,
            cheapestEligibleCandidateByPlan,
            fixedVendorGilCost);
        var decision = cheapestState == null ||
            !hasFixedVendorPurchases && bestState.Choices.All(choice => choice.Candidate == null)
            ? null
            : new MarketRouteDecision(
                config.TravelTolerance,
                MarketRouteScoring.GetMaximumPremiumRate(config.TravelTolerance),
                cheapestState.TotalGilCost,
                bestState.TotalGilCost,
                bestState.TotalEvidencePenalty,
                cheapestState.WorldStops,
                bestState.WorldStops,
                cheapestState.GetDataCenterTransfers(config),
                bestState.GetDataCenterTransfers(config),
                config.StartFromHomeDataCenter && !string.IsNullOrWhiteSpace(config.HomeDataCenter),
                config.StartFromHomeDataCenter ? config.HomeDataCenter : null,
                config.TravelPriority,
                representativeRoutes,
                itemDecisions)
            {
                FixedAcquisitionGilCost = fixedVendorGilCost,
                RouteSearchWasTruncated = routeSearchWasTruncated,
                ToleranceSelections = toleranceSelections
            };

        var isComplete = bestState.Choices.All(choice => choice.Candidate != null);
        return new ProcurementRouteOptimizationResult(plans, decision, isComplete);
    }

    private static IReadOnlyList<MarketRouteToleranceSelection> BuildRouteToleranceSelections(
        IReadOnlyList<ProcurementRouteSearchState> frontier,
        MarketAnalysisConfig config,
        IReadOnlyList<DetailedShoppingPlan> sourcePlans,
        IReadOnlyDictionary<int, List<MarketPurchaseCandidate>> standaloneCandidatesByPlan,
        IReadOnlyDictionary<int, MarketPurchaseCandidate?> cheapestEligibleCandidateByPlan,
        long fixedVendorGilCost)
    {
        var selections = new List<MarketRouteToleranceSelection>();
        ProcurementRouteSearchState? previous = null;
        for (var tolerance = 0; tolerance <= 11; tolerance++)
        {
            var state = OrderRouteStates(frontier, config, tolerance).FirstOrDefault();
            if (state == null)
            {
                continue;
            }

            if (previous != null &&
                string.Equals(previous.TieBreakKey, state.TieBreakKey, StringComparison.Ordinal) &&
                selections.Count > 0)
            {
                selections[^1] = selections[^1] with { MaximumTolerance = tolerance };
                continue;
            }

            var selectedPlans = sourcePlans.Select(ClonePlanForRouteOptimization).ToList();
            ApplyRouteState(selectedPlans, state, standaloneCandidatesByPlan);
            selections.Add(new MarketRouteToleranceSelection(
                tolerance,
                tolerance,
                state.TieBreakKey,
                state.TotalGilCost,
                state.TotalEvidencePenalty,
                state.WorldStops,
                state.GetDataCenterTransfers(config),
                fixedVendorGilCost,
                ProcurementRouteExecutionService.CompactResultShoppingPlans(selectedPlans),
                BuildItemDecisions(state, cheapestEligibleCandidateByPlan)));
            previous = state;
        }

        return selections;
    }

    private static List<MarketRouteItemDecision> BuildItemDecisions(
        ProcurementRouteSearchState state,
        IReadOnlyDictionary<int, MarketPurchaseCandidate?> cheapestEligibleCandidateByPlan) =>
        state.Choices
            .Where(choice => choice.Candidate != null)
            .Select(choice =>
            {
                var selected = choice.Candidate!;
                var cheapest = cheapestEligibleCandidateByPlan.GetValueOrDefault(choice.PlanIndex);
                return new MarketRouteItemDecision(
                    selected.ItemId,
                    selected.ItemName,
                    cheapest?.GilCost ?? selected.GilCost,
                    selected.GilCost);
            })
            .ToList();

    private static void ApplyRouteState(
        List<DetailedShoppingPlan> plans,
        ProcurementRouteSearchState state,
        IReadOnlyDictionary<int, List<MarketPurchaseCandidate>> standaloneCandidatesByPlan)
    {
        foreach (var plan in plans)
        {
            if (IsFixedVendorPlan(plan))
            {
                plan.RecommendedSplit = null;
                continue;
            }

            plan.RecommendedWorld = null;
            plan.RecommendedSplit = null;
            plan.CoverageSet = null;
        }

        foreach (var choice in state.Choices)
        {
            var plan = plans[choice.PlanIndex];
            if (choice.Candidate == null)
            {
                var standaloneCandidates = standaloneCandidatesByPlan.GetValueOrDefault(choice.PlanIndex) ?? [];
                if (standaloneCandidates.Count > 0 && standaloneCandidates.All(candidate => !candidate.HasTrustworthyEvidence))
                {
                    plan.Error ??= "Market evidence is 12 hours old or older. Refresh this item before routing it.";
                }
                continue;
            }

            if (choice.Candidate.Coverage != null)
            {
                plan.CoverageSet = CreateSelectedCoverageSet(plan, choice.Candidate.Coverage);
                plan.RecommendedWorld = choice.Candidate.Coverage.Worlds.Count == 1
                    ? FindWorldOption(plan, choice.Candidate.Coverage.Worlds[0])
                    : null;
                plan.RecommendedSplit = null;
                continue;
            }

            if (choice.Candidate.SingleWorld != null)
            {
                plan.RecommendedWorld = choice.Candidate.SingleWorld;
                plan.RecommendedSplit = null;
                continue;
            }

            if (choice.Candidate.Split?.Any() == true)
            {
                plan.RecommendedWorld = null;
                plan.RecommendedSplit = choice.Candidate.Split;
            }
        }
    }

    private static MarketCoverageSet CreateSelectedCoverageSet(
        DetailedShoppingPlan plan,
        MarketCoverageOption selectedCoverage)
    {
        var selected = selectedCoverage with { IsDefaultEligible = true };

        return new MarketCoverageSet(
            plan.ItemId,
            plan.Name,
            plan.QuantityNeeded,
            selected.Tier == MarketCoverageTier.SingleWorld ? selected : null,
            selected.Tier == MarketCoverageTier.CompactSplit ? selected : null,
            selected.Tier == MarketCoverageTier.WideSplit ? selected : null,
            selected.Tier == MarketCoverageTier.CheapestObserved ? selected : null,
            [selected]);
    }

    private static WorldShoppingSummary? FindWorldOption(
        DetailedShoppingPlan plan,
        MarketCoverageWorld coverageWorld)
    {
        return plan.WorldOptions.FirstOrDefault(option =>
            string.Equals(option.DataCenter, coverageWorld.DataCenter, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(option.WorldName, coverageWorld.WorldName, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<List<DetailedShoppingPlan>> CalculateDetailedShoppingPlansAsync(
        MarketAnalysisRequest request,
        IProgress<string>? progress = null,
        CancellationToken ct = default,
        MarketAnalysisExecutionOptions? executionOptions = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Evidence);

        var config = request.AnalysisConfig ?? new MarketAnalysisConfig();
        var execution = executionOptions ?? MarketAnalysisExecutionOptions.Synchronous;
        var plans = new List<DetailedShoppingPlan>();

        for (var itemIndex = 0; itemIndex < request.Items.Count; itemIndex++)
        {
            var item = request.Items[itemIndex];
            var completedItems = itemIndex + 1;
            if (execution.ShouldReportProgress(completedItems))
            {
                progress?.Report($"Analyzing {completedItems}/{request.Items.Count}: {item.Name}...");
            }

            ct.ThrowIfCancellationRequested();

            try
            {
                var cachedEntries = request.Evidence.GetEntriesForItem(item.ItemId);
                if (cachedEntries.Count == 0)
                {
                    plans.Add(new DetailedShoppingPlan
                    {
                        ItemId = item.ItemId,
                        Name = item.Name,
                        QuantityNeeded = item.TotalQuantity,
                        Error = "No market data in cache"
                    });
                    continue;
                }

                var listings = cachedEntries
                    .SelectMany(entry => ConvertCachedDataToListings(
                        entry,
                        entry.DataCenter,
                        request.BlacklistedMarketWorlds))
                    .ToList();
                var averagePrice = CalculateAveragePrice(cachedEntries);
                var dataCenter = cachedEntries.Count == 1 ? cachedEntries[0].DataCenter : null;

                var plan = CalculateItemShoppingPlan(
                    item.Name,
                    item.ItemId,
                    item.TotalQuantity,
                    listings,
                    averagePrice,
                    dataCenter,
                    request.RecommendationMode,
                    config,
                    request.BlacklistedWorlds);
                var missingDataCenters = request.Evidence.MissingRequests
                    .Where(pair => pair.itemId == item.ItemId)
                    .Select(pair => pair.dataCenter)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(dataCenterName => dataCenterName, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (missingDataCenters.Count > 0)
                {
                    plan.MarketDataWarning = $"Market data incomplete for {string.Join(", ", missingDataCenters)}; recommendations use available cache data only.";
                }

                plans.Add(plan);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to calculate shopping plan for {ItemName}", item.Name);
                plans.Add(new DetailedShoppingPlan
                {
                    ItemId = item.ItemId,
                    Name = item.Name,
                    QuantityNeeded = item.TotalQuantity,
                    Error = ex.Message
                });
            }
            finally
            {
                if (execution.ShouldYieldAfterItem(completedItems))
                {
                    await Task.Yield();
                }
            }
        }

        return plans;
    }

    private static List<MarketListing> ConvertCachedDataToListings(
        CachedMarketData cached,
        string dataCenter,
        HashSet<MarketWorldKey>? blacklistedMarketWorlds = null)
    {
        var listings = new List<MarketListing>();
        var listingDataCenter = !string.IsNullOrWhiteSpace(cached.DataCenter)
            ? cached.DataCenter
            : dataCenter;

        foreach (var world in cached.Worlds)
        {
            if (IsBlacklistedMarketWorld(listingDataCenter, world.WorldName, blacklistedMarketWorlds))
            {
                continue;
            }

            foreach (var listing in world.Listings)
            {
                listings.Add(new MarketListing
                {
                    WorldName = world.WorldName,
                    DataCenterName = listingDataCenter,
                    Quantity = listing.Quantity,
                    PricePerUnit = listing.PricePerUnit,
                    RetainerName = listing.RetainerName,
                    IsHq = listing.IsHq
                });
            }
        }

        return listings;
    }

    private DetailedShoppingPlan CalculateItemShoppingPlan(
        string itemName,
        int itemId,
        int quantityNeeded,
        List<MarketListing> listings,
        decimal averagePrice,
        string? dataCenter = null,
        RecommendationMode mode = RecommendationMode.MinimizeTotalCost,
        MarketAnalysisConfig? config = null,
        HashSet<string>? blacklistedWorlds = null)
    {
        blacklistedWorlds ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var plan = new DetailedShoppingPlan
        {
            ItemId = itemId,
            Name = itemName,
            QuantityNeeded = quantityNeeded,
            DCAveragePrice = averagePrice
        };

        if (listings.Count == 0)
        {
            plan.Error = "No market listings found";
            return plan;
        }

        // Get user's home world (if set)
        var homeWorld = _settingsService?.Get<string>("market.home_world", "");
        var hasHomeWorld = !string.IsNullOrWhiteSpace(homeWorld);

        // Check if we should exclude congested worlds
        var excludeCongested = _settingsService?.Get<bool>("market.exclude_congested_worlds", true) ?? true;

        // Group listings by structured world identity. World names can collide across data centers.
        var listingsByWorld = listings
            .GroupBy(l => new
            {
                l.WorldName,
                DataCenterName = !string.IsNullOrWhiteSpace(l.DataCenterName)
                    ? l.DataCenterName
                    : dataCenter ?? string.Empty
            })
            .ToDictionary(g => g.Key, g => g.OrderBy(l => l.PricePerUnit).ToList());

        // Calculate per-world summaries
        foreach (var (worldKey, worldListings) in listingsByWorld)
        {
            var worldName = worldKey.WorldName;
            var worldSummary = CalculateWorldSummary(worldName, worldKey.DataCenterName, worldListings, quantityNeeded, plan.DCAveragePrice, homeWorld, config);
            if (worldSummary != null)
            {
                // Skip congested worlds (except home world) if setting is enabled
                if (excludeCongested && worldSummary.IsCongested && !worldSummary.IsHomeWorld)
                {
                    _logger?.LogDebug("[MarketShopping] Excluding {World} - congested world", worldName);
                    continue;
                }

                // Skip blacklisted worlds (except home world)
                if (blacklistedWorlds.Contains(worldName) && !worldSummary.IsHomeWorld)
                {
                    _logger?.LogDebug("[MarketShopping] Excluding {World} - user blacklisted", worldName);
                    worldSummary.IsBlacklisted = true;
                    continue;
                }

                plan.WorldOptions.Add(worldSummary);
            }
        }

        // Calculate ValueScore for each world (single-world mode)
        foreach (var world in plan.WorldOptions)
        {
            world.ValueScore = CalculateValueScore(world, quantityNeeded, splitEnabled: false);
        }

        // Simple sort by ValueScore (lower is better)
        plan.WorldOptions = plan.WorldOptions
            .OrderBy(w => w.ValueScore)
            .ThenBy(w => w.WorldName)
            .ToList();

        // Set recommended option to first viable world (ValueScore < MaxValue)
        var bestWorld = plan.WorldOptions.FirstOrDefault(w => w.ValueScore < decimal.MaxValue);
        if (bestWorld != null)
        {
            plan.RecommendedWorld = bestWorld;
        }

        plan.CoverageSet = MarketCoverageBuilder.Build(plan);
        return plan;
    }

    /// <summary>
    /// Calculate the mode price - the price with the highest available quantity.
    /// Uses a two-pass approach: first find a reasonable baseline, then calculate mode
    /// from listings within 10x of that baseline to avoid fraud skewing the mode.
    /// </summary>
    private long CalculateModePrice(List<MarketListing> listings)
    {
        if (listings.Count == 0)
        {
            return 0;
        }

        // Pass 1: Get a fraud-resistant baseline using median of cheapest 50%
        var sortedByPrice = listings.OrderBy(l => l.PricePerUnit).ToList();
        var halfCount = Math.Max(1, sortedByPrice.Count / 2);
        var cheapestHalf = sortedByPrice.Take(halfCount);
        var baselinePrice = cheapestHalf.Any()
            ? cheapestHalf.Average(l => (decimal)l.PricePerUnit)
            : sortedByPrice.First().PricePerUnit;

        // Pass 2: Calculate mode only from listings within 10x of baseline
        // This prevents fraudulent listings from skewing the mode
        var reasonableListings = listings
            .Where(l => l.PricePerUnit <= baselinePrice * 10)
            .ToList();

        if (reasonableListings.Count == 0)
        {
            reasonableListings = sortedByPrice.Take(3).ToList(); // Fallback to cheapest 3
        }

        return reasonableListings
            .GroupBy(l => l.PricePerUnit)
            .Select(g => new { Price = g.Key, Quantity = g.Sum(l => l.Quantity) })
            .OrderByDescending(x => x.Quantity)
            .ThenBy(x => x.Price)
            .FirstOrDefault()?.Price ?? 0;
    }

    /// <summary>
    /// Calculate ValueScore - the single metric for world ranking.
    /// 
    /// Split mode: ValueScore = ModePrice / StockRatio
    /// - Worlds with more stock relative to need get better scores
    /// - Worlds with lower mode prices get better scores
    /// 
    /// Single-world mode: ValueScore = TotalCost
    /// - Returns MaxValue (infinity) if world can't fulfill full quantity
    /// - Lower total cost is better
    /// </summary>
    private decimal CalculateValueScore(
        WorldShoppingSummary world,
        int quantityNeeded,
        bool splitEnabled)
    {
        if (splitEnabled)
        {
            // Split mode: ValueScore = ModePrice / StockRatio
            var stockRatio = Math.Min((decimal)world.TotalQuantityPurchased / quantityNeeded, 1.0m);
            if (stockRatio <= 0)
            {
                return decimal.MaxValue;
            }

            var modePrice = world.ModePricePerUnit;
            if (modePrice <= 0)
            {
                return decimal.MaxValue;
            }

            return modePrice / stockRatio;
        }
        else
        {
            // Single-world mode: ValueScore = TotalCost, Infinity if can't fulfill
            if (world.TotalQuantityPurchased < quantityNeeded)
            {
                return decimal.MaxValue;
            }

            return world.TotalCost;
        }
    }

    private WorldShoppingSummary? CalculateWorldSummary(
        string worldName,
        string dataCenter,
        List<MarketListing> listings,
        int quantityNeeded,
        decimal dcAveragePrice,
        string? homeWorld = null,
        MarketAnalysisConfig? config = null)
    {
        // Get world status (if available)
        var worldStatus = _worldStatusService?.GetWorldStatus(worldName);

        var isHomeWorld = !string.IsNullOrWhiteSpace(homeWorld) &&
                         worldName.Equals(homeWorld, StringComparison.OrdinalIgnoreCase);

        var summary = new WorldShoppingSummary
        {
            DataCenter = dataCenter,
            WorldName = worldName,
            Listings = new List<ShoppingListingEntry>(),
            ExcludedListings = new List<ShoppingListingEntry>(),
            IsHomeWorld = isHomeWorld,
            Classification = worldStatus?.Classification ?? WorldClassification.Standard,
            MarketDataQualityScore = 70,
            MarketDataQualityBucket = MarketDataQualityBucket.Aging,
            MarketDataAgeSource = MarketDataAgeSource.LocalFetchFallback
        };

        var bestListing = listings.FirstOrDefault();
        if (bestListing != null)
        {
            summary.BestSingleListing = new ShoppingListingEntry
            {
                Quantity = bestListing.Quantity,
                PricePerUnit = bestListing.PricePerUnit,
                RetainerName = bestListing.RetainerName,
                IsUnderAverage = bestListing.PricePerUnit <= dcAveragePrice,
                IsHq = bestListing.IsHq
            };
        }

        // Calculate mode price for fraud detection threshold
        summary.ModePricePerUnit = CalculateModePrice(listings);
        var maxPriceMultiplier = config?.MaxPriceMultiplier ?? 2.5m; // Default 2.5x if not specified
        var maxPriceThreshold = summary.ModePricePerUnit > 0
            ? (long)(summary.ModePricePerUnit * maxPriceMultiplier)
            : long.MaxValue;

        _logger?.LogDebug("[FRAUD_CHECK] {WorldName}: ModePrice={ModePrice}, Multiplier={Multiplier}, Threshold={Threshold}, TotalListings={Count}",
            worldName, summary.ModePricePerUnit, maxPriceMultiplier, maxPriceThreshold, listings.Count);

        var remaining = quantityNeeded;
        long totalCost = 0;
        int listingsUsed = 0;
        int fraudSkipped = 0;

        // Include listings - skip fraud/gouging listings based on price threshold
        // This prevents "desperation recommendations" with extremely overpriced listings
        foreach (var listing in listings)
        {
            // Check if listing exceeds fraud threshold (soft filter - skip but continue scanning)
            if (listing.PricePerUnit > maxPriceThreshold)
            {
                _logger?.LogDebug("[FRAUD_DETECTED] {WorldName}: Excluding listing - Price={Price}, Threshold={Threshold}, Retainer={Retainer}",
                    worldName, listing.PricePerUnit, maxPriceThreshold, listing.RetainerName);
                fraudSkipped++;
                summary.ExcludedListings.Add(new ShoppingListingEntry
                {
                    Quantity = listing.Quantity,
                    PricePerUnit = listing.PricePerUnit,
                    RetainerName = listing.RetainerName,
                    IsUnderAverage = listing.PricePerUnit <= dcAveragePrice,
                    IsHq = listing.IsHq,
                    IsAdditionalOption = true  // Mark as excluded/not primary
                });
                continue;  // Skip this listing but keep scanning
            }

            if (remaining <= 0)
            {
                break;
            }

            var isUnderAverage = listing.PricePerUnit <= dcAveragePrice;

            var neededFromStack = Math.Min(listing.Quantity, remaining);
            var fullStackCost = listing.Quantity * listing.PricePerUnit;
            totalCost += fullStackCost;
            remaining -= neededFromStack;
            listingsUsed++;

            summary.Listings.Add(new ShoppingListingEntry
            {
                Quantity = listing.Quantity,
                PricePerUnit = listing.PricePerUnit,
                RetainerName = listing.RetainerName,
                IsUnderAverage = isUnderAverage,
                IsHq = listing.IsHq,
                NeededFromStack = neededFromStack,
                ExcessQuantity = Math.Max(0, listing.Quantity - neededFromStack)
            });
        }

        // Second pass: add top 2 additional listings for value comparison
        var additionalListings = listings
            .Where(l => !summary.Listings.Any(sl => sl.RetainerName == l.RetainerName && sl.Quantity == l.Quantity))
            .Take(2);

        foreach (var listing in additionalListings)
        {
            summary.Listings.Add(new ShoppingListingEntry
            {
                Quantity = listing.Quantity,
                PricePerUnit = listing.PricePerUnit,
                RetainerName = listing.RetainerName,
                IsUnderAverage = listing.PricePerUnit <= dcAveragePrice,
                IsHq = listing.IsHq,
                IsAdditionalOption = true,
                NeededFromStack = 0,
                ExcessQuantity = listing.Quantity
            });
        }

        // Never return null - always return what we can purchase from this world
        // The split calculation will handle combining multiple worlds if needed
        if (remaining > 0 && listingsUsed == 0)
        {
            _logger?.LogDebug("[CalculateWorldSummary] {World} - No usable listings for {Quantity}",
                worldName, quantityNeeded);
        }

        summary.TotalCost = totalCost;
        summary.TotalQuantityPurchased = summary.Listings.Where(l => !l.IsAdditionalOption).Sum(l => l.Quantity);
        summary.AveragePricePerUnit = summary.TotalQuantityPurchased > 0
            ? (decimal)totalCost / summary.TotalQuantityPurchased
            : 0;
        summary.ListingsUsed = listingsUsed;
        summary.IsFullyUnderAverage = summary.Listings.Where(l => !l.IsAdditionalOption).All(l => l.IsUnderAverage);
        summary.ExcessQuantity = summary.TotalQuantityPurchased - quantityNeeded;
        summary.HasSufficientStock = remaining <= 0;
        summary.ShortfallQuantity = remaining > 0 ? remaining : 0;

        _logger?.LogDebug("[CalculateWorldSummary] {World} - Purchased: {Purchased}/{Needed}, Cost: {Cost:N0}g, FraudSkipped: {Fraud}, Sufficient: {Sufficient}",
            worldName, summary.TotalQuantityPurchased, quantityNeeded, totalCost, fraudSkipped, summary.HasSufficientStock);

        return summary;
    }

    // North American Data Centers for cross-DC travel searches
    private static readonly string[] NorthAmericanDCs = { "Aether", "Primal", "Crystal", "Dynamis" };

    /// <summary>
    /// Calculate shopping plans searching across all NA Data Centers for potential savings.
    /// Reads from cache only - callers must ensure cache is populated first.
    /// </summary>
    public async Task<List<DetailedShoppingPlan>> CalculateDetailedShoppingPlansMultiDCAsync(
        List<MaterialAggregate> marketItems,
        IProgress<string>? progress = null,
        CancellationToken ct = default,
        RecommendationMode mode = RecommendationMode.MinimizeTotalCost,
        MarketAnalysisConfig? config = null,
        HashSet<MarketWorldKey>? blacklistedMarketWorlds = null,
        HashSet<string>? blacklistedWorlds = null)
    {
        config ??= new MarketAnalysisConfig();  // Use defaults
        blacklistedMarketWorlds ??= new HashSet<MarketWorldKey>();
        blacklistedWorlds ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var requests = marketItems
            .SelectMany(item => NorthAmericanDCs.Select(dc => (item.ItemId, dc)))
            .ToList();
        var entries = await GetManyCachedAsync(requests);
        var evidence = new MarketEvidenceSet(
            entries,
            requests,
            MarketFetchScope.EntireRegion,
            NorthAmericanDCs,
            NorthAmericanDCs[0],
            "North America",
            maxAge: null,
            fetchedCount: 0,
            loadedAtUtc: DateTime.UtcNow);

        return await CalculateDetailedShoppingPlansAsync(
            new MarketAnalysisRequest
            {
                Items = marketItems,
                Evidence = evidence,
                RecommendationMode = mode,
                AnalysisConfig = config,
                BlacklistedMarketWorlds = blacklistedMarketWorlds,
                BlacklistedWorlds = blacklistedWorlds
            },
            progress,
            ct);
    }

    private async Task<IReadOnlyDictionary<(int itemId, string dataCenter), CachedMarketData>> GetManyCachedAsync(
        IReadOnlyCollection<(int itemId, string dataCenter)> requests)
    {
        var entries = await _cacheService.GetManyAsync(requests);
        if (entries != null)
        {
            return entries;
        }

        var fallback = new Dictionary<(int itemId, string dataCenter), CachedMarketData>();
        foreach (var (itemId, dataCenter) in requests)
        {
            var cached = await _cacheService.GetAsync(itemId, dataCenter);
            if (cached != null)
            {
                fallback[(itemId, dataCenter)] = cached;
            }
        }

        return fallback;
    }

    private static decimal CalculateAveragePrice(IReadOnlyList<CachedMarketData> entries)
    {
        var pricedEntries = entries
            .Where(entry => entry.DCAveragePrice > 0)
            .ToList();

        return pricedEntries.Count == 0
            ? 0
            : pricedEntries.Average(entry => entry.DCAveragePrice);
    }

    private static bool IsBlacklistedMarketWorld(
        string dataCenter,
        string worldName,
        HashSet<MarketWorldKey>? blacklistedMarketWorlds)
    {
        return blacklistedMarketWorlds?.Contains(new MarketWorldKey(dataCenter, worldName)) == true;
    }

    /// <summary>
    /// Calculate craft-vs-buy analysis for all craftable items in a plan.
    /// </summary>
    public List<CraftVsBuyAnalysis> AnalyzeCraftVsBuy(CraftingPlan plan, Dictionary<int, PriceInfo> marketPrices)
    {
        var analyses = new List<CraftVsBuyAnalysis>();

        foreach (var rootItem in plan.RootItems)
        {
            AnalyzeNodeCraftVsBuy(rootItem, marketPrices, analyses);
        }

        return analyses.OrderByDescending(a => a.PotentialSavingsNq).ToList();
    }

    private void AnalyzeNodeCraftVsBuy(PlanNode node, Dictionary<int, PriceInfo> marketPrices, List<CraftVsBuyAnalysis> analyses)
    {
        if (node.Children.Any())
        {
            marketPrices.TryGetValue(node.ItemId, out var priceInfo);

            var buyPriceNq = priceInfo?.UnitPrice * node.Quantity ?? 0;
            var buyPriceHq = priceInfo?.HqUnitPrice * node.Quantity ?? 0;
            var hasHqData = priceInfo?.HasHqData ?? false;

            var componentCost = CalculateComponentCost(node, marketPrices);

            var savingsNq = buyPriceNq - componentCost;
            var savingsPercentNq = buyPriceNq > 0 ? (savingsNq / buyPriceNq) * 100 : 0;

            var savingsHq = hasHqData ? buyPriceHq - componentCost : 0;
            var savingsPercentHq = (hasHqData && buyPriceHq > 0) ? (savingsHq / buyPriceHq) * 100 : 0;

            analyses.Add(new CraftVsBuyAnalysis
            {
                ItemId = node.ItemId,
                ItemName = node.Name,
                Quantity = node.Quantity,
                BuyCostNq = buyPriceNq,
                CraftCost = componentCost,
                PotentialSavingsNq = savingsNq,
                SavingsPercentNq = savingsPercentNq,
                BuyCostHq = buyPriceHq,
                PotentialSavingsHq = savingsHq,
                SavingsPercentHq = savingsPercentHq,
                HasHqData = hasHqData,
                IsHqRequired = node.MustBeHq,
                IsCurrentlySetToCraft = !node.IsBuy,
                RecommendationNq = savingsNq > 0 ? CraftRecommendation.Craft : CraftRecommendation.Buy,
                RecommendationHq = hasHqData && savingsHq > 0 ? CraftRecommendation.Craft : CraftRecommendation.Buy
            });

            foreach (var child in node.Children)
            {
                AnalyzeNodeCraftVsBuy(child, marketPrices, analyses);
            }
        }
    }

    private decimal CalculateComponentCost(PlanNode node, Dictionary<int, PriceInfo> marketPrices)
    {
        decimal total = 0;

        foreach (var child in node.Children)
        {
            if (child.IsBuy || !child.Children.Any())
            {
                // Check for vendor price first (preferred over market price)
                if (child.Source == AcquisitionSource.VendorBuy && child.VendorPrice > 0)
                {
                    total += child.VendorPrice * child.Quantity;
                }
                else if (marketPrices.TryGetValue(child.ItemId, out var priceInfo))
                {
                    total += priceInfo.UnitPrice * child.Quantity;
                }
            }
            else
            {
                total += CalculateComponentCost(child, marketPrices);
            }
        }

        // Account for recipe yield: cost per item = total ingredient cost / yield
        if (node.Yield > 1)
        {
            return total / node.Yield;
        }

        return total;
    }

    // ========================================================================
    // Multi-World Split Purchase Calculation
    // ========================================================================

    /// <summary>
    /// Generates feasible purchase candidates for route-aware market optimization.
    /// </summary>
    public List<MarketPurchaseCandidate> GeneratePurchaseCandidates(
        DetailedShoppingPlan plan,
        MarketRouteState? currentRoute = null) =>
        GeneratePurchaseCandidates(plan, currentRoute, budget: null);

    private List<MarketPurchaseCandidate> GeneratePurchaseCandidates(
        DetailedShoppingPlan plan,
        MarketRouteState? currentRoute,
        MarketRouteCandidateWorkBudget? budget)
    {
        var candidates = new List<MarketPurchaseCandidate>();
        if (plan.QuantityNeeded <= 0 || plan.WorldOptions.Count == 0)
        {
            return candidates;
        }

        if (plan.HqQuantityNeeded > 0)
        {
            return GenerateQualityAwarePurchaseCandidates(plan, currentRoute, budget);
        }

        var coverageCandidates = MarketCoverageSelection.GetCandidates(plan.CoverageSet)
            .Where(coverage => coverage.Kind == MarketCoverageKind.SupportedListings)
            .Where(coverage => coverage.QualityPolicy == MarketCoverageQualityPolicy.NqOrHq)
            .Where(coverage => coverage.QuantityCovered >= plan.QuantityNeeded)
            .ToList();
        if (coverageCandidates.Count > 0)
        {
            return coverageCandidates
                .Select(coverage => CreateCoveragePurchaseCandidate(plan, coverage))
                .OrderBy(candidate => candidate.GilCost)
                .ThenBy(candidate => candidate.Worlds.Count)
                .ThenBy(candidate => candidate.Coverage!.CandidateId, StringComparer.Ordinal)
                .ToList();
        }

        var singleWorlds = plan.WorldOptions
            .Where(w => w.TotalQuantityPurchased >= plan.QuantityNeeded)
            .OrderBy(w => GetProcurementPriorityScore(w, w.TotalCost))
            .ThenBy(w => w.TotalCost)
            .ThenBy(w => w.DataCenter)
            .ThenBy(w => w.WorldName)
            .ToList();

        foreach (var world in singleWorlds)
        {
            candidates.Add(new MarketPurchaseCandidate(
                world.TotalCost,
                [new MarketWorldKey(world.DataCenter, world.WorldName)])
            {
                ItemId = plan.ItemId,
                ItemName = plan.Name,
                QuantityNeeded = plan.QuantityNeeded,
                QuantityFulfilled = plan.QuantityNeeded,
                MarketEvidencePenalty = MarketWorldRecommendationScoring.CalculateEvidencePenalty(world.TotalCost, world),
                HasTrustworthyEvidence = MarketEvidenceFreshness.IsRouteEligible(world.MarketDataQualityBucket),
                SingleWorld = world
            });
        }

        var splitWorlds = plan.WorldOptions
            .Where(w => w.TotalQuantityPurchased > 0)
            .Select(w => new
            {
                World = w,
                SplitScore = CalculateValueScore(w, plan.QuantityNeeded, splitEnabled: true)
            })
            .Where(w => w.SplitScore < decimal.MaxValue)
            .OrderBy(w => w.SplitScore)
            .ThenBy(w => GetProcurementPriorityScore(w.World, w.World.TotalCost))
            .ThenBy(w => w.World.TotalCost)
            .ThenBy(w => w.World.DataCenter)
            .ThenBy(w => w.World.WorldName)
            .Select(w => w.World)
            .ToList();

        foreach (var split in GenerateSplitPurchaseAlternatives(
                     plan.QuantityNeeded,
                     splitWorlds,
                     currentRoute,
                     budget))
        {
            var quantityFulfilled = split.Sum(s => s.QuantityToBuy);
            var evidencePenalty = CalculateSplitEvidencePenalty(split, plan.WorldOptions);
            candidates.Add(new MarketPurchaseCandidate(
                split.Sum(s => s.TotalCost),
                split.Select(s => new MarketWorldKey(s.DataCenter, s.WorldName)))
            {
                ItemId = plan.ItemId,
                ItemName = plan.Name,
                QuantityNeeded = plan.QuantityNeeded,
                QuantityFulfilled = quantityFulfilled,
                MarketEvidencePenalty = evidencePenalty,
                HasTrustworthyEvidence = HasTrustworthyEvidence(plan, split.Select(world =>
                    new MarketWorldKey(world.DataCenter, world.WorldName))),
                Split = split
            });
        }

        return candidates;
    }

    private static List<MarketPurchaseCandidate> GenerateQualityAwarePurchaseCandidates(
        DetailedShoppingPlan plan,
        MarketRouteState? currentRoute,
        MarketRouteCandidateWorkBudget? budget)
    {
        var worlds = plan.WorldOptions
            .Where(world => !string.Equals(
                world.WorldName,
                MarketShoppingConstants.VendorWorldName,
                StringComparison.OrdinalIgnoreCase))
            .ToList();
        var worldSets = new Dictionary<string, IReadOnlyList<WorldShoppingSummary>>(StringComparer.Ordinal);

        bool AddWorldSet(IEnumerable<WorldShoppingSummary> source)
        {
            budget?.CheckCancellation();
            var set = source
                .DistinctBy(world => new MarketWorldKey(world.DataCenter, world.WorldName))
                .OrderBy(world => world.DataCenter, StringComparer.OrdinalIgnoreCase)
                .ThenBy(world => world.WorldName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (set.Count == 0)
            {
                return true;
            }

            var key = string.Join('|', set.Select(world => $"{world.DataCenter}:{world.WorldName}"));
            if (worldSets.ContainsKey(key))
            {
                return true;
            }
            if (budget is not null && !budget.TryConsumeWorldSet())
            {
                return false;
            }
            worldSets.Add(key, set);
            return true;
        }

        foreach (var world in worlds)
        {
            if (!AddWorldSet([world]))
            {
                break;
            }
        }
        AddWorldSet(worlds);

        if (currentRoute?.Worlds.Count > 0)
        {
            var routeWorlds = worlds.Where(world => currentRoute.ContainsWorld(
                new MarketWorldKey(world.DataCenter, world.WorldName))).ToList();
            AddWorldSet(routeWorlds);
            foreach (var world in worlds)
            {
                if (!AddWorldSet(routeWorlds.Append(world)))
                {
                    break;
                }
            }
        }

        for (var left = 0; left < worlds.Count; left++)
        {
            var exhausted = false;
            for (var right = left + 1; right < worlds.Count; right++)
            {
                if (!AddWorldSet([worlds[left], worlds[right]]))
                {
                    exhausted = true;
                    break;
                }
            }
            if (exhausted)
            {
                break;
            }
        }

        var candidates = new List<MarketPurchaseCandidate>();
        foreach (var worldSet in worldSets.Values)
        {
            budget?.CheckCancellation();
            var available = worldSet
                .SelectMany(world => world.Listings
                    .Where(listing => listing.Quantity > 0 && listing.PricePerUnit > 0)
                    .Select(listing => new QualityAwareListingCoverageOptimizer.Listing(world, listing)))
                .OrderBy(entry => entry.MarketListing.PricePerUnit)
                .ThenBy(entry => entry.World.DataCenter, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.World.WorldName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var selection = QualityAwareListingCoverageOptimizer.SelectBounded(
                available,
                plan.QuantityNeeded,
                plan.HqQuantityNeeded,
                budget);
            var selected = selection.Listings;
            if (selected == null)
            {
                continue;
            }

            var coverage = CreateQualityAwareCoverage(plan, selected);
            var keys = coverage.Worlds
                .Select(world => new MarketWorldKey(world.DataCenter, world.WorldName))
                .ToList();
            var evidencePenalty = 0L;
            foreach (var coverageWorld in coverage.Worlds)
            {
                var world = worldSet.First(option =>
                    string.Equals(option.DataCenter, coverageWorld.DataCenter, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(option.WorldName, coverageWorld.WorldName, StringComparison.OrdinalIgnoreCase));
                evidencePenalty = SaturatingAddCost(
                    evidencePenalty,
                    MarketWorldRecommendationScoring.CalculateEvidencePenalty(
                        ToLongSaturating(coverageWorld.CashOutCost),
                        world));
            }

            candidates.Add(new MarketPurchaseCandidate(ToLongSaturating(coverage.CashOutCost), keys)
            {
                ItemId = plan.ItemId,
                ItemName = plan.Name,
                QuantityNeeded = plan.QuantityNeeded,
                QuantityFulfilled = plan.QuantityNeeded,
                MarketEvidencePenalty = evidencePenalty,
                HasTrustworthyEvidence = HasTrustworthyEvidence(plan, keys),
                Coverage = coverage
            });
        }

        return candidates
            .GroupBy(candidate => string.Join('|', candidate.Worlds
                .OrderBy(world => world.DataCenter)
                .ThenBy(world => world.WorldName)
                .Select(world => $"{world.DataCenter}:{world.WorldName}")), StringComparer.Ordinal)
            .Select(group => group.OrderBy(candidate => candidate.GilCost).First())
            .OrderBy(candidate => candidate.GilCost)
            .ThenBy(candidate => candidate.Worlds.Count)
            .ToList();
    }

    private static MarketCoverageOption CreateQualityAwareCoverage(
        DetailedShoppingPlan plan,
        IReadOnlyList<QualityAwareListingCoverageOptimizer.Listing> selected)
    {
        var remaining = plan.QuantityNeeded;
        var listings = selected
            .OrderByDescending(entry => entry.MarketListing.IsHq)
            .ThenBy(entry => entry.MarketListing.PricePerUnit)
            .Select(entry =>
            {
                var used = Math.Min(remaining, entry.MarketListing.Quantity);
                remaining -= used;
                return new MarketCoverageListing(
                    entry.World.DataCenter,
                    entry.World.WorldName,
                    entry.MarketListing.Quantity,
                    used,
                    entry.MarketListing.Quantity,
                    entry.MarketListing.PricePerUnit,
                    entry.MarketListing.IsHq);
            })
            .ToList();
        var worlds = listings
            .GroupBy(listing => new MarketWorldKey(listing.DataCenter, listing.WorldName))
            .Select(group => new MarketCoverageWorld(
                group.Key.DataCenter,
                group.Key.WorldName,
                group.Sum(listing => listing.QuantityUsed),
                group.Sum(listing => listing.QuantityPurchased),
                group.Sum(listing => listing.QuantityUsed * listing.PricePerUnit),
                group.Sum(listing => listing.QuantityPurchased * listing.PricePerUnit)))
            .OrderBy(world => world.DataCenter)
            .ThenBy(world => world.WorldName)
            .ToList();
        var quantityToPurchase = worlds.Sum(world => world.QuantityToPurchase);
        var exactNeededCost = worlds.Sum(world => world.ExactNeededCost);
        var cashOutCost = worlds.Sum(world => world.CashOutCost);
        var tier = worlds.Count switch
        {
            1 => MarketCoverageTier.SingleWorld,
            2 => MarketCoverageTier.CompactSplit,
            <= 5 => MarketCoverageTier.WideSplit,
            _ => MarketCoverageTier.CheapestObserved
        };
        var candidateId = $"{plan.ItemId}-{plan.QuantityNeeded}-{plan.HqQuantityNeeded}-quality-{string.Join('_', worlds.Select(world => $"{world.DataCenter}.{world.WorldName}"))}";

        return new MarketCoverageOption(
            candidateId,
            tier,
            MarketCoverageKind.SupportedListings,
            plan.HqQuantityNeeded >= plan.QuantityNeeded
                ? MarketCoverageQualityPolicy.HqOnly
                : MarketCoverageQualityPolicy.NqOrHq,
            plan.QuantityNeeded,
            quantityToPurchase,
            Math.Max(0, quantityToPurchase - plan.QuantityNeeded),
            exactNeededCost,
            cashOutCost,
            plan.QuantityNeeded > 0 ? exactNeededCost / plan.QuantityNeeded : 0,
            MarketCoveragePriceBand.Unknown,
            worlds,
            listings,
            new MarketCoverageFriction(
                worlds.Count,
                worlds.Select(world => world.DataCenter).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                worlds.Count == 0 ? 0 : worlds.Min(world => world.QuantityCovered),
                worlds.Count == 0 ? 0 : worlds.Max(world => world.QuantityCovered),
                Math.Max(0, quantityToPurchase - plan.QuantityNeeded)),
            MarketCoverageSavings.None,
            IsDefaultEligible: true,
            DegradedReason: null);
    }

    private static MarketPurchaseCandidate CreateCoveragePurchaseCandidate(
        DetailedShoppingPlan plan,
        MarketCoverageOption coverage)
    {
        var gilCost = ToLongSaturating(coverage.CashOutCost);
        var evidencePenalty = 0L;
        foreach (var coverageWorld in coverage.Worlds)
        {
            var world = plan.WorldOptions.FirstOrDefault(option =>
                string.Equals(option.DataCenter, coverageWorld.DataCenter, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(option.WorldName, coverageWorld.WorldName, StringComparison.OrdinalIgnoreCase));
            if (world == null)
            {
                continue;
            }

            evidencePenalty = SaturatingAddCost(
                evidencePenalty,
                MarketWorldRecommendationScoring.CalculateEvidencePenalty(
                    ToLongSaturating(coverageWorld.CashOutCost),
                    world));
        }

        return new MarketPurchaseCandidate(
            gilCost,
            coverage.Worlds.Select(world => new MarketWorldKey(world.DataCenter, world.WorldName)))
        {
            ItemId = plan.ItemId,
            ItemName = plan.Name,
            QuantityNeeded = plan.QuantityNeeded,
            QuantityFulfilled = coverage.QuantityCovered,
            MarketEvidencePenalty = evidencePenalty,
            HasTrustworthyEvidence = HasTrustworthyEvidence(
                plan,
                coverage.Worlds.Select(world => new MarketWorldKey(world.DataCenter, world.WorldName))),
            Coverage = coverage
        };
    }

    private static bool HasTrustworthyEvidence(
        DetailedShoppingPlan plan,
        IEnumerable<MarketWorldKey> worlds)
    {
        foreach (var worldKey in worlds)
        {
            var world = plan.WorldOptions.FirstOrDefault(option =>
                string.Equals(option.DataCenter, worldKey.DataCenter, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(option.WorldName, worldKey.WorldName, StringComparison.OrdinalIgnoreCase));
            if (world == null || !MarketEvidenceFreshness.IsRouteEligible(world.MarketDataQualityBucket))
            {
                return false;
            }
        }

        return true;
    }

    private static long ToLongSaturating(decimal value)
    {
        if (value <= 0)
        {
            return 0;
        }

        return value >= long.MaxValue
            ? long.MaxValue
            : (long)Math.Ceiling(value);
    }

    private static long SaturatingAddCost(long left, long right)
    {
        return left > 0 && right > long.MaxValue - left
            ? long.MaxValue
            : left + right;
    }

    private static List<List<SplitWorldPurchase>> GenerateSplitPurchaseAlternatives(
        int quantityNeeded,
        IReadOnlyList<WorldShoppingSummary> rankedWorlds,
        MarketRouteState? currentRoute,
        MarketRouteCandidateWorkBudget? budget)
    {
        var alternatives = new Dictionary<string, List<SplitWorldPurchase>>(StringComparer.OrdinalIgnoreCase);
        var routeKeyOrder = new List<string>();

        AddSplitAlternative(quantityNeeded, rankedWorlds, alternatives, routeKeyOrder, budget);
        AddSplitAlternative(
            quantityNeeded,
            rankedWorlds
                .OrderBy(world => GetProcurementPriorityScore(world, world.TotalCost))
                .ThenBy(world => world.DataCenter)
                .ThenBy(world => world.WorldName),
            alternatives,
            routeKeyOrder,
            budget);

        foreach (var seedWorld in GetSplitSeedWorlds(rankedWorlds, currentRoute))
        {
            if (budget is not null && !budget.TryConsumeSplitSeed())
            {
                break;
            }
            var seededWorlds = rankedWorlds
                .Where(world => !IsSameWorld(world, seedWorld))
                .Prepend(seedWorld)
                .ToList();

            AddSplitAlternative(quantityNeeded, seededWorlds, alternatives, routeKeyOrder, budget);
        }

        return routeKeyOrder
            .Select(routeKey => alternatives[routeKey])
            .ToList();
    }

    private static IEnumerable<WorldShoppingSummary> GetSplitSeedWorlds(
        IReadOnlyList<WorldShoppingSummary> rankedWorlds,
        MarketRouteState? currentRoute)
    {
        if (currentRoute == null)
        {
            return rankedWorlds;
        }

        var routeReuseWorlds = rankedWorlds
            .Where(world => currentRoute.ContainsWorld(new MarketWorldKey(world.DataCenter, world.WorldName)));

        var localWorlds = rankedWorlds
            .Where(world => !currentRoute.ContainsWorld(new MarketWorldKey(world.DataCenter, world.WorldName)));

        return routeReuseWorlds.Concat(localWorlds);
    }

    private static void AddSplitAlternative(
        int quantityNeeded,
        IEnumerable<WorldShoppingSummary> worlds,
        Dictionary<string, List<SplitWorldPurchase>> alternatives,
        List<string> routeKeyOrder,
        MarketRouteCandidateWorkBudget? budget)
    {
        var split = BuildSplitPurchase(quantityNeeded, worlds, budget);
        if (split.Count <= 1 || split.Sum(s => s.QuantityToBuy) < quantityNeeded)
        {
            return;
        }

        var routeKey = GetSplitRouteKey(split);
        if (alternatives.TryGetValue(routeKey, out var existing))
        {
            var existingCost = existing.Sum(s => s.TotalCost);
            var splitCost = split.Sum(s => s.TotalCost);
            if (splitCost < existingCost)
            {
                alternatives[routeKey] = split;
            }

            return;
        }

        alternatives[routeKey] = split;
        routeKeyOrder.Add(routeKey);
    }

    private static string GetSplitRouteKey(IEnumerable<SplitWorldPurchase> split)
    {
        return string.Join(
            "|",
            split
                .Select(s => new MarketWorldKey(s.DataCenter, s.WorldName))
                .Distinct()
                .OrderBy(world => world.DataCenter)
                .ThenBy(world => world.WorldName)
                .Select(world => $"{world.DataCenter}:{world.WorldName}"));
    }

    private static bool IsSameWorld(WorldShoppingSummary left, WorldShoppingSummary right)
    {
        return string.Equals(left.DataCenter, right.DataCenter, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.WorldName, right.WorldName, StringComparison.OrdinalIgnoreCase);
    }

    internal static List<SplitWorldPurchase> BuildSplitPurchase(
        int quantityNeeded,
        IEnumerable<WorldShoppingSummary> worlds) =>
        BuildSplitPurchase(quantityNeeded, worlds, budget: null);

    private static List<SplitWorldPurchase> BuildSplitPurchase(
        int quantityNeeded,
        IEnumerable<WorldShoppingSummary> worlds,
        MarketRouteCandidateWorkBudget? budget)
    {
        var split = new List<SplitWorldPurchase>();
        var remaining = quantityNeeded;

        foreach (var world in worlds)
        {
            budget?.CheckCancellation();
            if (remaining <= 0)
            {
                break;
            }

            var toAllocate = Math.Min(remaining, world.TotalQuantityPurchased);
            if (toAllocate <= 0)
            {
                continue;
            }

            var cost = 0L;
            var remainingFromWorld = toAllocate;
            var selectedListings = new List<ShoppingListingEntry>();

            foreach (var listing in world.Listings.Where(l => !l.IsAdditionalOption).OrderBy(l => l.PricePerUnit))
            {
                budget?.CheckCancellation();
                if (remainingFromWorld <= 0)
                {
                    break;
                }

                var fromThis = Math.Min(remainingFromWorld, listing.Quantity);
                if (fromThis <= 0)
                {
                    continue;
                }

                cost += listing.Quantity * listing.PricePerUnit;
                remainingFromWorld -= fromThis;

                selectedListings.Add(new ShoppingListingEntry
                {
                    Quantity = listing.Quantity,
                    PricePerUnit = listing.PricePerUnit,
                    RetainerName = listing.RetainerName,
                    IsUnderAverage = listing.IsUnderAverage,
                    IsHq = listing.IsHq,
                    IsAdditionalOption = false,
                    NeededFromStack = fromThis,
                    ExcessQuantity = Math.Max(0, listing.Quantity - fromThis)
                });
            }

            var excessAvailable = selectedListings.Sum(l => l.ExcessQuantity);
            var selectedListingQuantity = selectedListings.Sum(l => l.Quantity);
            var averageListingUnitPrice = selectedListingQuantity > 0
                ? cost / (decimal)selectedListingQuantity
                : world.AveragePricePerUnit;
            var travelContext = split.Count == 0
                ? TravelContextConstants.Primary
                : TravelContextConstants.Supplemental;

            split.Add(new SplitWorldPurchase
            {
                DataCenter = world.DataCenter,
                WorldName = world.WorldName,
                QuantityToBuy = toAllocate,
                PricePerUnit = averageListingUnitPrice,
                EffectivePricePerNeededUnit = toAllocate > 0 ? cost / (decimal)toAllocate : averageListingUnitPrice,
                IsPartial = toAllocate < quantityNeeded,
                TotalCost = cost,
                TravelContext = travelContext,
                ExcessAvailable = excessAvailable,
                Listings = selectedListings
            });

            remaining -= toAllocate;
        }

        return split;
    }

    private static decimal GetProcurementPriorityScore(WorldShoppingSummary world, long gilCost)
    {
        return world.ProcurementPriorityScore > 0 && world.ProcurementPriorityScore < decimal.MaxValue
            ? world.ProcurementPriorityScore
            : MarketWorldRecommendationScoring.CalculatePriorityScore(gilCost, world);
    }

    private static long CalculateSplitEvidencePenalty(
        IEnumerable<SplitWorldPurchase> split,
        IEnumerable<WorldShoppingSummary> worldOptions)
    {
        var worldLookup = worldOptions.ToDictionary(
            world => new MarketWorldKey(world.DataCenter, world.WorldName),
            world => world);
        long totalPenalty = 0;

        foreach (var splitWorld in split)
        {
            var key = new MarketWorldKey(splitWorld.DataCenter, splitWorld.WorldName);
            if (!worldLookup.TryGetValue(key, out var world))
            {
                continue;
            }

            var penalty = MarketWorldRecommendationScoring.CalculateEvidencePenalty(splitWorld.TotalCost, world);
            totalPenalty = SafeAdd(totalPenalty, penalty);
        }

        return totalPenalty;
    }

    private static long SafeAdd(long left, long right)
    {
        if (left > 0 && right > long.MaxValue - left)
        {
            return long.MaxValue;
        }

        return left + right;
    }

    /// <summary>
    /// Calculates a multi-world split purchase plan for items that can't be fulfilled on a single world.
    /// Uses ValueScore as the single metric for world selection.
    /// </summary>
    public void CalculateSplitPurchase(
        DetailedShoppingPlan plan,
        MarketAnalysisConfig config)
    {
        plan.RecommendedSplit = null;

        // Calculate ValueScores in split mode
        foreach (var world in plan.WorldOptions)
        {
            world.ValueScore = CalculateValueScore(world, plan.QuantityNeeded, splitEnabled: true);
        }

        // Get viable worlds sorted by ValueScore
        var viableWorlds = plan.WorldOptions
            .Where(w => w.ValueScore < decimal.MaxValue && w.TotalQuantityPurchased > 0)
            .OrderBy(w => w.ValueScore)
            .ToList();

        if (viableWorlds.Count == 0)
        {
            return;
        }

        var split = BuildSplitPurchase(plan.QuantityNeeded, viableWorlds);

        if (split.Count == 0)
        {
            return;
        }

        if (split.Sum(s => s.QuantityToBuy) < plan.QuantityNeeded)
        {
            return;
        }

        var splitCost = split.Sum(s => s.TotalCost);
        var singleWorldCost = plan.RecommendedWorld?.TotalCost ?? long.MaxValue;

        // Single-world contingency: prefer single if within 5%
        if (plan.RecommendedWorld != null &&
            plan.RecommendedWorld.HasSufficientStock &&
            singleWorldCost <= splitCost * 1.05m)
        {
            // Keep single-world recommendation
            return;
        }

        // Use split
        plan.RecommendedSplit = split;
    }

    /// <summary>
    /// Projects items already selected as VendorBuy into fixed procurement stops.
    ///
    /// This keeps existing market world options for comparison, but forces the recommended purchase source
    /// to a synthetic "Vendor" world using the selected vendor (or cheapest gil vendor fallback).
    /// </summary>
    public void ApplySelectedVendorPurchases(CraftingPlan? plan, List<DetailedShoppingPlan> plans)
    {
        if (plan == null || plans == null || plans.Count == 0)
        {
            return;
        }

        foreach (var shoppingPlan in plans)
        {
            var vendorNode = FindVendorBuyNodeByItemId(plan.RootItems, shoppingPlan.ItemId);
            if (vendorNode == null)
            {
                continue;
            }

            var gilVendors = vendorNode.VendorOptions.Where(v => v.IsGilVendor).ToList();
            if (gilVendors.Count == 0)
            {
                continue;
            }

            var selectedVendor = vendorNode.SelectedVendor;
            if (selectedVendor == null || !selectedVendor.IsGilVendor)
            {
                selectedVendor = gilVendors.OrderBy(v => v.Price).First();
            }

            var unitPrice = selectedVendor.Price;
            if (unitPrice <= 0)
            {
                continue;
            }

            var vendorWorldSummary = new WorldShoppingSummary
            {
                WorldName = MarketShoppingConstants.VendorWorldName,
                WorldId = 0,
                TotalCost = (long)(unitPrice * shoppingPlan.QuantityNeeded),
                AveragePricePerUnit = unitPrice,
                ListingsUsed = 1,
                TotalQuantityPurchased = shoppingPlan.QuantityNeeded,
                HasSufficientStock = true,
                IsHomeWorld = false,
                IsTravelProhibited = false,
                IsBlacklisted = false,
                Classification = WorldClassification.Standard,
                VendorName = selectedVendor.DisplayName,
                Listings = new List<ShoppingListingEntry>
                {
                    new()
                    {
                        Quantity = shoppingPlan.QuantityNeeded,
                        PricePerUnit = (long)unitPrice,
                        RetainerName = MarketShoppingConstants.VendorWorldName,
                        IsUnderAverage = true,
                        IsHq = false,
                        NeededFromStack = shoppingPlan.QuantityNeeded,
                        ExcessQuantity = 0
                    }
                }
            };

            shoppingPlan.RecommendedWorld = vendorWorldSummary;
            shoppingPlan.RecommendedSplit = null;
            shoppingPlan.Vendors = gilVendors;
            shoppingPlan.WorldOptions.RemoveAll(world => string.Equals(
                world.WorldName,
                MarketShoppingConstants.VendorWorldName,
                StringComparison.OrdinalIgnoreCase));
        }
    }

    private static PlanNode? FindVendorBuyNodeByItemId(IEnumerable<PlanNode> nodes, int itemId)
    {
        foreach (var node in nodes)
        {
            if (node.ItemId == itemId && node.Source == AcquisitionSource.VendorBuy)
            {
                return node;
            }

            if (node.Children.Count == 0)
            {
                continue;
            }

            var childMatch = FindVendorBuyNodeByItemId(node.Children, itemId);
            if (childMatch != null)
            {
                return childMatch;
            }
        }

        return null;
    }

    // ========================================================================
    // Item Categorization
    // ========================================================================

    /// <summary>
    /// Categorizes materials by their price source (Vendor, Market, or Untradeable).
    /// 
    /// VENDOR ITEM HANDLING:
    /// Vendor items are identified by PriceInfo.Source == PriceSource.Vendor.
    /// These items are excluded from market analysis and shopping plan calculations
    /// because they have fixed prices and unlimited stock from NPC vendors.
    /// 
    /// SEPARATION LOGIC:
    /// 1. Vendor items: PriceSource.Vendor → VendorItems list
    ///    - No market lookup needed
    ///    - Price is fixed (from Garland data)
    ///    - Stock is unlimited
    ///    - Displayed separately in UI with vendor location
    /// 
    /// 2. Untradeable items: PriceSource.Untradeable → UntradeableItems list
    ///    - Cannot be bought on market
    ///    - Must be gathered or crafted
    ///    - Shown in UI with "Untradeable" label
    /// 
    /// 3. Market items: PriceSource.Market or no price info → MarketItems list
    ///    - Requires market analysis
    ///    - Price varies by world
    ///    - Stock limited by listings
    ///    - Full shopping plan calculation needed
    /// 
    /// WHY SEPARATE VENDOR ITEMS:
    /// - Avoids unnecessary API calls to Universalis for fixed-price items
    /// - Allows special UI treatment (vendor location display, gold background)
    /// - Ensures accurate cost calculations (vendor always cheapest)
    /// - Simplifies procurement planning (always buy from vendor)
    /// </summary>
    public CategorizedMaterials CategorizeMaterials(List<MaterialAggregate> materials, Dictionary<int, PriceInfo> prices)
    {
        return CategorizeMaterials(materials, prices, null);
    }

    /// <summary>
    /// Categorizes materials by their price source (Vendor, Market, or Untradeable).
    /// Also checks the plan tree for items explicitly marked as VendorBuy by the user.
    /// </summary>
    /// <param name="materials">The materials to categorize.</param>
    /// <param name="prices">Price information dictionary.</param>
    /// <param name="plan">Optional crafting plan to check for user-selected VendorBuy sources.</param>
    public CategorizedMaterials CategorizeMaterials(
        List<MaterialAggregate> materials,
        Dictionary<int, PriceInfo> prices,
        CraftingPlan? plan)
    {
        var result = new CategorizedMaterials();

        // Collect items marked as VendorBuy in the plan tree
        var vendorBuyItemIds = new HashSet<int>();
        if (plan != null)
        {
            CollectVendorBuyItemIds(plan.RootItems, vendorBuyItemIds);
        }

        foreach (var material in materials)
        {
            // Check if user explicitly selected VendorBuy in recipe tree
            if (vendorBuyItemIds.Contains(material.ItemId))
            {
                result.VendorItems.Add(material);
                continue;
            }

            if (prices.TryGetValue(material.ItemId, out var priceInfo))
            {
                switch (priceInfo.Source)
                {
                    case PriceSource.Vendor:
                        result.VendorItems.Add(material);
                        break;
                    case PriceSource.Untradeable:
                        result.UntradeableItems.Add(material);
                        break;
                    case PriceSource.Market:
                    default:
                        result.MarketItems.Add(material);
                        break;
                }
            }
            else
            {
                // No price info - assume market
                result.MarketItems.Add(material);
            }
        }

        return result;
    }

    /// <summary>
    /// Recursively collects item IDs that are marked as VendorBuy in the plan tree.
    /// </summary>
    private static void CollectVendorBuyItemIds(List<PlanNode> nodes, HashSet<int> itemIds)
    {
        foreach (var node in nodes)
        {
            if (node.Source == AcquisitionSource.VendorBuy)
            {
                itemIds.Add(node.ItemId);
            }

            if (node.Children.Count > 0)
            {
                CollectVendorBuyItemIds(node.Children, itemIds);
            }
        }
    }

    private static List<ProcurementRouteSearchState> PruneRouteBeam(
        IEnumerable<ProcurementRouteSearchState> states,
        MarketAnalysisConfig config,
        out bool wasTruncated)
    {
        var distinctStates = new List<ProcurementRouteSearchState>();
        foreach (var group in states.GroupBy(state => state.RouteShapeKey, StringComparer.Ordinal))
        {
            var bestEvidencePenalty = long.MaxValue;
            foreach (var state in group
                .OrderBy(state => state.TotalGilCost)
                .ThenBy(state => state.TotalEvidencePenalty)
                .ThenBy(state => state.TieBreakKey, StringComparer.Ordinal))
            {
                if (state.TotalEvidencePenalty >= bestEvidencePenalty)
                {
                    continue;
                }

                distinctStates.Add(state);
                bestEvidencePenalty = state.TotalEvidencePenalty;
            }
        }

        wasTruncated = distinctStates.Count > MaxProcurementRouteBeamWidth;
        if (!wasTruncated)
        {
            return OrderRouteStates(distinctStates, config).ToList();
        }

        var preferred = OrderRouteStates(distinctStates, config)
            .Take(MaxProcurementRouteBeamWidth - 16);
        var cheapest = distinctStates
            .OrderBy(state => state.TotalGilCost)
            .ThenBy(state => state.TotalEvidencePenalty)
            .ThenBy(state => state.TieBreakKey, StringComparer.Ordinal)
            .Take(16);

        return preferred
            .Concat(cheapest)
            .DistinctBy(state => state.RouteShapeKey, StringComparer.Ordinal)
            .Take(MaxProcurementRouteBeamWidth)
            .ToList();
    }

    private static List<ProcurementRouteSearchState> ReduceToParetoFrontier(
        IEnumerable<ProcurementRouteSearchState> states,
        MarketAnalysisConfig config)
    {
        var materialized = states.ToList();
        return materialized
            .Where(candidate => !materialized.Any(other =>
                !ReferenceEquals(candidate, other) &&
                Dominates(other, candidate, config)))
            .OrderBy(state => state.TieBreakKey, StringComparer.Ordinal)
            .ToList();
    }

    private static bool Dominates(
        ProcurementRouteSearchState left,
        ProcurementRouteSearchState right,
        MarketAnalysisConfig config)
    {
        var leftTransfers = left.GetDataCenterTransfers(config);
        var rightTransfers = right.GetDataCenterTransfers(config);
        var noWorse = left.TotalGilCost <= right.TotalGilCost &&
            left.TotalEvidencePenalty <= right.TotalEvidencePenalty &&
            leftTransfers <= rightTransfers &&
            left.WorldStops <= right.WorldStops;
        var strictlyBetter = left.TotalGilCost < right.TotalGilCost ||
            left.TotalEvidencePenalty < right.TotalEvidencePenalty ||
            leftTransfers < rightTransfers ||
            left.WorldStops < right.WorldStops;
        return noWorse && strictlyBetter;
    }

    private static IOrderedEnumerable<ProcurementRouteSearchState> OrderRouteStates(
        IEnumerable<ProcurementRouteSearchState> states,
        MarketAnalysisConfig config,
        int? travelToleranceOverride = null,
        long? absoluteMaximumGilCost = null)
    {
        var materialized = states.ToList();
        var cheapestGilCost = materialized.Count == 0
            ? 0
            : materialized.Min(state => state.TotalGilCost);
        var travelTolerance = travelToleranceOverride ?? config.TravelTolerance;
        var maximumPremiumRate = MarketRouteScoring.GetMaximumPremiumRate(travelTolerance);
        var eligible = materialized
            .OrderBy(state => absoluteMaximumGilCost.HasValue
                ? state.TotalGilCost > absoluteMaximumGilCost.Value
                : !MarketRouteScoring.IsWithinPremium(
                    state.TotalGilCost,
                    cheapestGilCost,
                    maximumPremiumRate));
        var travelOrdered = config.TravelPriority == MarketTravelPriority.WorldVisitsFirst
            ? eligible
                .ThenBy(state => state.WorldStops)
                .ThenBy(state => state.GetDataCenterTransfers(config))
            : eligible
                .ThenBy(state => state.GetDataCenterTransfers(config))
                .ThenBy(state => state.WorldStops);

        return travelOrdered
            .ThenBy(state => state.TotalEvidencePenalty)
            .ThenBy(state => state.TotalGilCost)
            .ThenBy(state => state.TieBreakKey, StringComparer.Ordinal);
    }

    private static IReadOnlyList<MarketRouteFrontierOption> BuildRepresentativeRoutes(
        IReadOnlyList<ProcurementRouteSearchState> frontier,
        MarketAnalysisConfig config)
    {
        var representatives = new List<MarketRouteFrontierOption>();
        ProcurementRouteSearchState? previous = null;
        for (var tolerance = 0; tolerance <= 11; tolerance++)
        {
            var state = OrderRouteStates(frontier, config, tolerance).FirstOrDefault();
            if (state == null)
            {
                continue;
            }

            if (previous != null &&
                string.Equals(previous.TieBreakKey, state.TieBreakKey, StringComparison.Ordinal) &&
                representatives.Count > 0)
            {
                representatives[^1] = representatives[^1] with { MaximumTolerance = tolerance };
                continue;
            }

            representatives.Add(new MarketRouteFrontierOption(
                tolerance,
                tolerance,
                state.TotalGilCost,
                state.WorldStops,
                state.GetDataCenterTransfers(config)));
            previous = state;
        }

        return representatives;
    }

    private static bool IsFixedVendorPlan(DetailedShoppingPlan plan)
    {
        return string.Equals(
            plan.RecommendedWorld?.WorldName,
            MarketShoppingConstants.VendorWorldName,
            StringComparison.OrdinalIgnoreCase);
    }

    private static long SaturatingAdd(long left, long right)
    {
        if (left > 0 && right > long.MaxValue - left)
        {
            return long.MaxValue;
        }

        return left + right;
    }

    private static DetailedShoppingPlan ClonePlanForRouteOptimization(DetailedShoppingPlan plan)
    {
        return new DetailedShoppingPlan
        {
            ItemId = plan.ItemId,
            Name = plan.Name,
            IconId = plan.IconId,
            QuantityNeeded = plan.QuantityNeeded,
            HqQuantityNeeded = plan.HqQuantityNeeded,
            DCAveragePrice = plan.DCAveragePrice,
            WorldOptions = plan.WorldOptions.ToList(),
            RecommendedWorld = plan.RecommendedWorld,
            CoverageSet = plan.CoverageSet,
            RecommendedSplit = plan.RecommendedSplit?.ToList(),
            Error = plan.Error,
            MarketDataWarning = plan.MarketDataWarning,
            HQAveragePrice = plan.HQAveragePrice,
            Vendors = plan.Vendors.ToList()
        };
    }

    private sealed class ProcurementRouteSearchState
    {
        public ProcurementRouteSearchState(long fixedGilCost)
            : this(new MarketRouteState(), fixedGilCost, 0, [], string.Empty, string.Empty)
        {
        }

        private ProcurementRouteSearchState(
            MarketRouteState route,
            long totalGilCost,
            long totalEvidencePenalty,
            IReadOnlyList<ProcurementRouteChoice> choices,
            string tieBreakKey,
            string missingChoiceKey)
        {
            Route = route;
            TotalGilCost = totalGilCost;
            TotalEvidencePenalty = totalEvidencePenalty;
            Choices = choices;
            TieBreakKey = tieBreakKey;
            MissingChoiceKey = missingChoiceKey;
            RouteShapeKey = BuildRouteShapeKey(route, missingChoiceKey);
        }

        public MarketRouteState Route { get; }
        public long TotalGilCost { get; }
        public long TotalEvidencePenalty { get; }
        public int WorldStops => Route.Worlds.Count;
        public IReadOnlyList<ProcurementRouteChoice> Choices { get; }
        public string TieBreakKey { get; }
        public string RouteShapeKey { get; }
        public string MissingChoiceKey { get; }

        public int GetDataCenterTransfers(MarketAnalysisConfig config)
        {
            var dataCenterCount = Route.DataCenters.Count;
            if (dataCenterCount == 0)
            {
                return 0;
            }

            if (!config.StartFromHomeDataCenter || string.IsNullOrWhiteSpace(config.HomeDataCenter))
            {
                return Math.Max(0, dataCenterCount - 1);
            }

            return Route.ContainsDataCenter(config.HomeDataCenter)
                ? Math.Max(0, dataCenterCount - 1)
                : dataCenterCount;
        }

        public ProcurementRouteSearchState WithCandidate(int planIndex, MarketPurchaseCandidate candidate)
        {
            var choices = Choices
                .Append(new ProcurementRouteChoice(planIndex, candidate))
                .ToList();
            var route = new MarketRouteState(Route.Worlds.Concat(candidate.Worlds));

            return new ProcurementRouteSearchState(
                route,
                MarketShoppingService.SaturatingAdd(TotalGilCost, candidate.GilCost),
                MarketShoppingService.SaturatingAdd(TotalEvidencePenalty, candidate.MarketEvidencePenalty),
                choices,
                AppendChoiceKey(TieBreakKey, planIndex, candidate),
                MissingChoiceKey);
        }

        public ProcurementRouteSearchState WithoutRecommendation(int planIndex)
        {
            var choices = Choices
                .Append(new ProcurementRouteChoice(planIndex, null))
                .ToList();

            var missingChoiceKey = string.IsNullOrEmpty(MissingChoiceKey)
                ? planIndex.ToString()
                : $"{MissingChoiceKey},{planIndex}";
            return new ProcurementRouteSearchState(
                Route,
                TotalGilCost,
                TotalEvidencePenalty,
                choices,
                AppendChoiceKey(TieBreakKey, planIndex, candidate: null),
                missingChoiceKey);
        }

        private static string AppendChoiceKey(
            string existing,
            int planIndex,
            MarketPurchaseCandidate? candidate)
        {
            var choiceKey = candidate is null
                ? $"{planIndex:D8}:NO_RECOMMENDATION"
                : $"{planIndex:D8}:{candidate.GilCost:D20}:{string.Join(
                    ',',
                    candidate.Worlds
                        .OrderBy(world => world.DataCenter)
                        .ThenBy(world => world.WorldName)
                        .Select(world => $"{world.DataCenter}:{world.WorldName}"))}";
            return string.IsNullOrEmpty(existing) ? choiceKey : $"{existing}|{choiceKey}";
        }

        private static string BuildRouteShapeKey(
            MarketRouteState route,
            string missingChoiceKey)
        {
            return $"{route.CanonicalKey}|missing:{missingChoiceKey}";
        }
    }

    private sealed record ProcurementRouteChoice(int PlanIndex, MarketPurchaseCandidate? Candidate);
}

/// <summary>
/// Result of categorizing materials by their acquisition source.
/// 
/// Separates materials into three distinct categories for different handling:
/// 
/// VendorItems:
/// - Items available from NPC vendors at fixed prices
/// - Prices from Garland data (no market lookup needed)
/// - Unlimited stock assumption
/// - Displayed in "Vendor" procurement group with location info
/// - UI: Gold background, shop icon, vendor location shown
/// 
/// MarketItems:
/// - Items that must be purchased from market board
/// - Requires Universalis API lookup
/// - Price varies by world, limited stock
/// - Full shopping plan with world recommendations
/// - UI: Blue background, market board analysis shown
/// 
/// UntradeableItems:
/// - Items that cannot be traded on market
/// - Must be gathered, crafted, or obtained through other means
/// - No price information available
/// - UI: Gray background, "Untradeable" label
/// 
/// USAGE FLOW:
/// 1. RecipePlanner aggregates materials from plan tree
/// 2. PriceCheckService.GetBestPricesBulkAsync gets PriceInfo for each
/// 3. CategorizeMaterials separates by PriceInfo.Source
/// 4. VendorItems displayed separately in procurement plan
/// 5. MarketItems sent to MarketShoppingService for analysis
/// 6. UntradeableItems shown with warning
/// 
/// </summary>
public class CategorizedMaterials
{
    /// <summary>
    /// Items available from NPC vendors (fixed price, unlimited stock).
    /// These are excluded from market analysis.
    /// </summary>
    public List<MaterialAggregate> VendorItems { get; } = new();

    /// <summary>
    /// Items that must be purchased from market board (variable price, limited stock).
    /// These require full market analysis and shopping plan calculation.
    /// </summary>
    public List<MaterialAggregate> MarketItems { get; } = new();

    /// <summary>
    /// Items that cannot be traded on the market.
    /// These must be gathered, crafted, or obtained through other means.
    /// </summary>
    public List<MaterialAggregate> UntradeableItems { get; } = new();
}
