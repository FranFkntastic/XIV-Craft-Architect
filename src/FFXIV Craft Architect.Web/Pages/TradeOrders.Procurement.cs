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

        var workPackage = GetSelectedOrderPricingWorkPackage();
        return TradeCommissionPaymentSummary.FromOrder(
            workPackage,
            GetSelectedOrderResponsibilityProjection(),
            GetOrderEffectivePaymentPolicy(workPackage));
    }

    private IReadOnlyList<TradeOrderProcurementRow> GetSelectedOrderProcurementRows()
    {
        if (_selectedOrder == null)
        {
            return Array.Empty<TradeOrderProcurementRow>();
        }

        var workPackage = GetSelectedOrderPricingWorkPackage();
        var useCanonicalTerms = HasCanonicalCommission && !CanEditCanonicalDraft;
        return TradeProcurementRowBuilder.BuildRows(
            workPackage,
            GetSelectedOrderResponsibilityProjection(),
            useCanonicalTerms ? null : WorkerProjections.Shell.PlanId,
            useCanonicalTerms ? null : GetCurrentLiveProcurementSnapshot());
    }

    private TradeOrder GetSelectedOrderPricingWorkPackage()
    {
        if (_selectedOrder == null || IsEditingCommissionTermsRevision)
        {
            return _selectedOrder ?? new TradeOrder();
        }

        var commission = SelectedCanonicalCommission ?? _selectedOrder.CompanyCommission;
        return commission?.PublicMetadata.ViewState == CompanyCommissionPublicViewState.Published
            ? CreateCanonicalTermsWorkPackage(_selectedOrder, commission.CurrentTerms)
            : _selectedOrder;
    }

    private TradePayrollWorkflowDraft? GetSelectedOrderResponsibilityProjection()
    {
        if (_selectedOrder == null)
        {
            return null;
        }

        var stored = GetPayrollDraftForOrder(_selectedOrder);
        var commission = SelectedCanonicalCommission ?? _selectedOrder.CompanyCommission;
        if (commission == null)
        {
            return stored;
        }

        if (IsEditingCommissionTermsRevision && _commissionTermsRevisionBrief != null)
        {
            var staged = stored == null
                ? new TradePayrollWorkflowDraft
                {
                    CompanyProfileId = _selectedOrder.CompanyProfileId,
                    OrderId = _selectedOrder.Id
                }
                : TradeOrderWorkflow.CopyPayrollDraft(stored);
            staged.Responsibilities = _commissionTermsRevisionBrief.CrafterMaterials
                .Select(material => new TradePayrollResponsibilityLine(
                    material.ItemId,
                    material.RequiresHq,
                    CommissionMaterialResponsibility.Crafter))
                .Concat(_commissionTermsRevisionBrief.CompanyMaterials.Select(material =>
                    new TradePayrollResponsibilityLine(
                        material.ItemId,
                        material.RequiresHq,
                        CommissionMaterialResponsibility.Provided)))
                .ToArray();
            return staged;
        }

        var projection = stored == null
            ? new TradePayrollWorkflowDraft
            {
                CompanyProfileId = _selectedOrder.CompanyProfileId,
                OrderId = _selectedOrder.Id
            }
            : TradeOrderWorkflow.CopyPayrollDraft(stored);
        projection.Responsibilities = commission.CurrentTerms.Materials
            .Select(material => new TradePayrollResponsibilityLine(
                material.ItemId,
                material.RequiresHq,
                material.Responsibility))
            .ToArray();
        return projection;
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

    private Task OpenSupplyPlanAsync() => SetActiveOpsTabAsync(ProcurementTabIndex);

    private async Task SetActiveOpsTabAsync(int tabIndex)
    {
        _activeOpsTab = tabIndex;
        if (tabIndex != ProcurementTabIndex ||
            _selectedOrder == null ||
            IsSelectedOrderLinkedPlanActive() ||
            _isLoadingSelectedOrderSupplyPlan)
        {
            return;
        }

        _isLoadingSelectedOrderSupplyPlan = true;
        try
        {
            if (!await LoadSelectedOrderCraftPlanForNavigationAsync())
            {
                return;
            }

            ClearLiveProcurementSnapshot();
            await EnsureLiveProcurementSnapshotAsync();
        }
        finally
        {
            _isLoadingSelectedOrderSupplyPlan = false;
        }
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
            return rows
                .OrderBy(row => IsSupplyPrecraftRow(row) ? 0 : 1)
                .ThenBy(row => row.UsedIn, StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => row.ItemName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
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
                Header = "Item",
                Size = new WebTableColumnSize(170, 140),
                Sortable = true,
                CellCssClass = "trade-orders-procurement-item-cell",
                CellTemplate = RenderProcurementItemCell
            },
            WebTableColumn<TradeOrderProcurementRow, TradeOrderProcurementColumn>.Text(
                TradeOrderProcurementColumn.Quantity,
                "Required",
                FormatProcurementQuantity,
                widthPx: 78,
                minWidthPx: 68,
                alignEnd: true,
                cellCssClass: "trade-orders-procurement-quantity",
                headerTooltip: "Required quantity for precrafts; active procurement quantity for leaf materials."),
            new WebTableColumn<TradeOrderProcurementRow, TradeOrderProcurementColumn>
            {
                Id = TradeOrderProcurementColumn.Source,
                Header = "Acquisition",
                Size = new WebTableColumnSize(150, 118),
                Sortable = true,
                SuppressRowActivation = true,
                CellTemplate = RenderProcurementSourceCell
            },
            new WebTableColumn<TradeOrderProcurementRow, TradeOrderProcurementColumn>
            {
                Id = TradeOrderProcurementColumn.Responsibility,
                Header = "Responsibility",
                Size = new WebTableColumnSize(140, 116),
                Sortable = true,
                SuppressRowActivation = true,
                CellTemplate = RenderProcurementResponsibilityCell
            },
            new WebTableColumn<TradeOrderProcurementRow, TradeOrderProcurementColumn>
            {
                Id = TradeOrderProcurementColumn.Cost,
                Header = "Payment Impact",
                Size = new WebTableColumnSize(120, 96),
                Sortable = true,
                CellCssClass = "trade-orders-procurement-cost",
                CellTemplate = RenderProcurementCostCell
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
        if (IsSupplyPrecraftRow(row))
        {
            builder.OpenElement(sequence++, "span");
            builder.AddAttribute(sequence++, "class", "trade-orders-precraft-chip");
            builder.AddContent(sequence++, "Precraft");
            builder.CloseElement();
        }
        builder.CloseElement();

        builder.OpenElement(sequence++, "span");
        builder.AddAttribute(sequence++, "class", GetProcurementEvidenceTextClass(row));
        builder.AddAttribute(sequence++, "title", row.UnitCostExplanation);
        builder.AddContent(sequence++, FormatProcurementEvidence(row));
        builder.CloseElement();
    };

    private IReadOnlyList<TradeOrderProcurementRow> GetSupplyPlanRows(
        IReadOnlyList<TradeOrderProcurementRow> rows)
    {
        return rows
            .Where(row => !IsRequestedOutputReferenceRow(row))
            .Where(TradeProcurementRowBuilder.ShouldIncludePlanRow)
            .ToArray();
    }

    private bool IsRequestedOutputReferenceRow(TradeOrderProcurementRow row)
    {
        return _selectedOrder != null &&
            row.IsLiveAcquisitionRow &&
            GetOrderRootItems(_selectedOrder).Any(output => output.ItemId == row.ItemId);
    }

    private static bool IsSupplyPrecraftRow(TradeOrderProcurementRow row)
    {
        return TradeProcurementRowBuilder.IsPlanPrecraftRow(row);
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
        if (row.IsFullySuppressed)
        {
            return false;
        }

        if (row.Source == AcquisitionSource.OnHand)
        {
            return row.Warnings.Count > 0;
        }

        if (IsSupplyPrecraftRow(row) && row.Source == AcquisitionSource.Craft)
        {
            return row.TotalCost <= 0 || row.Warnings.Count > 0;
        }

        return row.TotalCost <= 0 ||
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

    private static decimal GetProcurementReimbursementTotal(
        IReadOnlyList<TradeOrderProcurementRow> rows)
    {
        return rows
            .Where(row => !row.IsLiveAcquisitionRow || row.IsActiveProcurement)
            .Where(row =>
                row.Responsibility == CommissionMaterialResponsibility.Crafter &&
                row.Source != AcquisitionSource.OnHand)
            .Sum(row => Math.Max(row.TotalCost, 0m));
    }

    private static decimal GetProcurementMaterialValueTotal(
        IReadOnlyList<TradeOrderProcurementRow> rows)
    {
        return rows
            .Where(row => !row.IsLiveAcquisitionRow || row.IsActiveProcurement)
            .Sum(row => Math.Max(row.TotalCost, 0m));
    }

    private static int CountActiveProcurementRows(IReadOnlyList<TradeOrderProcurementRow> rows)
    {
        return rows.Count(row => !row.IsLiveAcquisitionRow || row.IsActiveProcurement);
    }

    private static string FormatCompanyHandoffSummary(IReadOnlyList<TradeOrderProcurementRow> rows)
    {
        var companyRows = rows
            .Where(row => (!row.IsLiveAcquisitionRow || row.IsActiveProcurement) &&
                row.Responsibility == CommissionMaterialResponsibility.Provided)
            .ToArray();
        return companyRows.Length switch
        {
            0 => "None",
            1 => $"{companyRows[0].ItemName} {TradeDisplayFormatter.FormatQuantity(companyRows[0].Quantity)}",
            _ => $"{companyRows.Length:N0} material lines"
        };
    }

    private static string FormatCompanyHandoffDetail(IReadOnlyList<TradeOrderProcurementRow> rows)
    {
        var companyCount = rows.Count(row =>
            (!row.IsLiveAcquisitionRow || row.IsActiveProcurement) &&
            row.Responsibility == CommissionMaterialResponsibility.Provided);
        return companyCount == 0
            ? "Crafter supplies all materials"
            : $"{companyCount:N0} {(companyCount == 1 ? "handoff line" : "handoff lines")} before work begins";
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
        var materialCount = CountActiveProcurementRows(rows);
        var precraftCount = rows.Count(IsSupplyPrecraftRow);
        var state = attentionCount == 0
            ? "all pricing evidence current"
            : $"{attentionCount:N0} {(attentionCount == 1 ? "line needs" : "lines need")} attention";
        var precraft = precraftCount == 0
            ? string.Empty
            : $" | {precraftCount:N0} {(precraftCount == 1 ? "precraft decision" : "precraft decisions")}";
        return $"{materialCount:N0} active {(materialCount == 1 ? "material" : "materials")}{precraft} | {state}";
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
        var routeDescription = TradeProcurementRowBuilder.GetPlanRouteDescription(row);
        if (!string.IsNullOrWhiteSpace(routeDescription))
        {
            return routeDescription;
        }

        if (row.HasSuppressedOccurrences &&
            !row.IsFullySuppressed &&
            row.SuppressedBy.Count > 0)
        {
            return $"Partial demand after {string.Join(", ", row.SuppressedBy)} route";
        }

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

        if (row.Source == AcquisitionSource.OnHand)
        {
            return "trade-orders-procurement-row is-provided";
        }

        if (IsSupplyPrecraftRow(row))
        {
            return "trade-orders-procurement-row is-precraft";
        }

        if (!row.IsActiveProcurement)
        {
            return "trade-orders-procurement-row is-reference";
        }

        return row.HasSuppressedOccurrences
            ? "trade-orders-procurement-row is-partial"
            : "trade-orders-procurement-row";
    }

    private static string GetSupplyPlanRowClass(
        TradeOrderProcurementRow row,
        IReadOnlyList<TradeOrderProcurementRow> rows)
    {
        var classes = new List<string> { "trade-orders-supply-row" };
        var procurementClass = GetProcurementRowClass(row);
        if (!string.IsNullOrWhiteSpace(procurementClass))
        {
            classes.Add(procurementClass);
        }

        if (!IsSupplyPrecraftRow(row) &&
            !row.HasSuppressedOccurrences &&
            rows.Where(IsSupplyPrecraftRow).Any(precraft =>
                row.UsedIn.Contains(precraft.ItemName, StringComparison.OrdinalIgnoreCase)))
        {
            classes.Add("is-child");
        }

        return string.Join(' ', classes);
    }

    private static string FormatProcurementQuantity(TradeOrderProcurementRow row)
    {
        if (IsSupplyPrecraftRow(row))
        {
            return TradeDisplayFormatter.FormatQuantity(row.Quantity);
        }

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

    private bool CanEditProcurementSource(TradeOrderProcurementRow row)
    {
        return !_isCommissionCommandRunning &&
            (!HasCanonicalCommission || CanEditCanonicalDraft) &&
            TradeProcurementSourceMutationPolicy.CanChangeSource(row);
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

        if (responsibility == CommissionMaterialResponsibility.Provided &&
            IsSupplyPrecraftRow(row) &&
            row.Source == AcquisitionSource.Craft)
        {
            var sourceChanged = await ChangeProcurementRowSourceAsync(
                row,
                AcquisitionSource.OnHand,
                showSuccess: false);
            if (!sourceChanged)
            {
                return;
            }

            row = GetSelectedOrderProcurementRows().FirstOrDefault(candidate =>
                candidate.ItemId == row.ItemId &&
                candidate.RequiresHq == row.RequiresHq) ?? row;
        }

        var material = GetSelectedOrderPaymentSummary().Materials.FirstOrDefault(candidate =>
            candidate.ItemId == row.ItemId &&
            candidate.RequiresHq == row.RequiresHq);
        if (material == null)
        {
            Snackbar.Add("This material is missing from the order payment basis.", Severity.Warning);
            return;
        }

        var saved = await SetOrderMaterialResponsibilityAsync(material, responsibility);
        if (saved &&
            responsibility == CommissionMaterialResponsibility.Provided &&
            row.HasChildren)
        {
            Snackbar.Add(
                $"{row.ItemName} is now a company-provided handoff; its recipe subtree is excluded from crafter payment.",
                Severity.Success);
        }
    }

    private Task OnProcurementSortChanged(WebTableSortState<TradeOrderProcurementColumn> sortState)
    {
        _procurementSortState = sortState;
        return Task.CompletedTask;
    }

    private static IReadOnlyList<AcquisitionSource> GetProcurementSourceOptions(TradeOrderProcurementRow row)
    {
        return row.AvailableSources
            .Where(source => source is
                AcquisitionSource.Craft or
                AcquisitionSource.MarketBuyNq or
                AcquisitionSource.MarketBuyHq or
                AcquisitionSource.VendorBuy or
                AcquisitionSource.OnHand)
            .Distinct()
            .ToArray();
    }

    private async Task<bool> ChangeProcurementRowSourceAsync(
        TradeOrderProcurementRow row,
        AcquisitionSource source,
        bool showSuccess = true)
    {
        if (_selectedOrder == null)
        {
            return false;
        }

        if (TradeOrderStatusWorkflow.IsArchived(_selectedOrder.Status))
        {
            Snackbar.Add("Reopen archived orders before editing acquisition sources.", Severity.Warning);
            return false;
        }

        if (_selectedOrder.CompanyCommission != null && !CanEditCanonicalDraft)
        {
            Snackbar.Add(
                "Published acquisition decisions are part of the accepted terms. Use Revise Terms to change them.",
                Severity.Warning);
            return false;
        }

        if (!TradeProcurementSourceMutationPolicy.CanChangeSource(row))
        {
            Snackbar.Add("Open Acquisition Evaluation to change suppressed or reference rows.", Severity.Warning);
            return false;
        }

        if (source == AcquisitionSource.Craft &&
            row.Responsibility == CommissionMaterialResponsibility.Provided)
        {
            Snackbar.Add(
                "Assign this precraft to the crafter before expanding its recipe subtree.",
                Severity.Warning);
            return false;
        }

        if (!await LoadSelectedOrderCraftPlanForNavigationAsync())
        {
            return false;
        }

        var live = await WorkerSession.GetTradeProjectionAsync();
        if (live == null || !live.HasPlan)
        {
            Snackbar.Add("Linked craft plan could not be loaded.", Severity.Warning);
            return false;
        }

        var liveRow = live.AcquisitionRows.FirstOrDefault(candidate =>
            candidate.ItemId == row.ItemId);
        if (liveRow == null)
        {
            Snackbar.Add("This material could not be found in the linked craft plan.", Severity.Warning);
            return false;
        }

        if (!liveRow.AvailableSources.Contains(source))
        {
            Snackbar.Add($"{RecipePlanDisplayHelpers.GetSourceDisplayName(source)} is not available for {row.ItemName}.", Severity.Warning);
            return false;
        }

        var planId = _selectedOrder.CraftPlanId!;
        var planName = _selectedOrder.CraftPlanName ??
            TradeOrderWorkflow.CreateGeneratedCraftPlanName(_selectedOrder);
        var rollbackSnapshot = await WorkerSession.ExportStoredPlanAsync(
            planId,
            planName,
            includeSourcePlanIdentity: true);
        if (rollbackSnapshot == null)
        {
            Snackbar.Add("The linked craft plan could not be staged safely.", Severity.Error);
            return false;
        }

        if (IsEditingCommissionTermsRevision && _commissionTermsRevisionRollbackPlan == null)
        {
            _commissionTermsRevisionRollbackPlan = rollbackSnapshot;
        }

        await WorkerSession.MutateAcquisitionAsync(
            new WorkerAcquisitionMutation(row.ItemId, source, MustBeHq: null));
        var savedAt = DateTime.UtcNow;
        var stored = await WorkerSession.ExportStoredPlanAsync(
            planId,
            planName,
            includeSourcePlanIdentity: true);
        if (stored == null)
        {
            await RestoreStagedProcurementPlanAsync(rollbackSnapshot);
            Snackbar.Add("The changed craft plan could not be captured.", Severity.Error);
            return false;
        }

        var pricingResult = await TradeOrderPricingWorkflow.RepriceActivePlanAsync(
            _selectedOrder,
            source is AcquisitionSource.MarketBuyNq or AcquisitionSource.MarketBuyHq
                ? [row.ItemId]
                : null);
        var current = await WorkerSession.GetTradeProjectionAsync();
        var orderToSave = pricingResult.UpdatedOrder ??
            BuildFallbackOrderAfterSourceChange(_selectedOrder, current, savedAt);
        var savedOrder = _selectedOrder.CompanyCommission == null
            ? await SaveOrderAndNotifyAsync(orderToSave)
            : await UpdateCanonicalDraftAsync(
                orderToSave,
                BuildCommissionBrief(
                    orderToSave,
                    TradeCommissionPaymentSummary.FromOrder(
                        orderToSave,
                        GetSelectedOrderResponsibilityProjection(),
                        GetSelectedOrderEffectivePaymentPolicy())),
                $"{row.ItemName} acquisition saved to the commission draft");
        if (!savedOrder)
        {
            await RestoreStagedProcurementPlanAsync(rollbackSnapshot);
            Snackbar.Add("The acquisition change was not committed.", Severity.Error);
            return false;
        }

        if (!await PlanPersistence.SaveSnapshotAsync(stored))
        {
            Snackbar.Add(
                "The commission draft was saved, but its local craft-plan cache could not be updated. It will rebuild from the canonical order snapshot when reopened.",
                Severity.Warning);
        }

        if (_selectedOrder.CompanyCommission == null)
        {
            await LoadAsync();
            if (string.IsNullOrWhiteSpace(_loadError))
            {
                SelectOrderAfterReload(orderToSave.Id, "Source changed, but the order could not be loaded.");
            }
        }

        if (showSuccess)
        {
            Snackbar.Add(pricingResult.Message, ToSeverity(pricingResult.MessageLevel));
        }

        return true;
    }

    private async Task RestoreStagedProcurementPlanAsync(StoredPlan rollbackSnapshot)
    {
        try
        {
            await PlanLifecycle.ReplaceStoredPlanAsync(
                rollbackSnapshot,
                trackStoredPlanIdentity: true);
            if (!await PlanPersistence.SaveSnapshotAsync(rollbackSnapshot))
            {
                Snackbar.Add(
                    "The previous linked plan could not be restored automatically. Retry the change before using this plan.",
                    Severity.Error);
            }
        }
        catch (Exception ex)
        {
            Snackbar.Add(
                $"The previous linked plan could not be restored automatically: {ex.Message}",
                Severity.Error);
        }
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
