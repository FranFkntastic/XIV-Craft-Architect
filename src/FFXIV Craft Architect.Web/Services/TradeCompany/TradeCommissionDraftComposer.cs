using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Web.Services.TradeCompany;

/// <summary>Crafter-facing presentation and terms inputs for one commission brief.</summary>
public sealed record TradeCommissionBriefContext(
    string CompanyName,
    string Contact,
    string DeliveryInstructions,
    CompanyCommissionPaymentSchedule PaymentSchedule,
    string? CustomPaymentTerms,
    bool IsTestFixture = false,
    string StatusLabel = "Open for assignment",
    string AssignmentLabel = "Contact operator");

/// <summary>
/// Composes commission briefs from a Trade work package's stored evidence. The same composition
/// backs order creation, conversion of older plain drafts, and draft edits, so a new draft carries
/// exactly what the Orders page shows for that work package. Nothing here prices or reprices.
/// </summary>
public static class TradeCommissionDraftComposer
{
    public const string DefaultCompanyName = "FFXIV Trade Company";
    public const string DefaultDeliveryInstructions =
        "Confirm delivery details with the commission operator.";

    public static string CreateReference(TradeOrder order) =>
        $"CA-{order.CommissionedAtUtc:yyMMdd}-{order.Id.ToString("N")[..6].ToUpperInvariant()}";

    /// <summary>
    /// The brief a new or converted draft starts from: stored materials and payroll
    /// responsibilities, the order's effective payment policy, and the company's default contact.
    /// Without a procurement-route quote, crafter reimbursement stays 0 exactly as the Orders page
    /// shows it.
    /// </summary>
    public static CommissionBriefDocument ComposeNewDraft(
        TradeOrder workPackage,
        TradePayrollWorkflowDraft? payrollDraft,
        TradeCompanyProfile company)
    {
        ArgumentNullException.ThrowIfNull(workPackage);
        ArgumentNullException.ThrowIfNull(company);
        var payment = TradeCommissionPaymentSummary.FromOrder(
            workPackage,
            payrollDraft,
            TradeOrderWorkflow.ResolvePaymentPolicy(workPackage, company.PaymentPolicy),
            requireMaterialRouteQuote: true);
        return Compose(
            workPackage,
            payment,
            new TradeCommissionBriefContext(
                string.IsNullOrWhiteSpace(company.Name) ? DefaultCompanyName : company.Name,
                company.CommissionContact?.Trim() ?? string.Empty,
                DefaultDeliveryInstructions,
                workPackage.PaymentSchedule,
                workPackage.CustomPaymentTerms));
    }

    public static CommissionBriefDocument Compose(
        TradeOrder order,
        TradeCommissionPaymentSummary payment,
        TradeCommissionBriefContext context)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(payment);
        ArgumentNullException.ThrowIfNull(context);
        var source = order.SourceSnapshot ?? new TradeOrderSourceSnapshot();
        var evidenceCapturedAt = payment.Materials
            .Where(material => material.EvidenceTimestampUtc.HasValue)
            .Select(material => material.EvidenceTimestampUtc!.Value)
            .DefaultIfEmpty(source.ImportedAtUtc)
            .Max();

        return new CommissionBriefDocument
        {
            IsTestFixture = context.IsTestFixture,
            CompanyName = context.CompanyName,
            Title = order.Title,
            StatusLabel = context.StatusLabel,
            AssignmentLabel = context.AssignmentLabel,
            Reference = CreateReference(order),
            Contact = context.Contact,
            DeliveryInstructions = string.IsNullOrWhiteSpace(context.DeliveryInstructions)
                ? DefaultDeliveryInstructions
                : context.DeliveryInstructions.Trim(),
            Outputs = (source.RootItems ?? [])
                .Select(output => new CommissionBriefOutput(
                    output.ItemId,
                    output.Name,
                    output.Quantity,
                    output.MustBeHq))
                .ToArray(),
            CrafterMaterials = payment.Materials
                .Where(material => material.Responsibility == CommissionMaterialResponsibility.Crafter)
                .Select(ToBriefMaterial)
                .ToArray(),
            CompanyMaterials = payment.Materials
                .Where(material => material.Responsibility == CommissionMaterialResponsibility.Provided)
                .Select(ToBriefMaterial)
                .ToArray(),
            Payment = new CommissionBriefPayment(
                FormatPaymentContract(payment.Active.Contract),
                payment.Active.MaterialReimbursementTotal,
                0,
                payment.Active.CraftLaborTotal,
                payment.Active.Total,
                payment.Active.CraftSynthCount,
                payment.Active.GilPerSynth,
                context.PaymentSchedule,
                context.CustomPaymentTerms),
            Evidence = new CommissionBriefEvidence(
                source.CostBasis?.ToString() ?? "Unspecified",
                FormatMarketScope(source),
                FormatLocation(source),
                evidenceCapturedAt)
        };
    }

    public static string FormatPaymentContract(TradePaymentContractMode contract) =>
        contract == TradePaymentContractMode.LaborStandard
            ? "Craft labor and material reimbursement"
            : "Agreed payment terms";

    private static string FormatMarketScope(TradeOrderSourceSnapshot source) =>
        source.MarketFetchScope switch
        {
            MarketFetchScope.SelectedDataCenter => "Selected data center",
            MarketFetchScope.EntireRegion => "Entire region",
            _ => "Captured market scope"
        };

    private static string FormatLocation(TradeOrderSourceSnapshot source)
    {
        var values = new[] { source.Region, source.DataCenter, source.World }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        return string.Join(" · ", values) is { Length: > 0 } location ? location : "Unspecified";
    }

    private static CommissionBriefMaterial ToBriefMaterial(TradeCommissionPaymentMaterial material) =>
        new(
            material.ItemId,
            material.Name,
            material.Quantity,
            material.RequiresHq,
            material.UnitCost,
            material.TotalCost);
}
