using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Web.Services;

public sealed class TradeOrderDraftFactory
{
    private readonly IRecipeLayerWorkflowService _recipeLayerWorkflow;

    public TradeOrderDraftFactory(IRecipeLayerWorkflowService recipeLayerWorkflow)
    {
        _recipeLayerWorkflow = recipeLayerWorkflow;
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

        var materials = _recipeLayerWorkflow.BuildActiveProcurementItems(plan)
            .Where(item => item.TotalQuantity > 0)
            .Select(ToMaterialSnapshot)
            .ToArray();
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
                SourcePlanName = GetPlanName(appState),
                PlanSessionVersion = appState.PlanSessionVersion,
                MarketAnalysisVersion = versions.MarketAnalysisVersion,
                ImportedAtUtc = request.CreatedAtUtc,
                RootItems = rootItems,
                Materials = materials
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

    private static decimal EstimateRootSaleValue(PlanNode node)
    {
        var unitPrice = node.MustBeHq && node.HqMarketPrice > 0
            ? node.HqMarketPrice
            : node.MarketPrice;
        return unitPrice * node.Quantity;
    }

    private static TradeOrderMaterialSnapshot ToMaterialSnapshot(MaterialAggregate material)
    {
        return new TradeOrderMaterialSnapshot(
            material.ItemId,
            material.Name,
            material.TotalQuantity,
            material.RequiresHq,
            material.UnitPrice,
            material.UnitPrice * material.TotalQuantity);
    }

    private static string CreateSuggestedTitle(IReadOnlyList<TradeOrderRootItemSnapshot> rootItems)
    {
        var root = rootItems
            .OrderByDescending(item => item.EstimatedSaleValue)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .First();
        return $"{root.Name} Commission";
    }
}

public sealed record TradeOrderCreateRequest(
    AppState AppState,
    Guid CompanyProfileId,
    Guid? AssignedCrafterId,
    string? Title,
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
