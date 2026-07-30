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
    private TradeRequestedOrderOutput[] ToRequestedOrderOutputs()
    {
        return _requestedOrderOutputs
            .Where(output => output.Quantity > 0)
            .Select(output => new TradeRequestedOrderOutput(
                output.ItemId,
                output.Name,
                output.Quantity,
                output.MustBeHq,
                EstimatedSaleValue: 0m))
            .ToArray();
    }

    private TradeCommissionPaymentSummary GetSelectedOrderPaymentSummary()
    {
        if (_selectedOrder == null)
        {
            return TradeCommissionPaymentSummary.FromOrder(
                new TradeOrder(),
                draft: null,
                effectivePolicy: _companyProfile?.PaymentPolicy);
        }

        return TradeCommissionPaymentSummary.FromOrder(
            _selectedOrder,
            GetPayrollDraftForOrder(_selectedOrder),
            GetSelectedOrderEffectivePaymentPolicy());
    }

    private IReadOnlyList<TradeOrderProcurementRow> GetSelectedOrderProcurementRows()
    {
        if (_selectedOrder == null)
        {
            return Array.Empty<TradeOrderProcurementRow>();
        }

        return TradeProcurementRowBuilder.BuildRows(
            _selectedOrder,
            GetPayrollDraftForOrder(_selectedOrder),
            WorkerProjections.Shell.PlanId,
            GetCurrentLiveProcurementSnapshot());
    }

    private bool IsSelectedOrderLinkedPlanActive()
    {
        return _selectedOrder != null &&
            WorkerProjections.Shell.HasSession &&
            !string.IsNullOrWhiteSpace(_selectedOrder.CraftPlanId) &&
            string.Equals(
                _selectedOrder.CraftPlanId,
                WorkerProjections.Shell.PlanId,
                StringComparison.Ordinal);
    }

    private WorkerTradeProjection? GetCurrentLiveProcurementSnapshot()
    {
        var key = CreateLiveProcurementKey();
        return key.HasValue && key.Value.Equals(_liveProcurementKey)
            ? _liveProcurementSnapshot
            : null;
    }

    private async Task EnsureLiveProcurementSnapshotAsync()
    {
        var key = CreateLiveProcurementKey();
        if (!key.HasValue)
        {
            ClearLiveProcurementSnapshot();
            return;
        }

        if (key.Value.Equals(_liveProcurementKey) ||
            _isRefreshingLiveProcurement)
        {
            return;
        }

        var requestId = Interlocked.Increment(ref _liveProcurementRefreshRequestId);
        _isRefreshingLiveProcurement = true;
        try
        {
            var snapshot = await WorkerSession.GetTradeProjectionAsync();
            if (requestId != _liveProcurementRefreshRequestId)
            {
                return;
            }

            var currentKey = CreateLiveProcurementKey();
            if (!currentKey.HasValue || !currentKey.Value.Equals(key.Value))
            {
                return;
            }

            _liveProcurementSnapshot = snapshot;
            _liveProcurementKey = key.Value;
        }
        finally
        {
            _isRefreshingLiveProcurement = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    private void ClearLiveProcurementSnapshot()
    {
        if (_liveProcurementSnapshot == null &&
            !_liveProcurementKey.HasValue)
        {
            return;
        }

        _liveProcurementSnapshot = null;
        _liveProcurementKey = null;
    }

    private LiveProcurementKey? CreateLiveProcurementKey()
    {
        if (!IsSelectedOrderLinkedPlanActive() || _selectedOrder == null)
        {
            return null;
        }

        return new LiveProcurementKey(
            _selectedOrder.Id,
            WorkerProjections.Shell.PlanId ?? string.Empty,
            WorkerProjections.Shell.Revision);
    }

    private IReadOnlyList<TradeOrderProcurementRow> GetOrderedProcurementRows(IReadOnlyList<TradeOrderProcurementRow> rows)
    {
        if (!_procurementSortState.Column.HasValue)
        {
            return rows;
        }

        IOrderedEnumerable<TradeOrderProcurementRow> ordered = _procurementSortState.Column.Value switch
        {
            TradeOrderProcurementColumn.Item => rows.OrderBy(row => row.ItemName, StringComparer.OrdinalIgnoreCase),
            TradeOrderProcurementColumn.Quantity => rows.OrderBy(GetProcurementSortQuantity),
            TradeOrderProcurementColumn.Source => rows.OrderBy(row => row.SourceLabel, StringComparer.OrdinalIgnoreCase),
            TradeOrderProcurementColumn.Cost => rows.OrderBy(row => row.TotalCost),
            TradeOrderProcurementColumn.Responsibility => rows.OrderBy(row => row.Responsibility.ToString(), StringComparer.OrdinalIgnoreCase),
            _ => rows.OrderBy(row => row.ItemName, StringComparer.OrdinalIgnoreCase)
        };

        return (_procurementSortState.Descending ? ordered.Reverse() : ordered).ToArray();
    }

    private IReadOnlyList<WebTableColumn<TradeOrderProcurementRow, TradeOrderProcurementColumn>> GetProcurementColumns()
    {
        return
        [
            new WebTableColumn<TradeOrderProcurementRow, TradeOrderProcurementColumn>
            {
                Id = TradeOrderProcurementColumn.Item,
                Header = "Material",
                Size = new WebTableColumnSize(180, 150),
                Sortable = true,
                CellCssClass = "trade-orders-procurement-item-cell",
                CellTemplate = RenderProcurementItemCell
            },
            WebTableColumn<TradeOrderProcurementRow, TradeOrderProcurementColumn>.Text(
                TradeOrderProcurementColumn.Quantity,
                "Required",
                FormatProcurementQuantity,
                widthPx: 90,
                minWidthPx: 78,
                alignEnd: true,
                cellCssClass: "trade-orders-procurement-quantity",
                headerTooltip: "Active procurement quantity for live linked plans; total quantity for saved snapshots."),
            new WebTableColumn<TradeOrderProcurementRow, TradeOrderProcurementColumn>
            {
                Id = TradeOrderProcurementColumn.Source,
                Header = "Procurement Route",
                Size = new WebTableColumnSize(170, 132),
                Sortable = true,
                SuppressRowActivation = true,
                CellTemplate = RenderProcurementSourceCell
            },
            new WebTableColumn<TradeOrderProcurementRow, TradeOrderProcurementColumn>
            {
                Id = TradeOrderProcurementColumn.Cost,
                Header = "Estimated Cost",
                Size = new WebTableColumnSize(140, 116),
                Sortable = true,
                CellCssClass = "trade-orders-procurement-cost",
                CellTemplate = RenderProcurementCostCell
            },
            new WebTableColumn<TradeOrderProcurementRow, TradeOrderProcurementColumn>
            {
                Id = TradeOrderProcurementColumn.Responsibility,
                Header = "Responsibility",
                Size = new WebTableColumnSize(150, 126),
                Sortable = true,
                SuppressRowActivation = true,
                CellTemplate = RenderProcurementResponsibilityCell
            }
        ];
    }

    private RenderFragment<TradeOrderProcurementRow> RenderProcurementItemCell => row => builder =>
    {
        var sequence = 0;
        builder.OpenElement(sequence++, "div");
        builder.AddAttribute(sequence++, "class", "trade-orders-procurement-item-primary");
        builder.OpenElement(sequence++, "span");
        builder.AddAttribute(sequence++, "class", GetProcurementStatusDotClass(row));
        builder.CloseElement();
        builder.OpenElement(sequence++, "strong");
        builder.AddContent(sequence++, row.ItemName);
        builder.CloseElement();
        if (row.RequiresHq)
        {
            builder.OpenElement(sequence++, "span");
            builder.AddAttribute(sequence++, "class", "trade-orders-hq");
            builder.AddContent(sequence++, "HQ");
            builder.CloseElement();
        }
        builder.CloseElement();

        builder.OpenElement(sequence++, "span");
        builder.AddAttribute(sequence++, "class", GetProcurementEvidenceTextClass(row));
        builder.AddAttribute(sequence++, "title", row.UnitCostExplanation);
        builder.AddContent(sequence++, FormatProcurementEvidence(row));
        builder.CloseElement();
    };

    private static IReadOnlyList<TradeOrderProcurementRow> GetActiveProcurementRows(
        IReadOnlyList<TradeOrderProcurementRow> rows)
    {
        return rows
            .Where(row => !row.IsLiveAcquisitionRow || row.IsActiveProcurement)
            .ToArray();
    }

    private IReadOnlyList<TradeOrderProcurementRow> GetFilteredProcurementRows(
        IReadOnlyList<TradeOrderProcurementRow> rows)
    {
        return rows
            .Where(row => MatchesProcurementFilter(row, _procurementFilter))
            .ToArray();
    }

    private static int CountProcurementRows(
        IReadOnlyList<TradeOrderProcurementRow> rows,
        TradeOrderProcurementFilter filter)
    {
        return rows.Count(row => MatchesProcurementFilter(row, filter));
    }

    private static bool MatchesProcurementFilter(
        TradeOrderProcurementRow row,
        TradeOrderProcurementFilter filter)
    {
        return filter switch
        {
            TradeOrderProcurementFilter.Attention => ProcurementRowNeedsAttention(row),
            TradeOrderProcurementFilter.Crafter =>
                row.Responsibility == CommissionMaterialResponsibility.Crafter,
            TradeOrderProcurementFilter.Company =>
                row.Responsibility == CommissionMaterialResponsibility.Provided,
            _ => true
        };
    }

    private static bool ProcurementRowNeedsAttention(TradeOrderProcurementRow row)
    {
        return row.TotalCost <= 0 ||
            row.HasSuppressedOccurrences ||
            row.Warnings.Count > 0 ||
            !string.Equals(row.EvidenceStatus, "Priced", StringComparison.OrdinalIgnoreCase);
    }

    private void SetProcurementFilter(TradeOrderProcurementFilter filter)
    {
        _procurementFilter = filter;
    }

    private string GetProcurementFilterClass(TradeOrderProcurementFilter filter)
    {
        return _procurementFilter == filter
            ? "trade-orders-procurement-filter is-active"
            : "trade-orders-procurement-filter";
    }

    private string GetProcurementFilterEmptyMessage()
    {
        return _procurementFilter switch
        {
            TradeOrderProcurementFilter.Attention => "No procurement materials need attention.",
            TradeOrderProcurementFilter.Crafter => "No materials are assigned to the crafter.",
            TradeOrderProcurementFilter.Company => "No materials are assigned to the company.",
            _ => "No procurement materials match this view."
        };
    }

    private static string FormatProcurementOutputSummary(TradeOrder order)
    {
        var outputs = GetOrderRootItems(order);
        if (outputs.Count == 0)
        {
            return "No requested outputs";
        }

        var first = outputs[0];
        var firstLabel = $"{first.Name} {TradeDisplayFormatter.FormatQuantity(first.Quantity)}";
        return outputs.Count == 1
            ? firstLabel
            : $"{firstLabel} + {outputs.Count - 1:N0} more";
    }

    private static string FormatProcurementRouteSummary(TradeOrder order, int materialCount)
    {
        var route = HasLinkedCraftPlan(order)
            ? "Crafted from linked plan"
            : "Saved order snapshot";
        return $"{route} | {materialCount:N0} procured {(materialCount == 1 ? "material" : "materials")}";
    }

    private static decimal GetProcurementEstimatedTotal(
        IReadOnlyList<TradeOrderProcurementRow> rows)
    {
        return rows.Sum(row => Math.Max(row.TotalCost, 0m));
    }

    private static string GetProcurementHealthLabel(
        IReadOnlyList<TradeOrderProcurementRow> rows)
    {
        var attentionCount = rows.Count(ProcurementRowNeedsAttention);
        return attentionCount == 0
            ? "Evidence current"
            : $"{attentionCount:N0} {(attentionCount == 1 ? "line needs" : "lines need")} attention";
    }

    private static string FormatProcurementEvidenceContext(TradeOrder order)
    {
        var scope = string.IsNullOrWhiteSpace(order.SourceSnapshot.DataCenter)
            ? order.SourceSnapshot.Region
            : order.SourceSnapshot.DataCenter;
        var refreshed = order.SourceSnapshot.ImportedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        return string.IsNullOrWhiteSpace(scope)
            ? $"Refreshed {refreshed}"
            : $"{scope} | refreshed {refreshed}";
    }

    private static string FormatProcurementFooter(IReadOnlyList<TradeOrderProcurementRow> rows)
    {
        var attentionCount = rows.Count(ProcurementRowNeedsAttention);
        var state = attentionCount == 0
            ? "all pricing evidence current"
            : $"{attentionCount:N0} {(attentionCount == 1 ? "line needs" : "lines need")} attention";
        return $"{rows.Count:N0} {(rows.Count == 1 ? "material" : "materials")} | {state}";
    }

    private static string GetProcurementStatusDotClass(TradeOrderProcurementRow row)
    {
        return ProcurementRowNeedsAttention(row)
            ? "trade-orders-procurement-status-dot is-attention"
            : "trade-orders-procurement-status-dot";
    }

    private static string GetProcurementEvidenceTextClass(TradeOrderProcurementRow row)
    {
        return ProcurementRowNeedsAttention(row)
            ? "trade-orders-procurement-evidence is-attention"
            : "trade-orders-procurement-evidence";
    }

    private static string FormatProcurementEvidence(TradeOrderProcurementRow row)
    {
        if (!string.IsNullOrWhiteSpace(row.WarningSummary))
        {
            return row.WarningSummary;
        }

        if (!string.IsNullOrWhiteSpace(row.EvidenceSource))
        {
            return $"{row.EvidenceStatus} | {row.EvidenceSource}";
        }

        return row.EvidenceStatus;
    }

    private static string GetProcurementRowClass(TradeOrderProcurementRow row)
    {
        if (!row.IsLiveAcquisitionRow)
        {
            return string.Empty;
        }

        if (row.IsFullySuppressed)
        {
            return "trade-orders-procurement-row is-suppressed";
        }

        if (!row.IsActiveProcurement)
        {
            return "trade-orders-procurement-row is-reference";
        }

        return row.HasSuppressedOccurrences
            ? "trade-orders-procurement-row is-partial"
            : "trade-orders-procurement-row";
    }

    private static string FormatProcurementQuantity(TradeOrderProcurementRow row)
    {
        if (row.IsLiveAcquisitionRow && row.ActiveQuantity != row.Quantity)
        {
            return $"{TradeDisplayFormatter.FormatQuantity(row.ActiveQuantity)} / {TradeDisplayFormatter.FormatQuantity(row.Quantity)}";
        }

        return TradeDisplayFormatter.FormatQuantity(row.Quantity);
    }

    private static int GetProcurementSortQuantity(TradeOrderProcurementRow row)
    {
        return row.IsLiveAcquisitionRow
            ? row.ActiveQuantity
            : row.Quantity;
    }

    private static bool CanEditProcurementSource(TradeOrderProcurementRow row)
    {
        return TradeProcurementSourceMutationPolicy.CanChangeSource(row);
    }

    private async Task ChangeProcurementRowSourceValueAsync(
        TradeOrderProcurementRow row,
        string? value)
    {
        if (!Enum.TryParse<AcquisitionSource>(value, out var source))
        {
            return;
        }

        await ChangeProcurementRowSourceAsync(row, source);
    }

    private async Task ChangeProcurementRowResponsibilityValueAsync(
        TradeOrderProcurementRow row,
        string? value)
    {
        if (!Enum.TryParse<CommissionMaterialResponsibility>(value, out var responsibility))
        {
            return;
        }

        var material = GetSelectedOrderPaymentSummary().Materials.FirstOrDefault(candidate =>
            candidate.ItemId == row.ItemId &&
            candidate.RequiresHq == row.RequiresHq);
        if (material == null)
        {
            Snackbar.Add("This material is missing from the order payment basis.", Severity.Warning);
            return;
        }

        await SetOrderMaterialResponsibilityAsync(material, responsibility);
    }

    private Task OnProcurementSortChanged(WebTableSortState<TradeOrderProcurementColumn> sortState)
    {
        _procurementSortState = sortState;
        return Task.CompletedTask;
    }

    private static IReadOnlyList<AcquisitionSource> GetProcurementSourceOptions()
    {
        return
        [
            AcquisitionSource.Craft,
            AcquisitionSource.MarketBuyNq,
            AcquisitionSource.MarketBuyHq,
            AcquisitionSource.VendorBuy
        ];
    }

    private async Task ChangeProcurementRowSourceAsync(TradeOrderProcurementRow row, AcquisitionSource source)
    {
        if (_selectedOrder == null)
        {
            return;
        }

        if (TradeOrderStatusWorkflow.IsArchived(_selectedOrder.Status))
        {
            Snackbar.Add("Reopen archived orders before editing acquisition sources.", Severity.Warning);
            return;
        }

        if (!TradeProcurementSourceMutationPolicy.CanChangeSource(row))
        {
            Snackbar.Add("Open Acquisition Evaluation to change suppressed or reference rows.", Severity.Warning);
            return;
        }

        if (!await LoadSelectedOrderCraftPlanForNavigationAsync())
        {
            return;
        }

        var live = await WorkerSession.GetTradeProjectionAsync();
        if (live == null || !live.HasPlan)
        {
            Snackbar.Add("Linked craft plan could not be loaded.", Severity.Warning);
            return;
        }

        var liveRow = live.AcquisitionRows.FirstOrDefault(candidate =>
            candidate.ItemId == row.ItemId);
        if (liveRow == null)
        {
            Snackbar.Add("This material could not be found in the linked craft plan.", Severity.Warning);
            return;
        }

        if (!liveRow.AvailableSources.Contains(source))
        {
            Snackbar.Add($"{RecipePlanDisplayHelpers.GetSourceDisplayName(source)} is not available for {row.ItemName}.", Severity.Warning);
            return;
        }

        await WorkerSession.MutateAcquisitionAsync(
            new WorkerAcquisitionMutation(row.ItemId, source, MustBeHq: null));
        var savedAt = DateTime.UtcNow;
        var stored = await WorkerSession.ExportStoredPlanAsync(
            _selectedOrder.CraftPlanId!,
            _selectedOrder.CraftPlanName ?? TradeOrderWorkflow.CreateGeneratedCraftPlanName(_selectedOrder),
            includeSourcePlanIdentity: true);
        if (stored == null || !await PlanPersistence.SaveSnapshotAsync(stored))
        {
            Snackbar.Add("Source changed, but failed to save the linked craft plan.", Severity.Error);
            return;
        }

        var pricingResult = await TradeOrderPricingWorkflow.RepriceActivePlanAsync(
            _selectedOrder,
            source is AcquisitionSource.MarketBuyNq or AcquisitionSource.MarketBuyHq
                ? [row.ItemId]
                : null);
        var current = await WorkerSession.GetTradeProjectionAsync();
        var orderToSave = pricingResult.UpdatedOrder ??
            BuildFallbackOrderAfterSourceChange(_selectedOrder, current, savedAt);
        var savedOrder = await SaveOrderAndNotifyAsync(orderToSave);
        if (!savedOrder)
        {
            Snackbar.Add("Source changed, but failed to save Trade order evidence.", Severity.Error);
            return;
        }

        await LoadAsync();
        if (string.IsNullOrWhiteSpace(_loadError))
        {
            SelectOrderAfterReload(orderToSave.Id, "Source changed, but the order could not be loaded.");
        }

        Snackbar.Add(pricingResult.Message, ToSeverity(pricingResult.MessageLevel));
    }

    private TradeOrder BuildFallbackOrderAfterSourceChange(
        TradeOrder order,
        WorkerTradeProjection? source,
        DateTime savedAt)
    {
        var outputs = GetOrderRootItems(order)
            .Select(item => new TradeRequestedOrderOutput(
                item.ItemId,
                item.Name,
                item.Quantity,
                item.MustBeHq,
                item.EstimatedSaleValue))
            .ToArray();
        var orderToSave = TradeOrderWorkflow.CopyOrder(order);
        orderToSave.SourceSnapshot.Materials = TradeRequestedOrderWorkflow.BuildMaterialSnapshots(
            source?.ActiveProcurementItems ?? Array.Empty<MaterialAggregate>(),
            outputs);
        orderToSave.SourceSnapshot.Warnings = AppendDistinctWarning(
            orderToSave.SourceSnapshot.Warnings,
            "Acquisition source changed, but automatic repricing did not complete. Reprice the order before using payment totals.");
        orderToSave.SourceSnapshot.ImportedAtUtc = savedAt;
        orderToSave.UpdatedAtUtc = savedAt;
        return orderToSave;
    }

    private static Severity ToSeverity(RecipePlannerCommandMessageLevel level)
    {
        return level switch
        {
            RecipePlannerCommandMessageLevel.Success => Severity.Success,
            RecipePlannerCommandMessageLevel.Info => Severity.Info,
            RecipePlannerCommandMessageLevel.Error => Severity.Error,
            _ => Severity.Warning
        };
    }

    private static IReadOnlyList<string> AppendDistinctWarning(
        IReadOnlyList<string>? warnings,
        string warning)
    {
        return (warnings ?? Array.Empty<string>())
            .Append(warning)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

}
