using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Web.Services.ProfileHosting;

namespace FFXIV_Craft_Architect.Web.Services;

public enum PlanBrowserTab
{
    MyPlans,
    Trade,
    Archived
}

public enum PlanBrowserPlanKind
{
    /// <summary>A plan with no order association.</summary>
    User,

    /// <summary>A sealed plan owned by a Trade order (<see cref="StoredPlan.LinkedOrderId"/>).</summary>
    Linked,

    /// <summary>A "local changes" copy edited from an order-linked plan.</summary>
    Fork,

    /// <summary>
    /// An unsealed plan whose name is a Trade order's generated plan name
    /// (saved before order plans were sealed, or after the seal was lost).
    /// </summary>
    OrderNamed
}

public enum PlanBrowserGroupKind
{
    None,
    Order,
    UnattributedEdits,
    OrderStatusUnavailable,
    MissingOrder
}

public sealed record PlanBrowserOrderInfo(
    Guid Id,
    string Title,
    TradeOrderStatus Status)
{
    public bool IsOpen => !TradeOrderStatusWorkflow.IsArchived(Status);
}

public sealed class PlanBrowserRow
{
    public required StoredPlanSummary Summary { get; init; }
    public required PlanBrowserPlanKind Kind { get; init; }
    public required PlanBrowserTab Tab { get; init; }
    public required PlanBrowserGroupKind GroupKind { get; init; }
    public required string DisplayName { get; init; }
    public Guid? OrderId { get; init; }
    public PlanBrowserOrderInfo? Order { get; init; }

    public string Id => Summary.Id;
    public bool IsEmpty => Summary.ItemCount == 0;
    public bool CanRename => Kind != PlanBrowserPlanKind.Linked;

    public string GroupKey => GroupKind switch
    {
        PlanBrowserGroupKind.Order => $"order:{OrderId:D}",
        PlanBrowserGroupKind.None => "none",
        _ => GroupKind.ToString()
    };

    public bool Matches(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        var term = query.Trim();
        return Summary.Name.Contains(term, StringComparison.OrdinalIgnoreCase) ||
               (Order?.Title.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false) ||
               Summary.TopItems.Any(item =>
                   item.Name.Contains(term, StringComparison.OrdinalIgnoreCase));
    }
}

public sealed class PlanBrowserCatalog
{
    public required IReadOnlyList<PlanBrowserRow> Rows { get; init; }

    /// <summary>Null when Trade orders loaded; otherwise why they did not.</summary>
    public string? OrdersError { get; init; }

    /// <summary>Plan names that attribute a plan to an order by name alone.</summary>
    public IReadOnlySet<string> OrderPlanNames { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    public static PlanBrowserCatalog Empty { get; } = new() { Rows = [] };

    /// <summary>
    /// Classifies plan summaries against the known Trade orders. Pure and
    /// allocation-light: never loads plan payloads.
    /// </summary>
    public static PlanBrowserCatalog Build(
        IEnumerable<StoredPlanSummary> summaries,
        IReadOnlyDictionary<Guid, PlanBrowserOrderInfo>? orders,
        IEnumerable<TradeOrder>? generatedNameSources,
        string? ordersError)
    {
        var plans = summaries
            .Where(summary => !string.Equals(summary.Id, "autosave", StringComparison.Ordinal))
            .ToArray();
        var knownOrders = orders ?? new Dictionary<Guid, PlanBrowserOrderInfo>();
        var ordersAvailable = orders != null;
        var orderIdsByPlanName = BuildPlanNameIndex(plans, knownOrders, generatedNameSources);

        var rows = new List<PlanBrowserRow>(plans.Length);
        foreach (var summary in plans)
        {
            rows.Add(Classify(summary, knownOrders, ordersAvailable, orderIdsByPlanName));
        }

        return new PlanBrowserCatalog
        {
            Rows = rows,
            OrdersError = ordersError,
            OrderPlanNames = orderIdsByPlanName.Keys.ToHashSet(StringComparer.Ordinal)
        };
    }

    /// <summary>
    /// The name a fork keeps once detached to My plans. Its base name is reused
    /// unless an order claims it, because a plan named like an order is
    /// attributed back to that order on the next load.
    /// </summary>
    public static string ChooseDetachedName(string forkName, IReadOnlySet<string> orderPlanNames)
    {
        var baseName = StripForkSuffix(forkName);
        if (baseName.Length == 0)
        {
            baseName = forkName.Trim();
        }

        var candidate = baseName;
        for (var copy = 1; orderPlanNames.Contains(candidate.Trim()); copy++)
        {
            candidate = copy == 1 ? $"{baseName} (my copy)" : $"{baseName} (my copy {copy})";
        }

        return candidate;
    }

    public static bool IsForkName(string name) =>
        name.EndsWith(WebPlanPersistenceService.LocalChangesSuffix, StringComparison.Ordinal);

    public static string StripForkSuffix(string name)
    {
        var stripped = name;
        while (IsForkName(stripped))
        {
            stripped = stripped[..^WebPlanPersistenceService.LocalChangesSuffix.Length];
        }

        return stripped.Trim();
    }

    private static PlanBrowserRow Classify(
        StoredPlanSummary summary,
        IReadOnlyDictionary<Guid, PlanBrowserOrderInfo> orders,
        bool ordersAvailable,
        IReadOnlyDictionary<string, HashSet<Guid>> orderIdsByPlanName)
    {
        if (summary.LinkedOrderId is { } linkedOrderId)
        {
            return CreateOrderRow(
                summary,
                PlanBrowserPlanKind.Linked,
                summary.Name,
                linkedOrderId,
                orders,
                ordersAvailable);
        }

        var isFork = summary.SourceOrderId.HasValue || IsForkName(summary.Name);
        if (!isFork &&
            orderIdsByPlanName.TryGetValue(summary.Name.Trim(), out var namedOrderIds))
        {
            if (namedOrderIds.Count == 1)
            {
                return CreateOrderRow(
                    summary,
                    PlanBrowserPlanKind.OrderNamed,
                    summary.Name,
                    namedOrderIds.First(),
                    orders,
                    ordersAvailable);
            }

            return new PlanBrowserRow
            {
                Summary = summary,
                Kind = PlanBrowserPlanKind.OrderNamed,
                Tab = PlanBrowserTab.Trade,
                GroupKind = PlanBrowserGroupKind.UnattributedEdits,
                DisplayName = summary.Name
            };
        }

        if (!isFork)
        {
            return new PlanBrowserRow
            {
                Summary = summary,
                Kind = PlanBrowserPlanKind.User,
                Tab = PlanBrowserTab.MyPlans,
                GroupKind = PlanBrowserGroupKind.None,
                DisplayName = summary.Name
            };
        }

        var displayName = StripForkSuffix(summary.Name);
        if (displayName.Length == 0)
        {
            displayName = summary.Name;
        }

        var sourceOrderId = summary.SourceOrderId ??
            (orderIdsByPlanName.TryGetValue(displayName, out var candidates) && candidates.Count == 1
                ? candidates.First()
                : null);
        if (sourceOrderId is { } orderId)
        {
            return CreateOrderRow(
                summary,
                PlanBrowserPlanKind.Fork,
                displayName,
                orderId,
                orders,
                ordersAvailable);
        }

        return new PlanBrowserRow
        {
            Summary = summary,
            Kind = PlanBrowserPlanKind.Fork,
            Tab = PlanBrowserTab.Trade,
            GroupKind = PlanBrowserGroupKind.UnattributedEdits,
            DisplayName = displayName
        };
    }

    private static PlanBrowserRow CreateOrderRow(
        StoredPlanSummary summary,
        PlanBrowserPlanKind kind,
        string displayName,
        Guid orderId,
        IReadOnlyDictionary<Guid, PlanBrowserOrderInfo> orders,
        bool ordersAvailable)
    {
        if (orders.TryGetValue(orderId, out var order))
        {
            return new PlanBrowserRow
            {
                Summary = summary,
                Kind = kind,
                Tab = order.IsOpen ? PlanBrowserTab.Trade : PlanBrowserTab.Archived,
                GroupKind = PlanBrowserGroupKind.Order,
                DisplayName = displayName,
                OrderId = orderId,
                Order = order
            };
        }

        return new PlanBrowserRow
        {
            Summary = summary,
            Kind = kind,
            Tab = ordersAvailable ? PlanBrowserTab.Archived : PlanBrowserTab.Trade,
            GroupKind = ordersAvailable
                ? PlanBrowserGroupKind.MissingOrder
                : PlanBrowserGroupKind.OrderStatusUnavailable,
            DisplayName = displayName,
            OrderId = orderId
        };
    }

    private static Dictionary<string, HashSet<Guid>> BuildPlanNameIndex(
        IEnumerable<StoredPlanSummary> plans,
        IReadOnlyDictionary<Guid, PlanBrowserOrderInfo> orders,
        IEnumerable<TradeOrder>? generatedNameSources)
    {
        var index = new Dictionary<string, HashSet<Guid>>(StringComparer.Ordinal);

        void Add(string? name, Guid orderId)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            var key = name.Trim();
            if (!index.TryGetValue(key, out var ids))
            {
                index[key] = ids = [];
            }

            ids.Add(orderId);
        }

        foreach (var order in orders.Values)
        {
            Add(
                TradeOrderWorkflow.CreateGeneratedCraftPlanName(
                    new TradeOrder { Id = order.Id, Title = order.Title }),
                order.Id);
        }

        foreach (var order in generatedNameSources ?? [])
        {
            Add(order.CraftPlanName, order.Id);
        }

        foreach (var plan in plans)
        {
            if (plan.LinkedOrderId is { } linkedOrderId)
            {
                Add(plan.Name, linkedOrderId);
            }
        }

        return index;
    }
}

public sealed record PlanBrowserMovedPlan(string Id, string Name);

public sealed record PlanBrowserDeleteResult(
    IReadOnlyList<string> DeletedIds,
    IReadOnlyList<string> SkippedIds,
    IReadOnlyList<string> MissingIds);

/// <summary>
/// Reads and mutates the plan browser's view of stored plans. Order context
/// comes from local Trade orders, the hosted order projection and hosted
/// archive summaries; plan payloads are never materialized.
/// </summary>
public sealed class PlanBrowserService
{
    private readonly WebPlanPersistenceService _plans;
    private readonly TradeOperationsPersistenceService _tradeOperations;
    private readonly HostedOrderProjectionStore _hostedOrders;
    private readonly TradeOrderArchiveSummaryStore _archiveSummaries;
    private readonly ILogger<PlanBrowserService> _logger;

    public PlanBrowserService(
        WebPlanPersistenceService plans,
        TradeOperationsPersistenceService tradeOperations,
        HostedOrderProjectionStore hostedOrders,
        TradeOrderArchiveSummaryStore archiveSummaries,
        ILogger<PlanBrowserService> logger)
    {
        _plans = plans;
        _tradeOperations = tradeOperations;
        _hostedOrders = hostedOrders;
        _archiveSummaries = archiveSummaries;
        _logger = logger;
    }

    public async Task<PlanBrowserCatalog> LoadAsync()
    {
        var summaries = await _plans.LoadPlanSummariesAsync();
        var orders = await TryLoadOrdersAsync();
        return PlanBrowserCatalog.Build(
            summaries,
            orders.Lookup,
            orders.Sources,
            orders.Error);
    }

    /// <summary>
    /// Deletes plans unless they are linked to an order that still exists and is
    /// not Completed/Canceled. When Trade orders cannot be read, every
    /// order-linked plan is protected.
    /// </summary>
    public async Task<PlanBrowserDeleteResult> DeleteAsync(IReadOnlyCollection<string> planIds)
    {
        if (planIds.Count == 0)
        {
            return new PlanBrowserDeleteResult([], [], []);
        }

        var orders = await TryLoadOrdersAsync();
        IReadOnlyCollection<Guid> protectedOrderIds;
        if (orders.Error == null)
        {
            protectedOrderIds = orders.OpenOrderIds;
        }
        else
        {
            protectedOrderIds = (await _plans.LoadPlanSummariesAsync())
                .Where(summary => summary.LinkedOrderId.HasValue)
                .Select(summary => summary.LinkedOrderId!.Value)
                .ToHashSet();
        }

        var result = await _plans.DeletePlansAsync(planIds, protectedOrderIds);
        return new PlanBrowserDeleteResult(result.DeletedIds, result.SkippedIds, result.MissingIds);
    }

    /// <summary>Moves "local changes" forks to My plans. Returns each moved plan and its stored name.</summary>
    public async Task<IReadOnlyList<PlanBrowserMovedPlan>> MoveToMyPlansAsync(IEnumerable<PlanBrowserRow> forks)
    {
        var orderPlanNames = (await LoadAsync()).OrderPlanNames;
        var moved = new List<PlanBrowserMovedPlan>();
        foreach (var row in forks)
        {
            if (row.Kind != PlanBrowserPlanKind.Fork || row.Summary.LinkedOrderId.HasValue)
            {
                continue;
            }

            var name = PlanBrowserCatalog.ChooseDetachedName(row.Summary.Name, orderPlanNames);
            var renamedTo = string.Equals(name, row.Summary.Name, StringComparison.Ordinal)
                ? null
                : name;
            if (await _plans.DetachFromSourceOrderAsync(row.Id, renamedTo))
            {
                moved.Add(new PlanBrowserMovedPlan(row.Id, name));
            }
        }

        return moved;
    }

    /// <summary>
    /// Open orders protect their linked plans. Order presence is the union of
    /// local Trade storage and the hosted projection, so a plan is protected if
    /// either source still has its order open.
    /// </summary>
    private async Task<OrderLoadResult> TryLoadOrdersAsync()
    {
        var lookup = new Dictionary<Guid, PlanBrowserOrderInfo>();
        var openOrderIds = new HashSet<Guid>();
        var sources = new List<TradeOrder>();

        try
        {
            foreach (var record in await _archiveSummaries.LoadAsync())
            {
                lookup[record.OrderId] = new PlanBrowserOrderInfo(
                    record.OrderId,
                    record.Summary.Title,
                    record.Summary.Status);
            }
        }
        catch (Exception ex)
        {
            // Archive summaries only add titles for closed hosted orders.
            _logger.LogWarning(ex, "Plan browser could not read Trade order archive summaries");
        }

        try
        {
            foreach (var order in await _tradeOperations.LoadAllOrdersAsync())
            {
                Add(order);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Plan browser could not read Trade orders; order-linked plans stay protected");
            return new OrderLoadResult(null, new HashSet<Guid>(), [], ex.Message);
        }

        foreach (var snapshot in _hostedOrders.GetAll())
        {
            if (!snapshot.Deleted && snapshot.Order is { } order)
            {
                Add(order);
            }
        }

        return new OrderLoadResult(lookup, openOrderIds, sources, null);

        void Add(TradeOrder order)
        {
            var info = new PlanBrowserOrderInfo(order.Id, order.Title, order.Status);
            lookup[order.Id] = info;
            sources.Add(order);
            if (info.IsOpen)
            {
                openOrderIds.Add(order.Id);
            }
        }
    }

    private sealed record OrderLoadResult(
        IReadOnlyDictionary<Guid, PlanBrowserOrderInfo>? Lookup,
        IReadOnlySet<Guid> OpenOrderIds,
        IReadOnlyList<TradeOrder> Sources,
        string? Error);
}
