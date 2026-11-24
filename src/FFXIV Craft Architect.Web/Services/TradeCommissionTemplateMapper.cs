using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Web.Services;

/// <summary>
/// Reads the reusable parts of an existing order (outputs, payment terms, supply choices)
/// for commission templates and order duplication. Canonical commission terms win over the
/// order's own authoring fields whenever a commission exists.
/// </summary>
public static class TradeCommissionTemplateMapper
{
    /// <summary>Saves an existing order's request and supply choices as a new template.</summary>
    public static TradeCommissionTemplate CreateTemplateFromOrder(
        TradeOrder order,
        string name,
        TradePayrollWorkflowDraft? responsibilities,
        StoredPlan? linkedPlan,
        DateTime createdAtUtc)
    {
        ArgumentNullException.ThrowIfNull(order);

        var (schedule, customTerms) = ResolvePaymentSchedule(order);
        return TradeCommissionTemplateValidator.Normalize(new TradeCommissionTemplate
        {
            Id = Guid.NewGuid(),
            Name = name,
            DefaultOrderTitle = order.Title,
            Notes = order.Notes,
            PaymentSchedule = schedule,
            CustomPaymentTerms = customTerms,
            Outputs = ResolveOutputs(order),
            SupplyChoices = ExtractTemplateSupplyChoices(order, responsibilities, linkedPlan),
            CreatedAtUtc = createdAtUtc,
            UpdatedAtUtc = createdAtUtc
        });
    }

    /// <summary>
    /// The order's requested outputs: current canonical terms when a commission exists,
    /// otherwise the order's root items.
    /// </summary>
    public static IReadOnlyList<TradeCommissionTemplateOutput> ResolveOutputs(TradeOrder order)
    {
        ArgumentNullException.ThrowIfNull(order);

        if (TryGetCurrentTerms(order, out var terms) && terms.Outputs.Count > 0)
        {
            return terms.Outputs
                .Where(output => output.RequiredQuantity > 0)
                .Select(output => new TradeCommissionTemplateOutput(
                    output.ItemId,
                    output.Name,
                    output.RequiredQuantity,
                    output.MustBeHq))
                .ToArray();
        }

        return (order.SourceSnapshot?.RootItems ?? [])
            .Where(item => item.Quantity > 0)
            .Select(item => new TradeCommissionTemplateOutput(
                item.ItemId,
                item.Name,
                item.Quantity,
                item.MustBeHq))
            .ToArray();
    }

    public static (CompanyCommissionPaymentSchedule Schedule, string? CustomTerms) ResolvePaymentSchedule(
        TradeOrder order)
    {
        ArgumentNullException.ThrowIfNull(order);

        return TryGetCurrentTerms(order, out var terms)
            ? (terms.Payment.Schedule, terms.Payment.CustomTerms)
            : (order.PaymentSchedule, order.CustomPaymentTerms);
    }

    /// <summary>
    /// Every company-provided material plus every explicit (user-selected) non-output
    /// acquisition override in the linked plan, with its responsibility.
    /// </summary>
    public static IReadOnlyList<TradeCommissionTemplateSupplyChoice> ExtractTemplateSupplyChoices(
        TradeOrder order,
        TradePayrollWorkflowDraft? responsibilities,
        StoredPlan? linkedPlan)
    {
        ArgumentNullException.ThrowIfNull(order);

        var outputKeys = ResolveOutputs(order)
            .Select(output => output.ItemId)
            .ToHashSet();
        var responsibilityByKey = ResolveResponsibilities(order, responsibilities)
            .GroupBy(line => (line.ItemId, line.RequiresHq))
            .ToDictionary(group => group.Key, group => group.Last().Responsibility);
        var planNodes = ReadNonRootPlanNodes(linkedPlan);
        var planNodeByKey = planNodes
            .GroupBy(node => (node.ItemId, node.MustBeHq))
            .ToDictionary(group => group.Key, group => group.First());

        var names = new Dictionary<int, string>();
        foreach (var material in order.SourceSnapshot?.Materials ?? [])
        {
            names.TryAdd(material.ItemId, material.Name);
        }
        if (TryGetCurrentTerms(order, out var terms))
        {
            foreach (var material in terms.Materials)
            {
                names.TryAdd(material.ItemId, material.Name);
            }
        }
        foreach (var node in planNodes)
        {
            names.TryAdd(node.ItemId, node.Name);
        }

        var choices = new Dictionary<(int ItemId, bool RequiresHq), TradeCommissionTemplateSupplyChoice>();
        foreach (var (key, responsibility) in responsibilityByKey)
        {
            if (responsibility != CommissionMaterialResponsibility.Provided || outputKeys.Contains(key.ItemId))
            {
                continue;
            }

            planNodeByKey.TryGetValue(key, out var node);
            AcquisitionSource? source = node is not null &&
                (node.Source == AcquisitionSource.OnHand ||
                 node.SourceReason == AcquisitionSourceReason.UserSelected)
                    ? node.Source
                    : null;
            choices[key] = new TradeCommissionTemplateSupplyChoice(
                key.ItemId,
                names.GetValueOrDefault(key.ItemId, $"Item {key.ItemId}"),
                key.RequiresHq,
                CommissionMaterialResponsibility.Provided,
                source);
        }

        foreach (var node in planNodes)
        {
            var key = (node.ItemId, node.MustBeHq);
            if (node.SourceReason != AcquisitionSourceReason.UserSelected ||
                outputKeys.Contains(node.ItemId) ||
                choices.ContainsKey(key))
            {
                continue;
            }

            choices[key] = new TradeCommissionTemplateSupplyChoice(
                node.ItemId,
                names.GetValueOrDefault(node.ItemId, node.Name),
                node.MustBeHq,
                responsibilityByKey.GetValueOrDefault(key, CommissionMaterialResponsibility.Crafter),
                node.Source);
        }

        return choices.Values
            .Where(choice => !string.IsNullOrWhiteSpace(choice.Name))
            .OrderBy(choice => choice.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(choice => choice.ItemId)
            .ToArray();
    }

    public const int MinScaleMultiplier = 1;
    public const int MaxScaleMultiplier = 99;

    /// <summary>
    /// Company-wide "always company-supplied" defaults with explicit template/order choices
    /// layered on top. An explicit choice for the same (item, HQ) key always wins.
    /// </summary>
    public static IReadOnlyList<TradeCommissionTemplateSupplyChoice> MergeCompanySupplyDefaults(
        IReadOnlyList<TradeCompanySupplyDefault>? companyDefaults,
        IReadOnlyList<TradeCommissionTemplateSupplyChoice>? explicitChoices)
    {
        var merged = new Dictionary<(int ItemId, bool RequiresHq), TradeCommissionTemplateSupplyChoice>();
        foreach (var item in companyDefaults ?? [])
        {
            if (item is null || item.ItemId <= 0)
            {
                continue;
            }

            merged[(item.ItemId, item.RequiresHq)] = new TradeCommissionTemplateSupplyChoice(
                item.ItemId,
                item.Name?.Trim() ?? string.Empty,
                item.RequiresHq,
                CommissionMaterialResponsibility.Provided,
                AcquisitionSource: null);
        }

        foreach (var choice in explicitChoices ?? [])
        {
            if (choice is null)
            {
                continue;
            }

            merged[(choice.ItemId, choice.RequiresHq)] = choice;
        }

        return merged.Values
            .OrderBy(choice => choice.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(choice => choice.ItemId)
            .ThenBy(choice => choice.RequiresHq)
            .ToArray();
    }

    /// <summary>
    /// Multiplies every output quantity by <paramref name="multiplier"/> (clamped to
    /// 1..99), keeping each quantity inside the template's 1..9999 bounds.
    /// </summary>
    public static IReadOnlyList<TradeCommissionTemplateOutput> ScaleOutputs(
        IReadOnlyList<TradeCommissionTemplateOutput> outputs,
        int multiplier)
    {
        ArgumentNullException.ThrowIfNull(outputs);

        var clampedMultiplier = Math.Clamp(multiplier, MinScaleMultiplier, MaxScaleMultiplier);
        return outputs
            .Select(output => output with
            {
                Quantity = (int)Math.Clamp(
                    (long)output.Quantity * clampedMultiplier,
                    1,
                    TradeCommissionTemplateValidator.MaxOutputQuantity)
            })
            .ToArray();
    }

    /// <summary>Template supply choices as payroll responsibility lines.</summary>
    public static IReadOnlyList<TradePayrollResponsibilityLine> ToResponsibilityLines(
        IReadOnlyList<TradeCommissionTemplateSupplyChoice>? supplyChoices) =>
        (supplyChoices ?? [])
            .GroupBy(choice => (choice.ItemId, choice.RequiresHq))
            .Select(group => new TradePayrollResponsibilityLine(
                group.Key.ItemId,
                group.Key.RequiresHq,
                group.Last().Responsibility))
            .ToArray();

    private static IEnumerable<TradePayrollResponsibilityLine> ResolveResponsibilities(
        TradeOrder order,
        TradePayrollWorkflowDraft? draft)
    {
        // Published terms are what the crafter agreed to; an unpublished order's own payroll
        // draft is the operator's latest intent.
        var published = order.CompanyCommission is { } commission &&
            commission.PublicMetadata.ViewState != CompanyCommissionPublicViewState.Draft;
        if ((published || draft is null) && TryGetCurrentTerms(order, out var terms))
        {
            return terms.Materials.Select(material => new TradePayrollResponsibilityLine(
                material.ItemId,
                material.RequiresHq,
                material.Responsibility));
        }

        return draft?.Responsibilities ?? [];
    }

    private static bool TryGetCurrentTerms(TradeOrder order, out CompanyCommissionTermsVersion terms)
    {
        var commission = order.CompanyCommission;
        terms = commission?.TermsVersions.FirstOrDefault(version =>
            version.Version == commission.CurrentTermsVersion)!;
        return terms is not null;
    }

    private static IReadOnlyList<PlanNode> ReadNonRootPlanNodes(StoredPlan? linkedPlan)
    {
        if (string.IsNullOrWhiteSpace(linkedPlan?.PlanJson))
        {
            return [];
        }

        CraftingPlan? plan;
        try
        {
            plan = CorePlanSessionLoadService.RestorePlan(linkedPlan.PlanJson, linkedPlan.PlanStateJson);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException)
        {
            return [];
        }

        var nodes = new List<PlanNode>();
        var stack = new Stack<PlanNode>(
            (plan?.RootItems ?? []).SelectMany(root => root.Children));
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            nodes.Add(node);
            foreach (var child in node.Children)
            {
                stack.Push(child);
            }
        }

        return nodes;
    }
}
