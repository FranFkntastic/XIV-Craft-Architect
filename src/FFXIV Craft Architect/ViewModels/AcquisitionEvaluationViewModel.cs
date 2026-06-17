using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.ViewModels;

public partial class AcquisitionEvaluationViewModel : ViewModelBase
{
    private readonly CraftSessionState _session;
    private readonly CoreAcquisitionEvaluationWorkflowService _workflow;
    private readonly CoreAcquisitionEvaluationLedgerCache _ledgerCache;
    private readonly CoreAcquisitionDecisionService _decisionService;
    private CoreAcquisitionEvaluationLedgerKey? _currentKey;

    private ObservableCollection<AcquisitionDecisionRowViewModel> _rows = new();
    private AcquisitionDecisionRowViewModel? _selectedRow;
    private CoreAcquisitionFilter _currentFilter = CoreAcquisitionFilter.All;
    private int _uniqueItemCount;
    private int _marketCandidateCount;
    private int _activeProcurementItemCount;
    private int _analyzedCount;
    private bool _hasPlan;
    private bool _isLoading;
    private string _statusMessage = "No active plan";

    public AcquisitionEvaluationViewModel(
        CraftSessionState session,
        CoreAcquisitionEvaluationWorkflowService workflow,
        CoreAcquisitionEvaluationLedgerCache ledgerCache,
        CoreAcquisitionDecisionService decisionService)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _workflow = workflow ?? throw new ArgumentNullException(nameof(workflow));
        _ledgerCache = ledgerCache ?? throw new ArgumentNullException(nameof(ledgerCache));
        _decisionService = decisionService ?? throw new ArgumentNullException(nameof(decisionService));
        _session.Changed += OnSessionChanged;
    }

    public ObservableCollection<AcquisitionDecisionRowViewModel> Rows
    {
        get => _rows;
        private set => SetProperty(ref _rows, value);
    }

    public AcquisitionDecisionRowViewModel? SelectedRow
    {
        get => _selectedRow;
        set => SetProperty(ref _selectedRow, value);
    }

    public IReadOnlyList<AcquisitionFilterOptionViewModel> FilterOptions { get; } =
    [
        new(CoreAcquisitionFilter.All, "All Decisions"),
        new(CoreAcquisitionFilter.Active, "Active Procurement"),
        new(CoreAcquisitionFilter.Market, "Market Candidates"),
        new(CoreAcquisitionFilter.Suppressed, "Suppressed")
    ];

    public CoreAcquisitionFilter CurrentFilter
    {
        get => _currentFilter;
        set
        {
            if (!SetProperty(ref _currentFilter, value))
            {
                return;
            }

            if (_currentKey != null && _ledgerCache.TryGet(_currentKey, _currentFilter, out var snapshot))
            {
                ApplySnapshot(snapshot);
                return;
            }

            SafeFireAndForget(RefreshAsync());
        }
    }

    public int UniqueItemCount
    {
        get => _uniqueItemCount;
        private set => SetProperty(ref _uniqueItemCount, value);
    }

    public int MarketCandidateCount
    {
        get => _marketCandidateCount;
        private set => SetProperty(ref _marketCandidateCount, value);
    }

    public int ActiveProcurementItemCount
    {
        get => _activeProcurementItemCount;
        private set => SetProperty(ref _activeProcurementItemCount, value);
    }

    public int AnalyzedCount
    {
        get => _analyzedCount;
        private set => SetProperty(ref _analyzedCount, value);
    }

    public bool HasPlan
    {
        get => _hasPlan;
        private set => SetProperty(ref _hasPlan, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    [RelayCommand]
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        var stamp = _session.CaptureVersionStamp();
        var key = CoreAcquisitionEvaluationLedgerKey.FromStamp(stamp);
        _currentKey = key;

        if (_ledgerCache.TryGet(key, CurrentFilter, out var cachedSnapshot))
        {
            ApplySnapshot(cachedSnapshot);
            return;
        }

        var plan = _session.ActivePlan;
        var evidence = _session.MarketEvidence;
        if (plan == null)
        {
            ClearSnapshot("No active plan");
            return;
        }

        IsLoading = true;
        try
        {
            var snapshot = await _workflow.BuildCurrentSnapshotAsync(
                plan,
                evidence.ShoppingPlans ?? [],
                evidence.UnavailableMarketItemIds,
                CurrentFilter,
                cancellationToken);
            if (cancellationToken.IsCancellationRequested || !_session.IsCurrent(stamp))
            {
                return;
            }

            if (snapshot == null)
            {
                ClearSnapshot("Acquisition evaluation is waiting for the current plan");
                return;
            }

            _ledgerCache.Store(key, CurrentFilter, snapshot);
            ApplySnapshot(snapshot);
        }
        finally
        {
            IsLoading = false;
        }
    }

    public Task<CoreAcquisitionDecisionResult> ChangeSourceAsync(
        int itemId,
        AcquisitionSource source,
        CancellationToken cancellationToken = default)
    {
        var result = _decisionService.ChangeSource(itemId, source);
        return RefreshAfterDecisionAsync(result, cancellationToken);
    }

    public Task<CoreAcquisitionDecisionResult> ChangeMarketHqAsync(
        int itemId,
        bool isHq,
        CancellationToken cancellationToken = default)
    {
        var result = _decisionService.ChangeMarketHq(itemId, isHq);
        return RefreshAfterDecisionAsync(result, cancellationToken);
    }

    private async Task<CoreAcquisitionDecisionResult> RefreshAfterDecisionAsync(
        CoreAcquisitionDecisionResult result,
        CancellationToken cancellationToken)
    {
        if (result.Changed)
        {
            _ledgerCache.Invalidate();
            await RefreshAsync(cancellationToken);
        }

        return result;
    }

    private void ApplySnapshot(CoreAcquisitionEvaluationSnapshot snapshot)
    {
        var selectedItemId = SelectedRow?.ItemId;
        Rows = new ObservableCollection<AcquisitionDecisionRowViewModel>(
            snapshot.VisibleRows.Select(row => new AcquisitionDecisionRowViewModel(row, snapshot.CostContext, this)));
        SelectedRow = selectedItemId.HasValue
            ? Rows.FirstOrDefault(row => row.ItemId == selectedItemId.Value) ?? Rows.FirstOrDefault()
            : Rows.FirstOrDefault(row => row.IsActiveProcurement) ?? Rows.FirstOrDefault();
        UniqueItemCount = snapshot.Rows.Count;
        MarketCandidateCount = snapshot.MarketAnalysisCandidates.Count;
        ActiveProcurementItemCount = snapshot.ActiveProcurementItems.Count;
        AnalyzedCount = snapshot.Rows.Count(row => IsAnalyzed(row.MarketEvidence));
        HasPlan = true;
        StatusMessage = $"{Rows.Count:N0} acquisition decisions visible";
    }

    internal void BeginChangeSource(AcquisitionDecisionRowViewModel row, AcquisitionSource source)
    {
        SafeFireAndForget(ChangeSourceAsync(row.ItemId, source), ex =>
        {
            StatusMessage = $"Failed to change source: {ex.Message}";
        });
    }

    internal void BeginChangeMarketHq(AcquisitionDecisionRowViewModel row, bool isHq)
    {
        SafeFireAndForget(ChangeMarketHqAsync(row.ItemId, isHq), ex =>
        {
            StatusMessage = $"Failed to change HQ preference: {ex.Message}";
        });
    }

    private void ClearSnapshot(string statusMessage)
    {
        Rows.Clear();
        SelectedRow = null;
        UniqueItemCount = 0;
        MarketCandidateCount = 0;
        ActiveProcurementItemCount = 0;
        AnalyzedCount = 0;
        HasPlan = false;
        StatusMessage = statusMessage;
    }

    private void OnSessionChanged(object? sender, CraftSessionChange change)
    {
        if (!CoreAcquisitionEvaluationLedgerCache.IsRelevantStateChange(change.Scope))
        {
            return;
        }

        _ledgerCache.Invalidate();
        SafeFireAndForget(RefreshAsync());
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _session.Changed -= OnSessionChanged;
        }

        base.Dispose(disposing);
    }

    private static bool IsAnalyzed(string marketEvidence) =>
        !string.Equals(marketEvidence, "Not analyzed", StringComparison.Ordinal) &&
        !string.Equals(marketEvidence, "Needs data", StringComparison.Ordinal);
}

public sealed partial class AcquisitionDecisionRowViewModel : ObservableObject
{
    private readonly AcquisitionEvaluationViewModel _owner;
    private AcquisitionSource _selectedSource;
    private bool _isMarketHq;

    public AcquisitionDecisionRowViewModel(
        CoreDecisionRow row,
        AcquisitionCostContext costContext,
        AcquisitionEvaluationViewModel owner)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        ItemId = row.ItemId;
        ItemName = row.ItemName;
        Source = row.Source;
        SourceDisplay = FormatSource(row.Source);
        SourceOptions = CreateSourceOptions(row);
        OptionRows = CreateOptionRows(row, costContext);
        _selectedSource = row.Source;
        _isMarketHq = row.MustBeHq;
        TotalQuantity = row.TotalQuantity;
        ActiveQuantity = row.ActiveQuantity;
        UsedIn = row.UsedIn;
        HasSuppressedOccurrences = row.HasSuppressedOccurrences;
        IsFullySuppressed = row.IsFullySuppressed;
        IsActiveProcurement = row.IsActiveProcurement;
        HasEditableOccurrences = row.HasEditableOccurrences;
        IsMarketCandidate = row.IsMarketCandidate;
        CanChangeMarketHq = row.HasEditableOccurrences && row.CanBuyFromMarket && row.CanBeHq;
        MarketEvidence = row.MarketEvidence;
        EstimatedCost = row.EstimatedCost;
        Status = GetStatus(row);
    }

    public int ItemId { get; }
    public string ItemName { get; }
    public AcquisitionSource Source { get; }
    public string SourceDisplay { get; }
    public IReadOnlyList<AcquisitionSourceOptionViewModel> SourceOptions { get; }
    public IReadOnlyList<AcquisitionOptionRowViewModel> OptionRows { get; }
    public AcquisitionSource SelectedSource
    {
        get => _selectedSource;
        set
        {
            if (!SetProperty(ref _selectedSource, value))
            {
                return;
            }

            _owner.BeginChangeSource(this, value);
        }
    }

    public int TotalQuantity { get; }
    public int ActiveQuantity { get; }
    public string UsedIn { get; }
    public bool HasSuppressedOccurrences { get; }
    public bool IsFullySuppressed { get; }
    public bool IsActiveProcurement { get; }
    public bool HasEditableOccurrences { get; }
    public bool CanChangeSource => HasEditableOccurrences && SourceOptions.Count > 1;
    public bool IsMarketCandidate { get; }
    public bool CanChangeMarketHq { get; }
    public bool IsMarketHq
    {
        get => _isMarketHq;
        set
        {
            if (!SetProperty(ref _isMarketHq, value))
            {
                return;
            }

            _owner.BeginChangeMarketHq(this, value);
        }
    }

    public string MarketEvidence { get; }
    public string EstimatedCost { get; }
    public string Status { get; }
    public string QuantityDisplay => ActiveQuantity > 0
        ? $"{ActiveQuantity:N0} active / {TotalQuantity:N0} total"
        : $"{TotalQuantity:N0} total";

    private static string FormatSource(AcquisitionSource source) =>
        source switch
        {
            AcquisitionSource.Craft => "Craft",
            AcquisitionSource.MarketBuyNq => "Market Buy (NQ)",
            AcquisitionSource.MarketBuyHq => "Market Buy (HQ)",
            AcquisitionSource.VendorBuy => "Vendor Buy",
            AcquisitionSource.VendorSpecialCurrency => "Special Vendor",
            AcquisitionSource.UnknownSource => "Unknown",
            _ => source.ToString()
        };

    private static IReadOnlyList<AcquisitionSourceOptionViewModel> CreateSourceOptions(CoreDecisionRow row)
    {
        var sources = AcquisitionPlanningService.GetAvailableSources(row.Node);
        if (!sources.Contains(row.Source))
        {
            sources.Insert(0, row.Source);
        }

        return sources
            .Distinct()
            .Select(source => new AcquisitionSourceOptionViewModel(source, FormatSource(source)))
            .ToList();
    }

    private static IReadOnlyList<AcquisitionOptionRowViewModel> CreateOptionRows(
        CoreDecisionRow row,
        AcquisitionCostContext costContext)
    {
        return AcquisitionPlanningService.GetAvailableSources(row.Node)
            .Distinct()
            .Select(source =>
            {
                var hasCost = CoreAcquisitionEvaluationCostCalculator.TryGetCost(
                    row,
                    source,
                    costContext,
                    out var cost);
                var isProjectedUnsupported = IsProjectedUnsupportedOption(row, source, costContext);

                return new AcquisitionOptionRowViewModel(
                    source,
                    GetOptionName(source),
                    GetOptionDetail(row, source, costContext),
                    hasCost ? $"{cost:N0}g" : "-",
                    source == AcquisitionSource.UnknownSource || (hasCost && !isProjectedUnsupported),
                    isProjectedUnsupported);
            })
            .ToList();
    }

    private static bool IsProjectedUnsupportedOption(
        CoreDecisionRow row,
        AcquisitionSource source,
        AcquisitionCostContext costContext)
    {
        if (source is not (AcquisitionSource.MarketBuyNq or AcquisitionSource.MarketBuyHq) ||
            !costContext.TryGetShoppingPlan(row.ItemId, out var marketPlan))
        {
            return false;
        }

        return MarketPurchaseCostProjectionService
            .Estimate(
                marketPlan,
                row.TotalQuantity,
                source == AcquisitionSource.MarketBuyHq,
                includeVendor: false)
            .IsUnsupportedProjection;
    }

    private static string GetOptionName(AcquisitionSource source) =>
        source switch
        {
            AcquisitionSource.MarketBuyNq => "Buy NQ",
            AcquisitionSource.MarketBuyHq => "Buy HQ",
            AcquisitionSource.VendorBuy => "Vendor",
            AcquisitionSource.VendorSpecialCurrency => "Special Vendor",
            AcquisitionSource.UnknownSource => "Figure it out",
            _ => FormatSource(source)
        };

    private static string GetOptionDetail(
        CoreDecisionRow row,
        AcquisitionSource source,
        AcquisitionCostContext costContext)
    {
        switch (source)
        {
            case AcquisitionSource.Craft:
                return "Uses the recipe tree with market evidence for child purchases.";
            case AcquisitionSource.MarketBuyNq:
                return GetMarketPlanDetail(row, costContext, hqOnly: false);
            case AcquisitionSource.MarketBuyHq:
                return GetMarketPlanDetail(row, costContext, hqOnly: true);
            case AcquisitionSource.VendorBuy:
                return GetVendorDetail(row);
            case AcquisitionSource.UnknownSource:
                return "No supported craft, market, or vendor source is known.";
            default:
                return FormatSource(source);
        }
    }

    private static string GetMarketPlanDetail(
        CoreDecisionRow row,
        AcquisitionCostContext costContext,
        bool hqOnly)
    {
        DetailedShoppingPlan? marketPlan = null;
        if (costContext.TryGetShoppingPlan(row.ItemId, out var resolvedMarketPlan) &&
            resolvedMarketPlan != null)
        {
            marketPlan = resolvedMarketPlan;
            var estimate = MarketPurchaseCostProjectionService.Estimate(
                marketPlan,
                row.TotalQuantity,
                hqOnly,
                includeVendor: false);
            if (estimate.IsUnsupportedProjection)
            {
                return "Projected cost; current search scope cannot fill this purchase.";
            }

            if (estimate.World != null)
            {
                return $"{estimate.World.WorldName} can cover {estimate.World.TotalQuantityPurchased}/{marketPlan?.QuantityNeeded ?? row.TotalQuantity}.";
            }
        }

        var recommendedSplit = marketPlan?.RecommendedSplit;
        if (recommendedSplit != null &&
            recommendedSplit.Sum(split => split.QuantityToBuy) >= row.TotalQuantity)
        {
            return $"{recommendedSplit.Count} world split can cover market purchase.";
        }

        if (!string.IsNullOrWhiteSpace(marketPlan?.Error))
        {
            return marketPlan.Error;
        }

        return hqOnly && row.HqUnitPrice <= 0
            ? "No HQ price loaded."
            : "Run Market Analysis for actionable market evidence.";
    }

    private static string GetVendorDetail(CoreDecisionRow row)
    {
        var vendor = row.VendorOptions
            .Where(option => option.IsGilVendor)
            .OrderBy(option => option.Price)
            .FirstOrDefault();

        return vendor == null
            ? "No gil vendor price loaded."
            : $"{vendor.Name} - {vendor.Location}";
    }

    private static string GetStatus(CoreDecisionRow row)
    {
        if (row.IsFullySuppressed)
        {
            return "Suppressed";
        }

        if (row.IsActiveProcurement)
        {
            return "Active";
        }

        return row.IsMarketCandidate ? "Market candidate" : "Reference";
    }
}

public sealed record AcquisitionFilterOptionViewModel(
    CoreAcquisitionFilter Filter,
    string DisplayName);

public sealed record AcquisitionSourceOptionViewModel(
    AcquisitionSource Source,
    string DisplayName);

public sealed record AcquisitionOptionRowViewModel(
    AcquisitionSource Source,
    string Name,
    string Detail,
    string CostText,
    bool IsAvailable,
    bool IsProjectedUnsupported);
