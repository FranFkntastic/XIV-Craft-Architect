using System.Text;

using FFXIV_Craft_Architect.Core.Helpers;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Core.Services.Interfaces;
using FFXIV_Craft_Architect.Web.Dialogs;
using FFXIV_Craft_Architect.Web.Services;
using FFXIV_Craft_Architect.Web.Shared.TablePrimitives;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

using MudBlazor;

namespace FFXIV_Craft_Architect.Web.Pages;

public partial class TradeOrders
{
    private TradePayrollWorkflowDraft? GetPayrollDraftForOrder(TradeOrder order)
    {
        return _payrollDrafts.FirstOrDefault(draft =>
                !string.IsNullOrWhiteSpace(order.PayrollDraftId) &&
                string.Equals(draft.Id, order.PayrollDraftId, StringComparison.OrdinalIgnoreCase))
            ?? _payrollDrafts.FirstOrDefault(draft => draft.OrderId == order.Id);
    }

    private static string FormatMaterialCost(TradeCommissionPaymentMaterial material)
    {
        if (material.UnitCost <= 0 || material.TotalCost <= 0)
        {
            return "Not priced";
        }

        return FormatGil(material.TotalCost);
    }

    private static string FormatMaterialUnitCost(TradeCommissionPaymentMaterial material)
    {
        return material.UnitCost > 0 ? FormatGil(material.UnitCost) : "Not priced";
    }

    private static string FormatPaymentContract(TradePaymentContractMode mode)
    {
        return TradeOrderPaymentCopyFormatter.FormatPaymentContract(mode);
    }

    private static string FormatPaymentBreakdown(TradePaymentContractBreakdown breakdown)
    {
        return TradeOrderPaymentCopyFormatter.FormatPaymentBreakdown(breakdown);
    }

    private static string FormatActiveMaterialAdjustmentLabel(TradePaymentContractMode contract)
    {
        return contract == TradePaymentContractMode.LaborStandard
            ? "Labor material value bonus"
            : "Legacy material commission";
    }

    private static string FormatPaymentDifference(
        TradePaymentContractBreakdown laborStandard,
        TradePaymentContractBreakdown legacy)
    {
        return TradeOrderPaymentCopyFormatter.FormatPaymentDifference(laborStandard, legacy);
    }

    private static string FormatCraftLaborBasis(TradePaymentContractBreakdown breakdown)
    {
        return TradeOrderPaymentDisplayFormatter.FormatCraftLaborBasis(breakdown);
    }

    private static string FormatMaterialPaymentImpact(TradeCommissionPaymentMaterial material)
    {
        return TradeOrderPaymentDisplayFormatter.FormatMaterialPaymentImpact(material);
    }

    private static string FormatResponsibility(CommissionMaterialResponsibility responsibility)
    {
        return responsibility == CommissionMaterialResponsibility.Provided
            ? "Provided"
            : "Crafter procures";
    }

    private static string FormatGil(decimal value)
    {
        return value > 0 ? $"{value:N0} gil" : "Not priced";
    }

    private static string FormatGilAllowZero(decimal value)
    {
        return value >= 0 ? $"{value:N0} gil" : "Not priced";
    }

    private TradeOrderPaymentCopyContext CreateOrderPaymentCopyContext(TradeOrder order)
    {
        var summary = TradeCommissionPaymentSummary.FromOrder(
            order,
            GetPayrollDraftForOrder(order),
            GetOrderEffectivePaymentPolicy(order));
        return new TradeOrderPaymentCopyContext(
            order.Title,
            FormatAssignedCrafter(order),
            GetOrderRootItems(order)
                .Select(item => new TradeOrderPaymentOutput(item.Name, item.Quantity, item.MustBeHq))
                .ToArray(),
            summary);
    }

    private async Task CopyTextToClipboardAsync(string text, string successMessage)
    {
        try
        {
            await JSRuntime.InvokeVoidAsync("navigator.clipboard.writeText", text);
            Snackbar.Add(successMessage, Severity.Success);
        }
        catch
        {
            Snackbar.Add("Failed to copy to clipboard.", Severity.Error);
        }
    }

    private sealed record OrderStatusGroup(TradeOrderStatus Status, IReadOnlyList<TradeOrder> Orders);

    private readonly record struct LiveProcurementKey(
        Guid OrderId,
        string PlanId,
        long PlanSessionVersion,
        long PlanStructureVersion,
        long PlanDecisionVersion,
        long PlanPriceVersion,
        long MarketAnalysisVersion);

    private enum TradeOrderProcurementColumn
    {
        Item,
        Quantity,
        Source,
        Unit,
        EstimatedCost,
        Responsibility
    }

    private sealed class RequestedOrderOutputEditor
    {
        public int ItemId { get; init; }
        public string Name { get; init; } = string.Empty;
        public int Quantity { get; set; } = 1;
        public bool MustBeHq { get; set; }
    }
}
