using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Web.Dialogs;
using Microsoft.JSInterop;
using MudBlazor;

namespace FFXIV_Craft_Architect.Web.Pages;

public partial class TradeOrders
{
    private string _commissionContact = string.Empty;
    private string _commissionDeliveryInstructions = string.Empty;
    private bool _isPublishingCommission;
    private bool _isRevokingCommission;

    private bool HasLiveCommissionPublication =>
        _selectedOrder?.CommissionPublication is { RevokedAtUtc: null };

    private bool CanPublishCommission
    {
        get
        {
            if (_selectedOrder == null)
            {
                return false;
            }

            var payment = GetSelectedOrderPaymentSummary();
            return !_isPublishingCommission &&
                !HasLiveCommissionPublication &&
                GetOrderRootItems(_selectedOrder).Count > 0 &&
                payment.TotalPayment > 0 &&
                payment.Active.IsAvailable;
        }
    }

    private void PrepareCommissionDraft(TradeOrder order)
    {
        _commissionContact = _companyProfile?.CommissionContact ?? string.Empty;
        _commissionDeliveryInstructions = string.Empty;
    }

    private async Task PublishSelectedCommissionAsync()
    {
        if (_selectedOrder == null || _companyProfile == null || !CanPublishCommission)
        {
            return;
        }

        var payment = GetSelectedOrderPaymentSummary();
        var parameters = new DialogParameters
        {
            [nameof(TradeCommissionPublishDialog.Title)] = _selectedOrder.Title,
            [nameof(TradeCommissionPublishDialog.TotalPayment)] = payment.TotalPayment
        };
        var options = new DialogOptions { CloseOnEscapeKey = true, MaxWidth = MaxWidth.Small, FullWidth = true };
        var dialog = await DialogService.ShowAsync<TradeCommissionPublishDialog>(
            "Publish Commission",
            parameters,
            options);
        var result = await dialog.Result;
        if (result?.Canceled != false)
        {
            return;
        }

        _isPublishingCommission = true;
        try
        {
            var orderId = _selectedOrder.Id;
            var response = await CommissionBriefs.PublishAsync(BuildCommissionBrief(_selectedOrder, payment));
            if (!await CommissionBriefLocalState.SaveEditorTokenAsync(orderId, response.EditorToken))
            {
                Snackbar.Add("The brief was published, but this browser could not retain its revoke capability.", Severity.Warning);
            }

            var orderToSave = TradeOrderWorkflow.CopyOrder(_selectedOrder);
            orderToSave.CommissionPublication = new TradeCommissionPublication
            {
                PublicId = response.PublicId,
                Version = response.Version,
                PublishedAtUtc = response.PublishedAtUtc
            };
            AppendCommissionHistory(
                orderToSave,
                TradeOrderHistoryEventKind.CommissionPublished,
                $"Published crafter brief v{response.Version}.",
                response.PublishedAtUtc);
            if (!await SaveOrderAndNotifyAsync(orderToSave))
            {
                Snackbar.Add("The brief was published, but its link could not be attached to this order.", Severity.Warning);
                return;
            }

            await LoadAsync();
            SelectOrderAfterReload(orderId, "The brief was published, but the order could not be reloaded.");
            _activeOpsTab = 3;
            await CopyTextToClipboardAsync(GetCommissionUrl(response.PublicId), "Commission published and link copied");
        }
        catch (Exception)
        {
            Snackbar.Add("Commission publication failed. The order remains private.", Severity.Error);
        }
        finally
        {
            _isPublishingCommission = false;
        }
    }

    private async Task RevokeSelectedCommissionAsync()
    {
        if (_selectedOrder?.CommissionPublication is not { RevokedAtUtc: null } publication)
        {
            return;
        }

        var editorToken = await CommissionBriefLocalState.LoadEditorTokenAsync(_selectedOrder.Id);
        if (string.IsNullOrWhiteSpace(editorToken))
        {
            Snackbar.Add("This browser no longer has the capability needed to revoke this link.", Severity.Error);
            return;
        }

        _isRevokingCommission = true;
        try
        {
            var orderId = _selectedOrder.Id;
            await CommissionBriefs.RevokeAsync(publication.PublicId, editorToken);
            await CommissionBriefLocalState.ForgetEditorTokenAsync(orderId);
            var orderToSave = TradeOrderWorkflow.CopyOrder(_selectedOrder);
            orderToSave.CommissionPublication!.RevokedAtUtc = DateTime.UtcNow;
            AppendCommissionHistory(
                orderToSave,
                TradeOrderHistoryEventKind.CommissionRevoked,
                $"Revoked crafter brief v{publication.Version}.",
                orderToSave.CommissionPublication.RevokedAtUtc.Value);
            if (!await SaveOrderAndNotifyAsync(orderToSave))
            {
                Snackbar.Add("The link was revoked, but the order could not record that state.", Severity.Warning);
                return;
            }

            await LoadAsync();
            SelectOrderAfterReload(orderId, "The link was revoked, but the order could not be reloaded.");
            _activeOpsTab = 3;
            Snackbar.Add("Commission link revoked", Severity.Success);
        }
        catch (Exception)
        {
            Snackbar.Add("Commission link revocation failed.", Severity.Error);
        }
        finally
        {
            _isRevokingCommission = false;
        }
    }

    private CommissionBriefDocument BuildCommissionBrief(
        TradeOrder order,
        TradeCommissionPaymentSummary payment)
    {
        var source = order.SourceSnapshot ?? new TradeOrderSourceSnapshot();
        var evidenceCapturedAt = payment.Materials
            .Where(material => material.EvidenceTimestampUtc.HasValue)
            .Select(material => material.EvidenceTimestampUtc!.Value)
            .DefaultIfEmpty(source.ImportedAtUtc)
            .Max();

        return new CommissionBriefDocument
        {
            CompanyName = _companyProfile?.Name ?? "FFXIV Trade Company",
            Title = order.Title,
            StatusLabel = order.AssignedCrafterId.HasValue ? "Assigned" : "Open for assignment",
            AssignmentLabel = order.AssignedCrafterId.HasValue ? FormatAssignedCrafter(order) : "Contact operator",
            Reference = $"CA-{order.CommissionedAtUtc:yyMMdd}-{order.Id.ToString("N")[..6].ToUpperInvariant()}",
            Contact = string.IsNullOrWhiteSpace(_commissionContact)
                ? string.Empty
                : _commissionContact.Trim(),
            DeliveryInstructions = string.IsNullOrWhiteSpace(_commissionDeliveryInstructions)
                ? "Confirm delivery details with the commission operator."
                : _commissionDeliveryInstructions.Trim(),
            Outputs = GetOrderRootItems(order)
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
                FormatCommissionPaymentContract(payment.Active.Contract),
                payment.Active.MaterialReimbursementTotal,
                payment.Active.CommissionAmount,
                payment.Active.CraftLaborTotal,
                payment.Active.Total,
                payment.Active.CommissionPercent,
                payment.Active.CraftSynthCount,
                payment.Active.GilPerSynth),
            Evidence = new CommissionBriefEvidence(
                FormatCommissionCostBasis(source),
                FormatCommissionMarketScope(source),
                FormatCommissionLocation(source),
                evidenceCapturedAt)
        };
    }

    private static CommissionBriefMaterial ToBriefMaterial(TradeCommissionPaymentMaterial material) =>
        new(
            material.ItemId,
            material.Name,
            material.Quantity,
            material.RequiresHq,
            material.UnitCost,
            material.TotalCost);

    private string GetCommissionUrl(string publicId) =>
        $"{NavigationManager.BaseUri.TrimEnd('/')}/commission.html?id={Uri.EscapeDataString(publicId)}";

    private async Task CopyCommissionLinkAsync()
    {
        if (_selectedOrder?.CommissionPublication is { RevokedAtUtc: null } publication)
        {
            await CopyTextToClipboardAsync(GetCommissionUrl(publication.PublicId), "Commission link copied");
        }
    }

    private async Task OpenCommissionBriefAsync()
    {
        if (_selectedOrder?.CommissionPublication is { RevokedAtUtc: null } publication)
        {
            await JSRuntime.InvokeVoidAsync("open", GetCommissionUrl(publication.PublicId), "_blank");
        }
    }

    private static string FormatCommissionCostBasis(TradeOrderSourceSnapshot source) =>
        source.CostBasis switch
        {
            CommissionCostBasis.MarketRecommendation => "Market recommendation",
            CommissionCostBasis.SelectedAcquisitionSources => "Selected acquisition sources",
            _ => "Selected acquisition sources"
        };

    private static string FormatCommissionMarketScope(TradeOrderSourceSnapshot source) =>
        source.MarketFetchScope switch
        {
            MarketFetchScope.SelectedDataCenter => "Selected data center",
            MarketFetchScope.EntireRegion => "Entire region",
            _ => "Captured market scope"
        };

    private static string FormatCommissionPaymentContract(TradePaymentContractMode contract) =>
        contract == TradePaymentContractMode.LaborStandard
            ? "Labor standard"
            : "Legacy commission";

    private static string FormatCommissionLocation(TradeOrderSourceSnapshot source)
    {
        var values = new[] { source.Region, source.DataCenter, source.World }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        return string.Join(" · ", values) is { Length: > 0 } location ? location : "Unspecified";
    }

    private static void AppendCommissionHistory(
        TradeOrder order,
        TradeOrderHistoryEventKind kind,
        string note,
        DateTime createdAtUtc)
    {
        order.History = (order.History ?? Array.Empty<TradeOrderHistoryEvent>())
            .Append(new TradeOrderHistoryEvent
            {
                Id = Guid.NewGuid(),
                CompanyProfileId = order.CompanyProfileId,
                OrderId = order.Id,
                Kind = kind,
                Note = note,
                CreatedAtUtc = createdAtUtc
            })
            .ToArray();
    }
}
