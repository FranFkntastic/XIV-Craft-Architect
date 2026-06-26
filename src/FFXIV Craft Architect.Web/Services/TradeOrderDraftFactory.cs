using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Web.Services;

public sealed class TradeOrderDraftFactory
{
    private readonly IRecipeLayerWorkflowService _recipeLayerWorkflow;
    private readonly CommissionCostBasisResolver _costBasisResolver;

    public TradeOrderDraftFactory(
        IRecipeLayerWorkflowService recipeLayerWorkflow,
        CommissionCostBasisResolver costBasisResolver)
    {
        _recipeLayerWorkflow = recipeLayerWorkflow;
        _costBasisResolver = costBasisResolver;
    }

    public TradeOrderDraftCreateResult CreateFromCurrentPlan(TradeOrderCreateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var appState = request.AppState;
        var plan = appState.CurrentPlan;
        if (plan == null)
        {
            return TradeOrderDraftCreateResult.Unavailable("Create or load a craft plan before creating a Trade order.");
        }

        var rootItems = plan.RootItems
            .Select(ToRootSnapshot)
            .ToArray();
        if (rootItems.Length == 0)
        {
            return TradeOrderDraftCreateResult.Unavailable("The active craft plan does not contain root items to commission.");
        }

        var demandProjection = _recipeLayerWorkflow.BuildDemandProjection(plan);
        var activeProcurementItems = demandProjection.ToActiveProcurementMaterialAggregates()
            .Where(item => item.TotalQuantity > 0)
            .ToArray();
        var activeDemandRows = demandProjection.ActiveProcurementDemand
            .Where(row => row.Quantity > 0)
            .ToArray();
        var warnings = new List<string>();
        if (appState.MarketItemAnalyses.Count == 0)
        {
            warnings.Add("No market-analysis evidence is loaded. Payment uses plan prices where available and may be incomplete.");
        }
        else if (!string.IsNullOrWhiteSpace(appState.MarketAnalysisScopeWarning))
        {
            warnings.Add(appState.MarketAnalysisScopeWarning);
        }

        var lines = _costBasisResolver.BuildSelectedSourceLines(
            activeDemandRows,
            appState.MarketItemAnalyses.ToArray(),
            appState.ShoppingPlans.ToArray());
        warnings.AddRange(lines.SelectMany(line => line.Warnings));
        warnings.Add("Labor-standard evidence is unavailable until this order is repriced from its linked craft plan.");
        var materials = TradeOrderMaterialEvidenceMapper.ToMaterialSnapshots(lines);
        var title = string.IsNullOrWhiteSpace(request.Title)
            ? CreateSuggestedTitle(rootItems)
            : request.Title.Trim();
        var orderId = Guid.NewGuid();
        var status = request.AssignedCrafterId.HasValue
            ? TradeOrderStatus.Assigned
            : TradeOrderStatus.ReadyToAssign;
        var history = new List<TradeOrderHistoryEvent>
        {
            new()
            {
                Id = Guid.NewGuid(),
                CompanyProfileId = request.CompanyProfileId,
                OrderId = orderId,
                Kind = TradeOrderHistoryEventKind.Created,
                Note = $"Imported from {GetPlanName(appState)}.",
                ToStatus = status,
                CreatedAtUtc = request.CreatedAtUtc
            }
        };

        if (request.AssignedCrafterId.HasValue)
        {
            history.Add(new TradeOrderHistoryEvent
            {
                Id = Guid.NewGuid(),
                CompanyProfileId = request.CompanyProfileId,
                OrderId = orderId,
                Kind = TradeOrderHistoryEventKind.Assigned,
                Note = "Assigned during order creation.",
                CrafterId = request.AssignedCrafterId,
                ToStatus = status,
                CreatedAtUtc = request.CreatedAtUtc
            });
        }

        var versions = appState.CurrentVersions;
        var order = new TradeOrder
        {
            Id = orderId,
            CompanyProfileId = request.CompanyProfileId,
            Title = title,
            Status = status,
            AssignedCrafterId = request.AssignedCrafterId,
            CommissionedAtUtc = request.CreatedAtUtc,
            CreatedAtUtc = request.CreatedAtUtc,
            UpdatedAtUtc = request.CreatedAtUtc,
            SourceSnapshot = new TradeOrderSourceSnapshot
            {
                SourceKind = TradeOrderSourceKind.ActiveCraftPlan,
                SourcePlanId = appState.CurrentPlanId,
                SourcePlanName = GetPlanName(appState),
                DataCenter = appState.SelectedDataCenter,
                PlanSessionVersion = appState.PlanSessionVersion,
                MarketAnalysisVersion = versions.MarketAnalysisVersion,
                ImportedAtUtc = request.CreatedAtUtc,
                RootItems = rootItems,
                Materials = materials,
                Warnings = warnings
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(warning => warning, StringComparer.OrdinalIgnoreCase)
                    .ToArray()
            },
            History = history
        };
        return TradeOrderDraftCreateResult.Available(order);
    }

    public TradeOrderDraftCreateResult CreateFromRequestedOutputs(TradeRequestedOrderCreateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var requestedOutputs = request.Outputs ?? [];
        var rootItems = requestedOutputs
            .Where(output => output.Quantity > 0)
            .Select(ToRootSnapshot)
            .ToArray();
        if (rootItems.Length == 0)
        {
            return TradeOrderDraftCreateResult.Unavailable("Add at least one requested output before creating a Trade order.");
        }

        var title = string.IsNullOrWhiteSpace(request.Title)
            ? TradeRequestedOrderWorkflow.CreateSuggestedTitle(requestedOutputs)
            : request.Title.Trim();
        var orderId = Guid.NewGuid();
        var status = request.AssignedCrafterId.HasValue
            ? TradeOrderStatus.Assigned
            : TradeOrderStatus.ReadyToAssign;
        var history = CreateInitialHistory(
            request.CompanyProfileId,
            orderId,
            request.AssignedCrafterId,
            status,
            "Created from requested outputs.",
            request.CreatedAtUtc);

        var order = new TradeOrder
        {
            Id = orderId,
            CompanyProfileId = request.CompanyProfileId,
            Title = title,
            Status = status,
            AssignedCrafterId = request.AssignedCrafterId,
            CommissionedAtUtc = request.CreatedAtUtc,
            CreatedAtUtc = request.CreatedAtUtc,
            UpdatedAtUtc = request.CreatedAtUtc,
            Notes = NormalizeOptionalText(request.Notes),
            SourceSnapshot = new TradeOrderSourceSnapshot
            {
                SourceKind = TradeOrderSourceKind.TradeRequestedOutputs,
                SourcePlanName = "Trade requested outputs",
                DataCenter = request.DataCenter,
                World = NormalizeOptionalText(request.World),
                ImportedAtUtc = request.CreatedAtUtc,
                RootItems = rootItems,
                Materials = []
            },
            History = history
        };

        return TradeOrderDraftCreateResult.Available(order);
    }

    private static string GetPlanName(AppState appState)
    {
        return string.IsNullOrWhiteSpace(appState.CurrentPlanName)
            ? appState.CurrentPlan?.Name ?? "Active craft plan"
            : appState.CurrentPlanName;
    }

    private static TradeOrderRootItemSnapshot ToRootSnapshot(PlanNode node)
    {
        return new TradeOrderRootItemSnapshot(
            node.ItemId,
            node.Name,
            node.Quantity,
            node.MustBeHq,
            EstimateRootSaleValue(node));
    }

    private static TradeOrderRootItemSnapshot ToRootSnapshot(TradeRequestedOrderOutput output)
    {
        return new TradeOrderRootItemSnapshot(
            output.ItemId,
            output.Name.Trim(),
            output.Quantity,
            output.MustBeHq,
            output.EstimatedSaleValue);
    }

    private static decimal EstimateRootSaleValue(PlanNode node)
    {
        var unitPrice = node.MustBeHq && node.HqMarketPrice > 0
            ? node.HqMarketPrice
            : node.MarketPrice;
        return unitPrice * node.Quantity;
    }

    private static string CreateSuggestedTitle(IReadOnlyList<TradeOrderRootItemSnapshot> rootItems)
    {
        var root = rootItems
            .OrderByDescending(item => item.EstimatedSaleValue)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .First();
        return $"{root.Name} Commission";
    }

    private static IReadOnlyList<TradeOrderHistoryEvent> CreateInitialHistory(
        Guid companyProfileId,
        Guid orderId,
        Guid? assignedCrafterId,
        TradeOrderStatus status,
        string createdNote,
        DateTime createdAtUtc)
    {
        var history = new List<TradeOrderHistoryEvent>
        {
            new()
            {
                Id = Guid.NewGuid(),
                CompanyProfileId = companyProfileId,
                OrderId = orderId,
                Kind = TradeOrderHistoryEventKind.Created,
                Note = createdNote,
                ToStatus = status,
                CreatedAtUtc = createdAtUtc
            }
        };

        if (assignedCrafterId.HasValue)
        {
            history.Add(new TradeOrderHistoryEvent
            {
                Id = Guid.NewGuid(),
                CompanyProfileId = companyProfileId,
                OrderId = orderId,
                Kind = TradeOrderHistoryEventKind.Assigned,
                Note = "Assigned during order creation.",
                CrafterId = assignedCrafterId,
                ToStatus = status,
                CreatedAtUtc = createdAtUtc
            });
        }

        return history;
    }

    private static string? NormalizeOptionalText(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}

public sealed record TradeOrderCreateRequest(
    AppState AppState,
    Guid CompanyProfileId,
    Guid? AssignedCrafterId,
    string? Title,
    DateTime CreatedAtUtc);

public sealed record TradeRequestedOrderCreateRequest(
    Guid CompanyProfileId,
    Guid? AssignedCrafterId,
    string? Title,
    IReadOnlyList<TradeRequestedOrderOutput> Outputs,
    string DataCenter,
    string? World,
    string? Notes,
    DateTime CreatedAtUtc);

public sealed record TradeOrderDraftCreateResult(
    bool CanCreate,
    TradeOrder? Order,
    string? UnavailableReason)
{
    public static TradeOrderDraftCreateResult Available(TradeOrder order)
    {
        return new TradeOrderDraftCreateResult(true, order, null);
    }

    public static TradeOrderDraftCreateResult Unavailable(string reason)
    {
        return new TradeOrderDraftCreateResult(false, null, reason);
    }
}
