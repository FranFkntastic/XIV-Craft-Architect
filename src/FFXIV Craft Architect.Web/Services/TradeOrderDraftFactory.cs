using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Web.Services;

public sealed class TradeOrderDraftFactory
{
    public TradeOrderDraftCreateResult CreateFromCurrentPlan(TradeOrderCreateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var source = request.Source;
        if (!source.HasPlan)
        {
            return TradeOrderDraftCreateResult.Unavailable("Create or load a craft plan before creating a Trade order.");
        }

        var rootItems = source.RootItems.ToArray();
        if (rootItems.Length == 0)
        {
            return TradeOrderDraftCreateResult.Unavailable("The active craft plan does not contain root items to commission.");
        }

        var warnings = source.Warnings.ToList();
        warnings.Add("Labor-standard evidence is unavailable until this order is repriced from its linked craft plan.");
        var materials = TradeOrderMaterialEvidenceMapper.ToMaterialSnapshots(source.MaterialLines);
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
                Note = $"Imported from {source.PlanName}.",
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
                SourcePlanId = source.PlanId,
                SourcePlanName = source.PlanName,
                CostBasis = CommissionCostBasis.SelectedAcquisitionSources,
                MarketFetchScope = source.MarketFetchScope,
                Region = source.SelectedRegion,
                DataCenter = source.SelectedDataCenter,
                RequestedDataCenters = source.RequestedDataCenters.ToArray(),
                PlanSessionVersion = source.PlanSessionVersion,
                MarketAnalysisVersion = source.MarketAnalysisVersion,
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
                MarketFetchScope = FFXIV_Craft_Architect.Core.Models.MarketFetchScope.SelectedDataCenter,
                Region = MarketFetchScopeResolver.ResolveRegionForDataCenter(request.DataCenter, string.Empty),
                DataCenter = request.DataCenter,
                World = NormalizeOptionalText(request.World),
                RequestedDataCenters = [request.DataCenter],
                ImportedAtUtc = request.CreatedAtUtc,
                RootItems = rootItems,
                Materials = []
            },
            History = history
        };

        return TradeOrderDraftCreateResult.Available(order);
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
    WorkerTradeProjection Source,
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
