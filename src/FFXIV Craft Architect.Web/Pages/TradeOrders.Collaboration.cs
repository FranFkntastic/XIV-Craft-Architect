using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Web.Dialogs;
using FFXIV_Craft_Architect.Web.Services;
using FFXIV_Craft_Architect.Web.Services.ProfileHosting;
using FFXIV_Craft_Architect.Web.Services.TradeCompany;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor;

namespace FFXIV_Craft_Architect.Web.Pages;

public partial class TradeOrders
{
    private string _commissionContact = string.Empty;
    private string _commissionDeliveryInstructions = string.Empty;
    private bool _isPublishingCommission;
    private bool _isRevokingCommission;
    private bool _isReconcilingDiscordPublication;

    private bool HasLiveCommissionPublication =>
        _selectedOrder?.CommissionPublication is { RevokedAtUtc: null };

    private TradeCommissionPublicationProjection? SelectedCompanyPublication =>
        _selectedOrder == null
            ? null
            : TradeCollaboration.GetPublication(_selectedOrder.Id);

    /// <summary>
    /// Why the canonical draft cannot be published yet. Channel availability is chosen in the
    /// delivery dialog, where a portable link remains available without a trade channel.
    /// </summary>
    private string? CanonicalPublishUnavailableReason
    {
        get
        {
            if (HasSelectedLocalHostedCollision)
            {
                return "Resolve the local edit collision before publishing.";
            }

            if (SelectedCommissionOwner?.Order is not { } order ||
                order.CompanyCommission is not { } commission)
            {
                return "The canonical company commission is unavailable.";
            }

            if (commission.PublicMetadata.ViewState != CompanyCommissionPublicViewState.Draft)
            {
                return "The canonical brief has already been published.";
            }

            if (_selectedOrderPaymentTermsDirty)
            {
                return "Save the payment timing before publishing.";
            }

            if (HasActiveCompanyPublication)
            {
                return "Trade channel delivery is already in progress.";
            }

            if (commission.CurrentTerms.Outputs.Count == 0)
            {
                return "Add at least one requested output before publishing.";
            }

            if (TradeOrderWorkflow.IsUnpricedSkeleton(order) ||
                IsSelectedOrderUnpricedSkeleton)
            {
                return "Price the plan before publishing.";
            }

            if (commission.CurrentTerms.Payment.Total <= 0)
            {
                return "Finish the work package and pricing before publishing.";
            }

            if (commission.CurrentTerms.Payment.Schedule ==
                    CompanyCommissionPaymentSchedule.Custom &&
                string.IsNullOrWhiteSpace(commission.CurrentTerms.Payment.CustomTerms))
            {
                return "Define the custom payment timing before publishing.";
            }

            return null;
        }
    }

    private bool CanPublishCanonicalCommission =>
        !_isPublishingCommission &&
        CanonicalPublishUnavailableReason == null;

    private ProfileSyncConflict? SelectedOrderConflict =>
        _selectedOrder == null ? null : FindOrderConflict(_selectedOrder.Id);

    private ProfileSyncConflict? FindOrderConflict(Guid orderId) =>
        ProfileSync.Conflicts.FirstOrDefault(conflict =>
                string.Equals(
                    conflict.Collection,
                    ProfileSyncCollections.TradeOrders,
                    StringComparison.Ordinal) &&
                string.Equals(
                    conflict.ObjectId,
                    orderId.ToString("D"),
                    StringComparison.OrdinalIgnoreCase));

    private bool IsOrderPending(Guid orderId) =>
        ProfileSync.PendingSaves.Any(pending =>
            string.Equals(
                pending.Collection,
                ProfileSyncCollections.TradeOrders,
                StringComparison.Ordinal) &&
            string.Equals(
                pending.ObjectId,
                orderId.ToString("D"),
                StringComparison.OrdinalIgnoreCase));

    private bool IsSelectedOrderPending =>
        _selectedOrder != null && IsOrderPending(_selectedOrder.Id);

    private bool HasActiveCompanyPublication =>
        SelectedCompanyPublication?.State is
            TradeCommissionDeliveryState.Pending or
            TradeCommissionDeliveryState.Published;

    private bool HasFailedCompanyPublication =>
        SelectedCompanyPublication?.State == TradeCommissionDeliveryState.Failed;

    private bool HasCanonicalDraftDetailChanges =>
        _selectedOrder?.CompanyCommission is { } commission &&
        (!string.Equals(
             _commissionContact?.Trim() ?? string.Empty,
             IsEditingCommissionTermsRevision
                 ? _commissionTermsRevisionBrief?.Contact ?? commission.CurrentTerms.ContactInstructions
                 : commission.CurrentTerms.ContactInstructions,
             StringComparison.Ordinal) ||
         !string.Equals(
             _commissionDeliveryInstructions?.Trim() ?? string.Empty,
             IsEditingCommissionTermsRevision
                 ? _commissionTermsRevisionBrief?.DeliveryInstructions ?? commission.CurrentTerms.DeliveryInstructions
                 : commission.CurrentTerms.DeliveryInstructions,
             StringComparison.Ordinal));

    private void PrepareCommissionDraft(TradeOrder order)
    {
        var terms = order.CompanyCommission?.CurrentTerms;
        _commissionContact = terms?.ContactInstructions ??
            _companyProfile?.CommissionContact ??
            string.Empty;
        _commissionDeliveryInstructions = terms?.DeliveryInstructions ?? string.Empty;
        _ = RefreshCollaborationAsync(order);
    }

    private async Task RefreshCollaborationAsync(TradeOrder order)
    {
        if (_companyProfile == null ||
            !ProfileSync.CurrentStatus.IsConnected ||
            !ProfileSync.CurrentStatus.HostReachable)
        {
            return;
        }

        try
        {
            await TradeCollaboration.RefreshAsync(_companyProfile.Id, order.Id);
            await InvokeAsync(StateHasChanged);
        }
        catch (Exception exception)
        {
            await InvokeAsync(() =>
                Snackbar.Add(
                    $"Collaboration refresh failed: {exception.Message}",
                    Severity.Warning));
        }
    }

    private void ActivateSharingTab()
    {
        _activeOpsTab = SharingTabIndex;
        if (_selectedOrder != null)
        {
            ScheduleSelectedCommissionOwnerRefresh(_selectedOrder);
        }
    }

    /// <summary>
    /// Publishes the canonical commission draft. The delivery dialog chooses the company trade
    /// channel or a portable crafter link, and can mark the brief as a test fixture. Both
    /// destinations commit through the host and the browser adopts the committed order.
    /// </summary>
    private async Task PublishCanonicalCommissionAsync()
    {
        if (!CanPublishCanonicalCommission || _companyProfile == null)
        {
            return;
        }

        _isPublishingCommission = true;
        TradeOrder? publishing = null;
        try
        {
            if (HasFailedCompanyPublication)
            {
                await RetrySelectedCommissionToDiscordAsync();
                return;
            }

            if (HasCanonicalDraftDetailChanges &&
                !await TrySaveCanonicalCommissionDraftDetailsAsync(showSuccess: false))
            {
                return;
            }

            var owner = SelectedCommissionOwner;
            if (owner?.Order.CompanyCommission is not { } commission)
            {
                throw new InvalidOperationException(
                    "The canonical commission changed before publication.");
            }

            var discordAvailable = CanPublishToConfiguredTradeChannel(
                owner.Order,
                out var discordUnavailableReason);
            var parameters = new DialogParameters
            {
                [nameof(TradeCommissionPublishDialog.DiscordAvailable)] = discordAvailable,
                [nameof(TradeCommissionPublishDialog.DiscordUnavailableReason)] =
                    discordUnavailableReason
            };
            var dialog = await DialogService.ShowAsync<TradeCommissionPublishDialog>(
                "Publish Commission",
                parameters,
                new DialogOptions { CloseOnEscapeKey = true, MaxWidth = MaxWidth.Small, FullWidth = true });
            var result = await dialog.Result;
            if (result?.Canceled != false ||
                result.Data is not TradeCommissionPublishDialogResult publishResult)
            {
                return;
            }

            owner = SelectedCommissionOwner;
            if (owner?.Order.CompanyCommission is not { } current ||
                current.PublicMetadata.ViewState != CompanyCommissionPublicViewState.Draft)
            {
                throw new InvalidOperationException(
                    "The canonical commission changed before publication.");
            }

            var brief = BuildCanonicalCommissionBrief(owner.Order, current);
            brief.IsTestFixture = publishResult.IsTestFixture;
            publishing = owner.Order;
            if (publishResult.Destination == TradeCommissionDestination.DiscordChannel)
            {
                await PublishSelectedCommissionToDiscordAsync(owner.Order, brief);
                return;
            }

            var orderId = owner.Order.Id;
            var link = await TradeCollaboration.PublishPortableLinkAsync(owner.Order, brief);
            var outcome = await ConfirmPublishedCommissionAsync(owner.Order);
            await ShowPublishedCommissionAsync(orderId);
            await CopyTextToClipboardAsync(
                link.Url,
                outcome.Outcome == PublishedCommissionOutcome.Published
                    ? "Commission published and link copied"
                    : "Commission published and link copied; status still confirming");
        }
        catch (TradeCompanyPublicationRefusedException exception)
        {
            Snackbar.Add(exception.Message, Severity.Error);
        }
        catch (Exception exception) when (publishing != null && exception is not OperationCanceledException)
        {
            // The host may have committed before this browser failed; only a commission that is
            // still a draft on the host is reported as a failed publication.
            await ReportPublicationExceptionAsync(publishing, exception);
        }
        catch (Exception exception)
        {
            Snackbar.Add(
                $"Commission publication failed: {exception.Message}",
                Severity.Error);
        }
        finally
        {
            _isPublishingCommission = false;
        }
    }

    private enum PublishedCommissionOutcome
    {
        Published,
        StillDraft,
        Unconfirmed
    }

    private static readonly TimeSpan[] PublishedCommissionConfirmDelays =
    [
        TimeSpan.Zero,
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(3)
    ];

    /// <summary>
    /// Adopts the host's committed commission straight from the host after publication (no
    /// profile sync involved), retrying briefly, so this browser never keeps showing the
    /// pre-publication draft. Unconfirmed outcomes are logged with their last error.
    /// </summary>
    private async Task<(PublishedCommissionOutcome Outcome, string? Detail)> ConfirmPublishedCommissionAsync(
        TradeOrder order)
    {
        string? lastError = null;
        foreach (var delay in PublishedCommissionConfirmDelays)
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay);
            }
            try
            {
                var projection = await CommissionOperations.RefreshOwnerAsync(order);
                return projection.Order.CompanyCommission?.PublicMetadata.ViewState ==
                       CompanyCommissionPublicViewState.Draft
                    ? (PublishedCommissionOutcome.StillDraft, null)
                    : (PublishedCommissionOutcome.Published, null);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                lastError = exception.Message;
            }
        }

        Logger.LogWarning(
            "The publication outcome of commission {OrderId} could not be confirmed ({Code}) after {Attempts} attempts: {Message}",
            order.Id,
            "publication_status_unconfirmed",
            PublishedCommissionConfirmDelays.Length,
            lastError);
        return (PublishedCommissionOutcome.Unconfirmed, lastError);
    }

    private async Task ShowPublishedCommissionAsync(Guid orderId)
    {
        AppState.NotifyTradeOperationsDataChanged();
        await LoadAsync();
        SelectOrderAfterReload(orderId, "The commission was published, but the order could not be reloaded.");
        ActivateSharingTab();
    }

    /// <summary>
    /// A publish call failed in this browser. The host decides what happened: a published
    /// commission is adopted and reported quietly, only a commission still in draft is a failure.
    /// </summary>
    private async Task ReportPublicationExceptionAsync(TradeOrder order, Exception exception)
    {
        var (outcome, detail) = await ConfirmPublishedCommissionAsync(order);
        switch (outcome)
        {
            case PublishedCommissionOutcome.Published:
                Logger.LogWarning(
                    "Commission {OrderId} was published, but this browser failed afterwards: {Message}",
                    order.Id,
                    exception.Message);
                await ShowPublishedCommissionAsync(order.Id);
                Snackbar.Add("Commission published; status still confirming.", Severity.Info);
                break;
            case PublishedCommissionOutcome.StillDraft:
                Snackbar.Add(
                    $"Commission publication failed: {exception.Message}",
                    Severity.Error);
                break;
            default:
                Snackbar.Add(
                    $"Publication status still confirming (publication_status_unconfirmed): {exception.Message} {detail}".TrimEnd(),
                    Severity.Info);
                break;
        }
    }

    private async Task SaveCanonicalCommissionDraftDetailsAsync() =>
        await TrySaveCanonicalCommissionDraftDetailsAsync(showSuccess: true);

    private async Task<bool> TrySaveCanonicalCommissionDraftDetailsAsync(
        bool showSuccess = true)
    {
        if (!EnsureHostedOrderMutationAvailable())
        {
            return false;
        }

        var owner = SelectedCommissionOwner;
        var commission = owner?.Order.CompanyCommission;
        if (owner == null || commission == null || !CanEditCanonicalWorkPackage)
        {
            return false;
        }

        var workPackage = IsEditingCommissionTermsRevision
            ? _commissionTermsRevisionWorkPackage ?? _selectedOrder!
            : owner.Order;
        var brief = IsEditingCommissionTermsRevision
            ? BuildCommissionBrief(
                workPackage,
                TradeCommissionPaymentSummary.FromOrder(
                    workPackage,
                    GetSelectedOrderResponsibilityProjection(),
                    GetSelectedOrderEffectivePaymentPolicy()))
            : BuildCanonicalCommissionBrief(owner.Order, commission);
        brief.Contact = _commissionContact?.Trim() ?? string.Empty;
        brief.DeliveryInstructions = _commissionDeliveryInstructions?.Trim() ?? string.Empty;
        return await UpdateCanonicalDraftAsync(
            workPackage,
            brief,
            showSuccess
                ? "Crafter-facing details saved to the commission draft"
                : null);
    }

    private async Task PublishSelectedCommissionToDiscordAsync(
        TradeOrder order,
        CommissionBriefDocument brief)
    {
        var orderId = order.Id;
        var result = await TradeCollaboration.PublishToDiscordAsync(
            order,
            brief);
        if (!result.Success &&
            result.Publication == null &&
            result.Disposition != TradeCompanyMutationDisposition.Conflict)
        {
            // The publish call failed in this browser; the host may still have committed it.
            await ReportPublicationExceptionAsync(
                order,
                new InvalidOperationException(
                    result.Message ?? "Discord publication could not be queued."));
            return;
        }
        if (!result.Success)
        {
            if (result.Publication != null)
            {
                await LoadAsync();
                SelectOrderAfterReload(
                    orderId,
                    "The commission terms were committed, but the order could not be reloaded.");
                ActivateSharingTab();
            }
            Snackbar.Add(
                result.Publication?.Message ??
                    result.Message ??
                    "Discord publication could not be queued.",
                result.Publication != null ||
                result.Disposition == TradeCompanyMutationDisposition.Conflict
                    ? Severity.Warning
                    : Severity.Error);
            return;
        }

        var (outcome, _) = await ConfirmPublishedCommissionAsync(order);
        await ShowPublishedCommissionAsync(orderId);
        Snackbar.Add(
            (result.Publication?.State == TradeCommissionDeliveryState.Published
                ? "Commission published to Discord"
                : "Discord publication queued") +
            (outcome == PublishedCommissionOutcome.Published ? string.Empty : "; status still confirming"),
            Severity.Success);
    }

    private async Task RetrySelectedCommissionToDiscordAsync()
    {
        if (_selectedOrder == null ||
            SelectedCompanyPublication?.PublicId is not { Length: > 0 } publicId)
        {
            return;
        }

        var orderId = _selectedOrder.Id;
        var result = await TradeCollaboration.RetryDiscordPublicationAsync(
            _selectedOrder,
            publicId);
        if (!result.Success)
        {
            Snackbar.Add(
                result.Publication?.Message ??
                    result.Message ??
                    "Discord publication could not be retried.",
                result.Disposition == TradeCompanyMutationDisposition.Conflict
                    ? Severity.Warning
                    : Severity.Error);
            return;
        }

        await TradeCollaboration.RefreshAsync(
            _selectedOrder.CompanyProfileId,
            orderId);
        Snackbar.Add("Discord publication requeued", Severity.Success);
    }

    private async Task ReconcileSelectedCommissionToDiscordAsync()
    {
        if (_selectedOrder == null ||
            SelectedCompanyPublication?.PublicId is not { Length: > 0 } publicId)
        {
            return;
        }

        _isReconcilingDiscordPublication = true;
        try
        {
            var orderId = _selectedOrder.Id;
            var result = await TradeCollaboration.ReconcileDiscordPublicationAsync(
                _selectedOrder,
                publicId);
            if (!result.Success)
            {
                Snackbar.Add(
                    result.Publication?.Message ??
                    result.Message ??
                    "Discord publication could not be refreshed.",
                    result.Disposition == TradeCompanyMutationDisposition.Conflict
                        ? Severity.Warning
                        : Severity.Error);
                return;
            }

            await TradeCollaboration.RefreshAsync(
                _selectedOrder.CompanyProfileId,
                orderId);
            Snackbar.Add("Discord brief refresh queued", Severity.Success);
        }
        catch (TradeCompanyPublicationRefusedException exception)
        {
            Snackbar.Add(exception.Message, Severity.Warning);
        }
        finally
        {
            _isReconcilingDiscordPublication = false;
        }
    }

    private bool CanPublishToConfiguredTradeChannel(
        TradeOrder order,
        out string reason) =>
        TradeCollaboration.CanPerformExternalAction(order, out reason);

    private async Task UseHostedOrderVersionAsync()
    {
        if (SelectedOrderConflict == null)
        {
            return;
        }

        var orderId = _selectedOrder?.Id;
        try
        {
            await ProfileSync.AcceptRemoteConflictAsync(SelectedOrderConflict);
        }
        catch (Exception exception)
        {
            Snackbar.Add(
                $"The hosted order could not be applied locally: {exception.Message}",
                Severity.Error);
            return;
        }

        await LoadAsync();
        if (orderId.HasValue)
        {
            SelectOrderAfterReload(
                orderId.Value,
                "The hosted version was applied, but the order could not be reloaded.");
            ActivateSharingTab();
        }

        AppState.NotifyTradeOperationsDataChanged();
        Snackbar.Add("Hosted order version applied", Severity.Success);
    }

    private async Task KeepLocalOrderVersionAsync()
    {
        var conflict = SelectedOrderConflict;
        if (conflict == null || !conflict.CanKeepLocal)
        {
            return;
        }

        var orderId = _selectedOrder?.Id;
        try
        {
            await ProfileSync.KeepLocalConflictAsync(conflict);
        }
        catch (Exception exception)
        {
            Snackbar.Add(
                $"Your changes could not be published: {exception.Message}",
                Severity.Error);
            return;
        }

        await LoadAsync();
        if (orderId.HasValue)
        {
            SelectOrderAfterReload(
                orderId.Value,
                "Your changes were published, but the order could not be reloaded.");
            ActivateSharingTab();
        }

        AppState.NotifyTradeOperationsDataChanged();
        Snackbar.Add("Your changes were published", Severity.Success);
    }

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
            ? $"Trade channel — {publication.DestinationLabel ?? "company channel"}"
            : "Portable crafter link";

    private async Task RevokeSelectedCommissionAsync()
    {
        if (_selectedOrder?.CommissionPublication is not { RevokedAtUtc: null } publication)
        {
            return;
        }

        string? editorToken = null;
        if (publication.Ownership == null)
        {
            editorToken = await CommissionBriefLocalState.LoadEditorTokenAsync(_selectedOrder.Id);
            if (string.IsNullOrWhiteSpace(editorToken))
            {
                Snackbar.Add(
                    "This browser no longer has the capability needed to revoke this link.",
                    Severity.Error);
                return;
            }
        }

        _isRevokingCommission = true;
        try
        {
            var orderId = _selectedOrder.Id;
            if (publication.Ownership is not null)
            {
                await TradeCollaboration.RevokePortableLinkAsync(
                    _selectedOrder,
                    publication.PublicId);
            }
            else
            {
                await CommissionBriefs.RevokeAsync(publication.PublicId, editorToken!);
                await CommissionBriefLocalState.ForgetEditorTokenAsync(orderId);
            }
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
            ActivateSharingTab();
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
        TradeCommissionPaymentSummary payment,
        bool? isTestFixture = null)
    {
        var paymentTiming = GetEditablePaymentTiming(order);
        return TradeCommissionDraftComposer.Compose(
            order,
            payment,
            new TradeCommissionBriefContext(
                string.IsNullOrWhiteSpace(_companyProfile?.Name)
                    ? TradeCommissionDraftComposer.DefaultCompanyName
                    : _companyProfile.Name,
                _commissionContact?.Trim() ?? string.Empty,
                _commissionDeliveryInstructions ?? string.Empty,
                paymentTiming.Schedule,
                paymentTiming.CustomTerms,
                isTestFixture ?? order.CompanyCommission?.PublicMetadata.IsTestFixture ?? false,
                order.AssignedCrafterId.HasValue ? "Assigned" : "Open for assignment",
                order.AssignedCrafterId.HasValue ? FormatAssignedCrafter(order) : "Contact operator"));
    }

    private (CompanyCommissionPaymentSchedule Schedule, string? CustomTerms)
        GetEditablePaymentTiming(TradeOrder order)
    {
        if (IsEditingCommissionTermsRevision &&
            _commissionTermsRevisionBrief?.Payment is { } revisionPayment)
        {
            return (revisionPayment.Schedule, revisionPayment.CustomTerms);
        }

        if (order.CompanyCommission?.CurrentTerms.Payment is { } canonicalPayment)
        {
            return (canonicalPayment.Schedule, canonicalPayment.CustomTerms);
        }

        return (order.PaymentSchedule, order.CustomPaymentTerms);
    }

    private CommissionBriefDocument BuildCanonicalCommissionBrief(
        TradeOrder order,
        TradeCompanyCommission commission)
    {
        var terms = commission.CurrentTerms;
        return new CommissionBriefDocument
        {
            IsTestFixture = commission.PublicMetadata.IsTestFixture,
            CompanyName = _companyProfile?.Name ?? "FFXIV Trade Company",
            Title = order.Title,
            StatusLabel = order.AssignedCrafterId.HasValue ? "Assigned" : "Open for assignment",
            AssignmentLabel = order.AssignedCrafterId.HasValue
                ? FormatAssignedCrafter(order)
                : "Contact operator",
            Reference = commission.Reference,
            Contact = terms.ContactInstructions,
            DeliveryInstructions = terms.DeliveryInstructions,
            Outputs = terms.Outputs
                .Select(output => new CommissionBriefOutput(
                    output.ItemId,
                    output.Name,
                    output.RequiredQuantity,
                    output.MustBeHq))
                .ToArray(),
            CrafterMaterials = terms.Materials
                .Where(material => material.Responsibility == CommissionMaterialResponsibility.Crafter)
                .Select(material => new CommissionBriefMaterial(
                    material.ItemId,
                    material.Name,
                    material.Quantity,
                    material.RequiresHq,
                    material.UnitCost,
                    material.TotalCost))
                .ToArray(),
            CompanyMaterials = terms.Materials
                .Where(material => material.Responsibility == CommissionMaterialResponsibility.Provided)
                .Select(material => new CommissionBriefMaterial(
                    material.ItemId,
                    material.Name,
                    material.Quantity,
                    material.RequiresHq,
                    material.UnitCost,
                    material.TotalCost))
                .ToArray(),
            Payment = new CommissionBriefPayment(
                terms.Payment.ContractLabel,
                terms.Payment.MaterialReimbursement,
                terms.Payment.HistoricalAmount,
                terms.Payment.CraftLabor,
                terms.Payment.Total,
                terms.Payment.CraftSynthCount,
                terms.Payment.GilPerSynth,
                terms.Payment.Schedule,
                terms.Payment.CustomTerms),
            Evidence = new CommissionBriefEvidence(
                terms.PricingEvidence.CostBasis,
                terms.PricingEvidence.MarketScope,
                terms.PricingEvidence.Location,
                terms.PricingEvidence.CapturedAtUtc)
        };
    }

    private async Task CopyCommissionLinkAsync()
    {
        if (_selectedOrder?.CommissionPublication is { RevokedAtUtc: null } publication)
        {
            var link = await ResolvePortableLinkAsync(publication);
            await CopyTextToClipboardAsync(
                link.Url,
                "Commission link copied");
        }
    }

    private async Task OpenCommissionBriefAsync()
    {
        if (_selectedOrder?.CommissionPublication is { RevokedAtUtc: null } publication)
        {
            var link = await ResolvePortableLinkAsync(publication);
            await JSRuntime.InvokeVoidAsync(
                "open",
                link.Url,
                "_blank");
        }
    }

    private async Task<PortableCommissionLink> ResolvePortableLinkAsync(
        TradeCommissionPublication publication)
    {
        if (!string.IsNullOrWhiteSpace(publication.PublicUrl))
        {
            return CommissionBriefClient.CreatePortableLink(
                publication.PublicId,
                publication.PublicUrl,
                publication.Version,
                publication.PublishedAtUtc);
        }

        var resolved = publication.Ownership == null
            ? await CommissionBriefs.ResolvePortableLinkAsync(
                publication.PublicId)
            : await TradeCollaboration.ResolvePortableLinkAsync(
                publication.PublicId);
        publication.PublicUrl = resolved.Url;
        return resolved;
    }

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
