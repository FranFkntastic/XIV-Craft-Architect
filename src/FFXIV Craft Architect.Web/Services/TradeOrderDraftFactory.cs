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
            AuthoringSchemaVersion = TradeOrder.CurrentAuthoringSchemaVersion,
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
                CostBasis = source.MaterialQuote == null
                    ? CommissionCostBasis.SelectedAcquisitionSources
                    : CommissionCostBasis.ProcurementRoute,
                MarketFetchScope = source.MarketFetchScope,
                Region = source.SelectedRegion,
                DataCenter = source.SelectedDataCenter,
                RequestedDataCenters = source.RequestedDataCenters.ToArray(),
                PlanSessionVersion = source.PlanSessionVersion,
                MarketAnalysisVersion = source.MarketAnalysisVersion,
                ImportedAtUtc = request.CreatedAtUtc,
                RootItems = rootItems,
                Materials = materials,
                MaterialQuote = source.MaterialQuote,
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
            AuthoringSchemaVersion = TradeOrder.CurrentAuthoringSchemaVersion,
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
                MarketFetchScope = request.MarketFetchScope,
                Region = request.Region,
                DataCenter = request.DataCenter,
                World = NormalizeOptionalText(request.World),
                RequestedDataCenters = request.RequestedDataCenters.ToArray(),
                ImportedAtUtc = request.CreatedAtUtc,
                RootItems = rootItems,
                Materials = []
            },
            History = history
        };

        return TradeOrderDraftCreateResult.Available(order);
    }

    /// <summary>
    /// A fresh draft order from a company commission template. The template's supply choices
    /// come back alongside so the caller can seed responsibilities and initialize the plan.
    /// </summary>
    public TradeOrderSeededDraftResult CreateFromTemplate(TradeCommissionTemplateOrderCreateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Template);

        var template = TradeCommissionTemplateValidator.Normalize(request.Template);
        var rootItems = template.Outputs
            .Where(output => output.Quantity > 0)
            .Select(output => new TradeOrderRootItemSnapshot(
                output.ItemId,
                output.Name,
                output.Quantity,
                output.MustBeHq,
                0m))
            .ToArray();
        if (rootItems.Length == 0)
        {
            return TradeOrderSeededDraftResult.Unavailable(
                $"Template \"{template.Name}\" has no outputs to commission.");
        }

        var title = FirstNonBlank(request.Title, template.DefaultOrderTitle) ??
            CreateSuggestedTitle(rootItems);
        var order = CreateSeededOrder(
            request.CompanyProfileId,
            request.AssignedCrafterId,
            title,
            template.Notes,
            template.PaymentSchedule,
            template.CustomPaymentTerms,
            paymentPolicyOverride: null,
            rootItems,
            request.PricingScope,
            world: null,
            $"Created from template \"{template.Name}\".",
            request.CreatedAtUtc);
        return TradeOrderSeededDraftResult.Available(
            order,
            template.SupplyChoices,
            TradeCommissionTemplateMapper.ToResponsibilityLines(template.SupplyChoices));
    }

    /// <summary>
    /// A fresh, unpublished, unassigned draft with the same request as an existing order:
    /// outputs and payment schedule (from canonical terms when a commission exists), notes,
    /// and supply choices. Claims, publication, commission, history, plan and pricing are not
    /// carried over.
    /// </summary>
    public TradeOrderSeededDraftResult CreateDuplicate(TradeOrderDuplicateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Source);

        var source = request.Source;
        var estimatedValues = (source.SourceSnapshot?.RootItems ?? [])
            .GroupBy(item => (item.ItemId, item.MustBeHq))
            .ToDictionary(group => group.Key, group => group.First());
        var rootItems = TradeCommissionTemplateMapper.ResolveOutputs(source)
            .Select(output =>
            {
                var unitValue = estimatedValues.TryGetValue((output.ItemId, output.MustBeHq), out var root) &&
                    root.Quantity > 0
                        ? root.EstimatedSaleValue / root.Quantity
                        : 0m;
                return new TradeOrderRootItemSnapshot(
                    output.ItemId,
                    output.Name,
                    output.Quantity,
                    output.MustBeHq,
                    unitValue * output.Quantity);
            })
            .ToArray();
        if (rootItems.Length == 0)
        {
            return TradeOrderSeededDraftResult.Unavailable("The order has no outputs to duplicate.");
        }

        var (schedule, customTerms) = TradeCommissionTemplateMapper.ResolvePaymentSchedule(source);
        var title = FirstNonBlank(request.Title) ??
            (string.IsNullOrWhiteSpace(source.Title)
                ? CreateSuggestedTitle(rootItems)
                : $"{source.Title.Trim()} (Copy)");
        var snapshot = source.SourceSnapshot;
        var pricingScope = snapshot is { MarketFetchScope: { } scope } &&
            !string.IsNullOrWhiteSpace(snapshot.Region)
                ? new SeedPricingScope(
                    scope,
                    snapshot.Region,
                    snapshot.DataCenter,
                    snapshot.RequestedDataCenters)
                : null;
        var order = CreateSeededOrder(
            source.CompanyProfileId,
            assignedCrafterId: null,
            title,
            source.Notes,
            schedule,
            customTerms,
            paymentPolicyOverride: null,
            rootItems,
            pricingScope,
            snapshot?.World,
            $"Duplicated from \"{source.Title}\".",
            request.CreatedAtUtc);
        var supplyChoices = TradeCommissionTemplateMapper.ExtractTemplateSupplyChoices(
            source,
            request.Responsibilities,
            request.LinkedPlan);
        return TradeOrderSeededDraftResult.Available(
            order,
            supplyChoices,
            TradeCommissionTemplateMapper.ToResponsibilityLines(supplyChoices));
    }

    private static TradeOrder CreateSeededOrder(
        Guid companyProfileId,
        Guid? assignedCrafterId,
        string title,
        string? notes,
        CompanyCommissionPaymentSchedule paymentSchedule,
        string? customPaymentTerms,
        TradePaymentPolicy? paymentPolicyOverride,
        IReadOnlyList<TradeOrderRootItemSnapshot> rootItems,
        SeedPricingScope? pricingScope,
        string? world,
        string createdNote,
        DateTime createdAtUtc)
    {
        var orderId = Guid.NewGuid();
        var status = assignedCrafterId.HasValue
            ? TradeOrderStatus.Assigned
            : TradeOrderStatus.ReadyToAssign;
        return new TradeOrder
        {
            Id = orderId,
            AuthoringSchemaVersion = TradeOrder.CurrentAuthoringSchemaVersion,
            CompanyProfileId = companyProfileId,
            Title = title.Trim(),
            Status = status,
            AssignedCrafterId = assignedCrafterId,
            CommissionedAtUtc = createdAtUtc,
            CreatedAtUtc = createdAtUtc,
            UpdatedAtUtc = createdAtUtc,
            Notes = NormalizeOptionalText(notes),
            PaymentPolicyOverride = paymentPolicyOverride,
            PaymentSchedule = paymentSchedule,
            CustomPaymentTerms = paymentSchedule == CompanyCommissionPaymentSchedule.Custom
                ? NormalizeOptionalText(customPaymentTerms)
                : null,
            SourceSnapshot = new TradeOrderSourceSnapshot
            {
                SourceKind = TradeOrderSourceKind.TradeRequestedOutputs,
                SourcePlanName = "Trade requested outputs",
                MarketFetchScope = pricingScope?.Scope,
                Region = pricingScope?.Region,
                DataCenter = pricingScope?.DataCenter,
                World = NormalizeOptionalText(world),
                RequestedDataCenters = pricingScope?.RequestedDataCenters.ToArray() ?? [],
                ImportedAtUtc = createdAtUtc,
                RootItems = rootItems.ToArray(),
                Materials = []
            },
            History = CreateInitialHistory(
                companyProfileId,
                orderId,
                assignedCrafterId,
                status,
                createdNote,
                createdAtUtc)
        };
    }

    private static string? FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

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
    string Region,
    MarketFetchScope MarketFetchScope,
    IReadOnlyList<string> RequestedDataCenters,
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

public sealed record SeedPricingScope(
    MarketFetchScope Scope,
    string Region,
    string? DataCenter,
    IReadOnlyList<string> RequestedDataCenters)
{
    public static SeedPricingScope From(TradeCompanyPricingScope scope) =>
        new(
            scope.MarketFetchScope,
            scope.Region,
            scope.EffectiveDataCenter,
            MarketFetchScopeResolver.GetDataCenters(
                scope.MarketFetchScope,
                scope.EffectiveDataCenter,
                scope.Region,
                [scope.Region]));
}

public sealed record TradeCommissionTemplateOrderCreateRequest(
    TradeCommissionTemplate Template,
    Guid CompanyProfileId,
    DateTime CreatedAtUtc,
    Guid? AssignedCrafterId = null,
    string? Title = null,
    SeedPricingScope? PricingScope = null);

public sealed record TradeOrderDuplicateRequest(
    TradeOrder Source,
    DateTime CreatedAtUtc,
    string? Title = null,
    // The source order's payroll draft and linked plan, when loaded, carry its supply choices.
    TradePayrollWorkflowDraft? Responsibilities = null,
    StoredPlan? LinkedPlan = null);

/// <summary>A new draft plus the supply choices to apply when its plan is initialized.</summary>
public sealed record TradeOrderSeededDraftResult(
    TradeOrderDraftCreateResult Draft,
    IReadOnlyList<TradeCommissionTemplateSupplyChoice> SupplyChoices,
    IReadOnlyList<TradePayrollResponsibilityLine> Responsibilities)
{
    public bool CanCreate => Draft.CanCreate;
    public TradeOrder? Order => Draft.Order;

    public static TradeOrderSeededDraftResult Available(
        TradeOrder order,
        IReadOnlyList<TradeCommissionTemplateSupplyChoice> supplyChoices,
        IReadOnlyList<TradePayrollResponsibilityLine> responsibilities) =>
        new(TradeOrderDraftCreateResult.Available(order), supplyChoices, responsibilities);

    public static TradeOrderSeededDraftResult Unavailable(string reason) =>
        new(TradeOrderDraftCreateResult.Unavailable(reason), [], []);
}
