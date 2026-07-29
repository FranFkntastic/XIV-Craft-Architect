using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Web.Dialogs;
using FFXIV_Craft_Architect.Web.Services.TradeCompany;
using Microsoft.JSInterop;
using MudBlazor;

namespace FFXIV_Craft_Architect.Web.Pages;

public partial class TradeOrders
{
    private string _commissionContact = string.Empty;
    private string _commissionDeliveryInstructions = string.Empty;
    private bool _isPublishingCommission;
    private bool _isRevokingCommission;
    private bool _isRetryingCompanyMutation;
    private string? _activeInterestClaimId;
    private readonly Dictionary<string, Guid?> _interestCrafterSelections =
        new(StringComparer.Ordinal);

    private bool HasLiveCommissionPublication =>
        _selectedOrder?.CommissionPublication is { RevokedAtUtc: null };

    private TradeCommissionPublicationProjection? SelectedCompanyPublication =>
        _selectedOrder == null
            ? null
            : TradeCollaboration.GetPublication(_selectedOrder.Id);

    private IReadOnlyList<TradeCommissionInterest> SelectedPendingInterests =>
        _selectedOrder == null
            ? []
            : TradeCollaboration.GetPendingInterests(_selectedOrder.Id);

    private TradeCompanyRecordConflict? SelectedOrderConflict =>
        _selectedOrder == null
            ? null
            : TradeCompanyClient.Conflicts.FirstOrDefault(conflict =>
                string.Equals(
                    conflict.RecordKind,
                    TradeCompanyRecordKinds.Order,
                    StringComparison.Ordinal) &&
                string.Equals(
                    conflict.RecordId,
                    _selectedOrder.Id.ToString("D"),
                    StringComparison.OrdinalIgnoreCase));

    private bool HasActiveCompanyPublication =>
        SelectedCompanyPublication?.State is
            TradeCommissionDeliveryState.Pending or
            TradeCommissionDeliveryState.Published;

    private string PublishCommissionButtonLabel =>
        SelectedCompanyPublication?.State == TradeCommissionDeliveryState.Failed
            ? "Retry Publication"
            : "Publish Commission";

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
                !HasActiveCompanyPublication &&
                GetOrderRootItems(_selectedOrder).Count > 0 &&
                payment.TotalPayment > 0 &&
                payment.Active.IsAvailable;
        }
    }

    private void PrepareCommissionDraft(TradeOrder order)
    {
        _commissionContact = _companyProfile?.CommissionContact ?? string.Empty;
        _commissionDeliveryInstructions = string.Empty;
        foreach (var claim in TradeCollaboration.GetPendingInterests(order.Id))
        {
            _interestCrafterSelections.TryAdd(claim.ClaimId, claim.MatchedCrafterId);
        }
    }

    private async Task PublishSelectedCommissionAsync()
    {
        if (_selectedOrder == null || _companyProfile == null || !CanPublishCommission)
        {
            return;
        }

        var payment = GetSelectedOrderPaymentSummary();
        var discordAvailable = TradeCompanyClient.CanPerformExternalAction(
            _selectedOrder.Id,
            out var discordUnavailableReason);
        var parameters = new DialogParameters
        {
            [nameof(TradeCommissionPublishDialog.Title)] = _selectedOrder.Title,
            [nameof(TradeCommissionPublishDialog.TotalPayment)] = payment.TotalPayment,
            [nameof(TradeCommissionPublishDialog.DiscordAvailable)] = discordAvailable,
            [nameof(TradeCommissionPublishDialog.DiscordUnavailableReason)] =
                discordUnavailableReason
        };
        var options = new DialogOptions { CloseOnEscapeKey = true, MaxWidth = MaxWidth.Small, FullWidth = true };
        var dialog = await DialogService.ShowAsync<TradeCommissionPublishDialog>(
            "Publish Commission",
            parameters,
            options);
        var result = await dialog.Result;
        if (result?.Canceled != false ||
            result.Data is not TradeCommissionPublishDialogResult publishResult)
        {
            return;
        }

        _isPublishingCommission = true;
        try
        {
            if (publishResult.Destination == TradeCommissionDestination.DiscordChannel)
            {
                await PublishSelectedCommissionToDiscordAsync(payment);
                return;
            }

            var orderId = _selectedOrder.Id;
            var ownership = TradeCompanyClient.GetPublicationOwnership(orderId);
            var response = await CommissionBriefs.PublishAsync(
                BuildCommissionBrief(_selectedOrder, payment),
                ownership);
            if (!await CommissionBriefLocalState.SaveEditorTokenAsync(orderId, response.EditorToken))
            {
                Snackbar.Add("The brief was published, but this browser could not retain its revoke capability.", Severity.Warning);
            }

            var orderToSave = TradeOrderWorkflow.CopyOrder(_selectedOrder);
            orderToSave.CommissionPublication = new TradeCommissionPublication
            {
                PublicId = response.PublicId,
                Version = response.Version,
                PublishedAtUtc = response.PublishedAtUtc,
                Ownership = ownership
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

    private async Task PublishSelectedCommissionToDiscordAsync(
        TradeCommissionPaymentSummary payment)
    {
        if (_selectedOrder == null)
        {
            return;
        }

        var orderId = _selectedOrder.Id;
        var result = await TradeCollaboration.PublishToDiscordAsync(
            _selectedOrder,
            BuildCommissionBrief(_selectedOrder, payment));
        if (!result.Success)
        {
            Snackbar.Add(
                result.Publication?.Message ??
                    result.Message ??
                    "Discord publication could not be queued.",
                result.Disposition == TradeCompanyMutationDisposition.Conflict
                    ? Severity.Warning
                    : Severity.Error);
            return;
        }

        await LoadAsync();
        SelectOrderAfterReload(orderId, "The publication was accepted, but the order could not be reloaded.");
        _activeOpsTab = 3;
        Snackbar.Add(
            result.Publication?.State == TradeCommissionDeliveryState.Published
                ? "Commission published to Discord"
                : "Discord publication queued",
            Severity.Success);
    }

    private string GetInterestCrafterValue(TradeCommissionInterest claim)
    {
        if (_interestCrafterSelections.TryGetValue(claim.ClaimId, out var selected))
        {
            return selected?.ToString("D") ?? string.Empty;
        }

        return claim.MatchedCrafterId?.ToString("D") ?? string.Empty;
    }

    private void SetInterestCrafterValue(TradeCommissionInterest claim, string value)
    {
        _interestCrafterSelections[claim.ClaimId] = Guid.TryParse(value, out var crafterId)
            ? crafterId
            : null;
    }

    private async Task AcceptInterestAsync(TradeCommissionInterest claim)
    {
        if (_selectedOrder == null ||
            !_interestCrafterSelections.TryGetValue(claim.ClaimId, out var crafterId) ||
            !crafterId.HasValue)
        {
            Snackbar.Add("Choose an existing company crafter before assigning this interest.", Severity.Warning);
            return;
        }

        _activeInterestClaimId = claim.ClaimId;
        try
        {
            var orderId = _selectedOrder.Id;
            var result = await TradeCollaboration.AcceptInterestAsync(
                _selectedOrder,
                claim,
                crafterId.Value);
            if (!result.Success)
            {
                Snackbar.Add(
                    result.Message ?? "Crafter interest could not be accepted.",
                    result.Disposition == TradeCompanyMutationDisposition.Conflict
                        ? Severity.Warning
                        : Severity.Error);
                return;
            }

            await LoadAsync();
            SelectOrderAfterReload(orderId, "The assignment was accepted, but the order could not be reloaded.");
            _activeOpsTab = 3;
            Snackbar.Add("Crafter interest accepted and order assigned", Severity.Success);
        }
        finally
        {
            _activeInterestClaimId = null;
        }
    }

    private async Task DeclineInterestAsync(TradeCommissionInterest claim)
    {
        if (_selectedOrder == null)
        {
            return;
        }

        _activeInterestClaimId = claim.ClaimId;
        try
        {
            var orderId = _selectedOrder.Id;
            var result = await TradeCollaboration.DeclineInterestAsync(_selectedOrder, claim);
            if (!result.Success)
            {
                Snackbar.Add(
                    result.Message ?? "Crafter interest could not be declined.",
                    result.Disposition == TradeCompanyMutationDisposition.Conflict
                        ? Severity.Warning
                        : Severity.Error);
                return;
            }

            await LoadAsync();
            SelectOrderAfterReload(orderId, "The interest was declined, but the order could not be reloaded.");
            _activeOpsTab = 3;
            Snackbar.Add("Crafter interest declined", Severity.Success);
        }
        finally
        {
            _activeInterestClaimId = null;
        }
    }

    private async Task RetryCompanyMutationsAsync()
    {
        if (_companyProfile == null)
        {
            return;
        }

        _isRetryingCompanyMutation = true;
        try
        {
            var orderId = _selectedOrder?.Id;
            await TradeOrderMutations.RetryPendingAsync();
            await LoadAsync();
            if (orderId.HasValue)
            {
                SelectOrderAfterReload(orderId.Value, "Company sync completed, but the order could not be reloaded.");
                _activeOpsTab = 3;
            }

            Snackbar.Add(
                TradeCompanyClient.Connection.State == TradeCompanyConnectionState.Current
                    ? "Company changes are current"
                    : TradeCompanyClient.Connection.Message ?? "Company changes are still pending.",
                TradeCompanyClient.Connection.State == TradeCompanyConnectionState.Current
                    ? Severity.Success
                    : Severity.Warning);
        }
        finally
        {
            _isRetryingCompanyMutation = false;
        }
    }

    private async Task UseCompanyOrderVersionAsync()
    {
        if (SelectedOrderConflict?.CurrentRecord == null)
        {
            return;
        }

        var orderId = _selectedOrder?.Id;
        if (!await TradeOrderMutations.AcceptRemoteConflictAsync(SelectedOrderConflict.CurrentRecord))
        {
            Snackbar.Add("The company order could not be applied locally.", Severity.Error);
            return;
        }

        await LoadAsync();
        if (orderId.HasValue)
        {
            SelectOrderAfterReload(orderId.Value, "The company version was applied, but the order could not be reloaded.");
            _activeOpsTab = 3;
        }

        AppState.NotifyTradeOperationsDataChanged();
        Snackbar.Add("Company order version applied", Severity.Success);
    }

    private string FormatCompanyConnectionState() =>
        TradeCompanyClient.Connection.State switch
        {
            TradeCompanyConnectionState.LocalOnly => "Local only",
            TradeCompanyConnectionState.Refreshing => "Refreshing",
            TradeCompanyConnectionState.Current => "Company current",
            TradeCompanyConnectionState.Pending => "Sync pending",
            TradeCompanyConnectionState.Conflict => "Conflict",
            TradeCompanyConnectionState.Unavailable => "Company unavailable",
            _ => "Company unavailable"
        };

    private string GetCompanyConnectionChipClass() =>
        TradeCompanyClient.Connection.State switch
        {
            TradeCompanyConnectionState.Current => "trade-orders-company-chip is-current",
            TradeCompanyConnectionState.Pending => "trade-orders-company-chip is-pending",
            TradeCompanyConnectionState.Conflict => "trade-orders-company-chip is-conflict",
            _ => "trade-orders-company-chip"
        };

    private static string GetCompanyPublicationClass(
        TradeCommissionPublicationProjection publication) =>
        publication.State switch
        {
            TradeCommissionDeliveryState.Published => "trade-orders-collaboration-state is-live",
            TradeCommissionDeliveryState.Failed => "trade-orders-collaboration-state is-failed",
            TradeCommissionDeliveryState.Pending => "trade-orders-collaboration-state is-pending",
            _ => "trade-orders-collaboration-state"
        };

    private static string GetCompanyPublicationChipClass(
        TradeCommissionPublicationProjection publication) =>
        publication.State switch
        {
            TradeCommissionDeliveryState.Published => "trade-orders-publication-chip is-live",
            TradeCommissionDeliveryState.Failed => "trade-orders-publication-chip is-failed",
            TradeCommissionDeliveryState.Pending => "trade-orders-publication-chip is-attention",
            _ => "trade-orders-publication-chip"
        };

    private static string FormatCompanyPublicationDestination(
        TradeCommissionPublicationProjection publication) =>
        publication.Destination == TradeCommissionDestination.DiscordChannel
            ? $"Discord publication · {publication.DestinationLabel ?? "company channel"}"
            : "Public commission link";

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
