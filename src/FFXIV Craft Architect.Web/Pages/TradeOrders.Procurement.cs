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
            AppState.CurrentPlanId,
            GetCurrentLiveProcurementSnapshot());
    }

    private bool IsSelectedOrderLinkedPlanActive()
    {
        return _selectedOrder != null &&
            AppState.CurrentPlan != null &&
            !string.IsNullOrWhiteSpace(_selectedOrder.CraftPlanId) &&
            string.Equals(_selectedOrder.CraftPlanId, AppState.CurrentPlanId, StringComparison.Ordinal);
    }

    private AcquisitionEvaluationSnapshot? GetCurrentLiveProcurementSnapshot()
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
        var plan = AppState.CurrentPlan;
        var planSessionVersion = AppState.PlanSessionVersion;
        _isRefreshingLiveProcurement = true;
        try
        {
            var snapshot = await AcquisitionEvaluationWorkflow.BuildCurrentSnapshotAsync(
                plan,
                AppState.ShoppingPlans,
                AppState.UnavailableMarketItems,
                AcquisitionFilter.All);
            if (requestId != _liveProcurementRefreshRequestId ||
                !AppState.IsCurrentPlanSession(plan, planSessionVersion))
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

        var versions = AppState.CurrentVersions;
        return new LiveProcurementKey(
            _selectedOrder.Id,
            AppState.CurrentPlanId ?? string.Empty,
            AppState.PlanSessionVersion,
            versions.PlanStructureVersion,
            versions.PlanDecisionVersion,
            versions.PlanPriceVersion,
            versions.MarketAnalysisVersion);
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
            TradeOrderProcurementColumn.Unit => rows.OrderBy(row => row.UnitCost),
            TradeOrderProcurementColumn.EstimatedCost => rows.OrderBy(row => row.TotalCost),
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
                Header = "Item",
                Size = new WebTableColumnSize(150, 120),
                Sortable = true,
                CellCssClass = "trade-orders-procurement-item-cell",
                CellTemplate = RenderProcurementItemCell
            },
            WebTableColumn<TradeOrderProcurementRow, TradeOrderProcurementColumn>.Text(
                TradeOrderProcurementColumn.Quantity,
                "Qty",
                FormatProcurementQuantity,
                widthPx: 82,
                minWidthPx: 72,
                alignEnd: true,
                cellCssClass: "trade-orders-procurement-quantity",
                headerTooltip: "Active procurement quantity for live linked plans; total quantity for saved snapshots."),
            new WebTableColumn<TradeOrderProcurementRow, TradeOrderProcurementColumn>
            {
                Id = TradeOrderProcurementColumn.Source,
                Header = "Source",
                Size = new WebTableColumnSize(150, 120),
                Sortable = true,
                SuppressRowActivation = true,
                CellTemplate = RenderProcurementSourceCell
            },
            WebTableColumn<TradeOrderProcurementRow, TradeOrderProcurementColumn>.Text(
                TradeOrderProcurementColumn.Unit,
                "Unit",
                row => FormatGil(row.UnitCost),
                widthPx: 76,
                minWidthPx: 68,
                alignEnd: true,
                cellCssClass: "trade-orders-procurement-cost"),
            WebTableColumn<TradeOrderProcurementRow, TradeOrderProcurementColumn>.Text(
                TradeOrderProcurementColumn.EstimatedCost,
                "Est. Cost",
                row => FormatGil(row.TotalCost),
                widthPx: 112,
                minWidthPx: 96,
                alignEnd: true,
                cellCssClass: "trade-orders-procurement-cost"),
            new WebTableColumn<TradeOrderProcurementRow, TradeOrderProcurementColumn>
            {
                Id = TradeOrderProcurementColumn.Responsibility,
                Header = "Resp.",
                Size = new WebTableColumnSize(114, 104),
                Sortable = true,
                CellTemplate = RenderProcurementResponsibilityCell
            }
        ];
    }

    private RenderFragment<TradeOrderProcurementRow> RenderProcurementItemCell => row => builder =>
    {
        var sequence = 0;
        builder.OpenElement(sequence++, "button");
        builder.AddAttribute(sequence++, "type", "button");
        builder.AddAttribute(sequence++, "class", "trade-orders-link-button");
        builder.AddAttribute(sequence++, "onclick", EventCallback.Factory.Create(this, () => OpenProcurementRowDetailsAsync(row)));
        builder.AddContent(sequence++, row.ItemName);
        builder.CloseElement();
        if (row.RequiresHq)
        {
            builder.OpenElement(sequence++, "span");
            builder.AddAttribute(sequence++, "class", "trade-orders-hq");
            builder.AddContent(sequence++, "HQ");
            builder.CloseElement();
        }
    };

    private RenderFragment<TradeOrderProcurementRow> RenderProcurementResponsibilityCell => row => builder =>
    {
        var sequence = 0;
        if (row.IsLiveAcquisitionRow && !row.IsActiveProcurement)
        {
            builder.OpenElement(sequence++, "span");
            builder.AddAttribute(sequence++, "class", "trade-orders-procurement-reference");
            builder.AddContent(sequence++, row.IsFullySuppressed ? "Suppressed" : "Reference");
            builder.CloseElement();
            return;
        }

        builder.AddContent(sequence++, FormatResponsibility(row.Responsibility));
    };

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

    private Task OpenProcurementRowDetailsAsync(TradeOrderProcurementRow row)
    {
        return row.IsLiveAcquisitionRow &&
            (!row.IsActiveProcurement || row.Source == AcquisitionSource.Craft)
                ? OpenAcquisitionEvaluationForProcurementRowAsync(row)
                : OpenMarketAnalysisForProcurementRowAsync(row);
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

        var plan = AppState.CurrentPlan;
        if (plan == null)
        {
            Snackbar.Add("Linked craft plan could not be loaded.", Severity.Warning);
            return;
        }

        var beforeProjection = RecipeLayerWorkflowService.BuildDemandProjection(plan);
        var matchingNodes = FindPlanNodesByItemId(AppState.CurrentPlan, row.ItemId).ToArray();
        if (matchingNodes.Length == 0)
        {
            Snackbar.Add("This material could not be found in the linked craft plan.", Severity.Warning);
            return;
        }

        var node = matchingNodes[0];
        var previousSource = node.Source;
        if (!AcquisitionPlanningService.CanUseAcquisitionSource(node, source))
        {
            Snackbar.Add($"{RecipePlanDisplayHelpers.GetSourceDisplayName(source)} is not available for {row.ItemName}.", Severity.Warning);
            return;
        }

        var change = AcquisitionDecisionService.ChangeSource(node, source);
        if (!change.Changed)
        {
            Snackbar.Add("Acquisition source was already set.", Severity.Info);
            return;
        }

        var afterProjection = RecipeLayerWorkflowService.BuildDemandProjection(plan);
        var marketItemIdsToRefresh = AcquisitionSourceChangeImpactService.GetMarketRefreshItemIds(
            beforeProjection,
            afterProjection,
            row.ItemId,
            previousSource,
            source);
        var savedAt = DateTime.UtcNow;
        var planSaved = await PlanPersistence.SaveGeneratedOrderPlanAsync(
            _selectedOrder.CraftPlanId!,
            _selectedOrder.CraftPlanName ?? TradeOrderWorkflow.CreateGeneratedCraftPlanName(_selectedOrder),
            plan,
            GetOrderRootItems(_selectedOrder),
            savedAt);
        if (!planSaved)
        {
            Snackbar.Add("Source changed, but failed to save the linked craft plan.", Severity.Error);
            return;
        }

        var pricingResult = await TradeOrderPricingWorkflow.RepriceActivePlanAsync(
            _selectedOrder,
            marketItemIdsToRefresh);
        var orderToSave = pricingResult.UpdatedOrder ?? BuildFallbackOrderAfterSourceChange(_selectedOrder, savedAt);
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

    private TradeOrder BuildFallbackOrderAfterSourceChange(TradeOrder order, DateTime savedAt)
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
            RecipeLayerWorkflowService.BuildActiveProcurementItems(AppState.CurrentPlan),
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

    private static IEnumerable<PlanNode> FindPlanNodesByItemId(CraftingPlan? plan, int itemId)
    {
        if (plan == null)
        {
            yield break;
        }

        foreach (var root in plan.RootItems)
        {
            foreach (var node in FindPlanNodesByItemId(root, itemId))
            {
                yield return node;
            }
        }
    }

    private static IEnumerable<PlanNode> FindPlanNodesByItemId(PlanNode node, int itemId)
    {
        if (node.ItemId == itemId)
        {
            yield return node;
        }

        foreach (var child in node.Children)
        {
            foreach (var match in FindPlanNodesByItemId(child, itemId))
            {
                yield return match;
            }
        }
    }

}
