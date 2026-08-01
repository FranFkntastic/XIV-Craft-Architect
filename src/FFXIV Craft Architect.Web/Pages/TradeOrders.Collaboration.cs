using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Web.Dialogs;
using FFXIV_Craft_Architect.Web.Services;
using FFXIV_Craft_Architect.Web.Services.ProfileHosting;
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
    private string? _activeInterestClaimId;
    private readonly Dictionary<string, Guid?> _interestCrafterSelections =
        new(StringComparer.Ordinal);

    private bool HasLiveCommissionPublication =>
        _selectedOrder?.CommissionPublication is { RevokedAtUtc: null };

    private TradeCommissionPublicationProjection? SelectedCompanyPublication =>
        _selectedOrder == null
            ? null
            : TradeCollaboration.GetPublication(_selectedOrder.Id);

    private string? CanonicalDiscordPublishUnavailableReason
    {
        get
        {
            if (SelectedCommissionOwner?.Order is not { } order ||
                order.CompanyCommission is not { } commission)
            {
                return "The canonical company commission is unavailable.";
            }

            if (commission.PublicMetadata.ViewState != CompanyCommissionPublicViewState.Draft)
            {
                return "The canonical brief has already been published.";
            }

            if (HasActiveCompanyPublication)
            {
                return "Trade channel delivery is already in progress.";
            }

            return CanPublishToConfiguredTradeChannel(order, out var reason)
                ? null
                : reason;
        }
    }

    private bool CanPublishCanonicalCommissionToDiscord =>
        !_isPublishingCommission &&
        CanonicalDiscordPublishUnavailableReason == null;

    private IReadOnlyList<TradeCommissionInterest> SelectedPendingInterests =>
        _selectedOrder == null
            ? []
            : TradeCollaboration.GetPendingInterests(_selectedOrder.Id);

    private ProfileSyncConflict? SelectedOrderConflict =>
        _selectedOrder == null
            ? null
            : ProfileSync.Conflicts.FirstOrDefault(conflict =>
                string.Equals(
                    conflict.Collection,
                    ProfileSyncCollections.TradeOrders,
                    StringComparison.Ordinal) &&
                string.Equals(
                    conflict.ObjectId,
                    _selectedOrder.Id.ToString("D"),
                    StringComparison.OrdinalIgnoreCase));

    private bool HasActiveCompanyPublication =>
        SelectedCompanyPublication?.State is
            TradeCommissionDeliveryState.Pending or
            TradeCommissionDeliveryState.Published;

    private bool HasFailedCompanyPublication =>
        SelectedCompanyPublication?.State == TradeCommissionDeliveryState.Failed;

    private string PublishCommissionButtonLabel =>
        HasFailedCompanyPublication
            ? "Retry Trade Channel"
            : "Choose Delivery";

    private string? PublishCommissionUnavailableReason
    {
        get
        {
            if (_selectedOrder == null)
            {
                return "Choose a commission first.";
            }

            if (HasLiveCommissionPublication && !HasFailedCompanyPublication)
            {
                return "This commission already has a live crafter brief.";
            }

            if (HasActiveCompanyPublication)
            {
                return "Trade channel delivery is already in progress.";
            }

            if (HasFailedCompanyPublication)
            {
                if (string.IsNullOrWhiteSpace(SelectedCompanyPublication?.PublicId))
                {
                    return "The failed trade channel publication identity is unavailable.";
                }

                return CanPublishToConfiguredTradeChannel(
                    _selectedOrder,
                    out var retryReason)
                        ? null
                        : retryReason;
            }

            if (GetOrderRootItems(_selectedOrder).Count == 0)
            {
                return "Add at least one requested output before publishing.";
            }

            var payment = GetSelectedOrderPaymentSummary();
            if (payment.TotalPayment <= 0 || !payment.Active.IsAvailable)
            {
                return "Resolve the current payment evidence before publishing.";
            }

            return null;
        }
    }

    private bool CanPublishCommission =>
        !_isPublishingCommission &&
        PublishCommissionUnavailableReason == null;

    private void PrepareCommissionDraft(TradeOrder order)
    {
        var terms = order.CompanyCommission?.CurrentTerms;
        _commissionContact = terms?.ContactInstructions ??
            _companyProfile?.CommissionContact ??
            string.Empty;
        _commissionDeliveryInstructions = terms?.DeliveryInstructions ?? string.Empty;
        if (order.CompanyCommission == null)
        {
            foreach (var claim in TradeCollaboration.GetPendingInterests(order.Id))
            {
                _interestCrafterSelections.TryAdd(claim.ClaimId, claim.MatchedCrafterId);
            }
        }

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
            foreach (var claim in TradeCollaboration.GetPendingInterests(order.Id))
            {
                _interestCrafterSelections.TryAdd(claim.ClaimId, claim.MatchedCrafterId);
            }
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

    private async Task PublishSelectedCommissionAsync()
    {
        if (_selectedOrder == null || _companyProfile == null || !CanPublishCommission)
        {
            return;
        }

        if (HasFailedCompanyPublication)
        {
            _isPublishingCommission = true;
            try
            {
                await RetrySelectedCommissionToDiscordAsync();
            }
            finally
            {
                _isPublishingCommission = false;
            }

            return;
        }

        var payment = GetSelectedOrderPaymentSummary();
        var discordAvailable = CanPublishToConfiguredTradeChannel(
            _selectedOrder,
            out var discordUnavailableReason);
        var parameters = new DialogParameters
        {
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
        var publicationOrderId = _selectedOrder.Id;
        TradeCompanyPublicationOwnership? ownership = null;
        PortableCommissionLink? localLink = null;
        var localOrderAttached = false;
        try
        {
            if (publishResult.Destination == TradeCommissionDestination.DiscordChannel)
            {
                await PublishSelectedCommissionToDiscordAsync(
                    _selectedOrder,
                    BuildCommissionBrief(
                        _selectedOrder,
                        payment,
                        publishResult.IsTestFixture));
                return;
            }

            var orderId = publicationOrderId;
            ownership = await TradeCollaboration.GetPublicationOwnershipAsync(_selectedOrder);
            var brief = BuildCommissionBrief(
                _selectedOrder,
                payment,
                publishResult.IsTestFixture);
            var link = ownership == null
                ? await CommissionBriefs.PublishPortableLinkAsync(brief)
                : await TradeCollaboration.PublishPortableLinkAsync(
                    _selectedOrder,
                    brief);
            if (ownership == null)
            {
                localLink = link;
                var editorToken = link.EditorToken ??
                    throw new InvalidOperationException(
                        "The local brief did not return its revoke capability.");
                if (!await CommissionBriefLocalState.SaveEditorTokenAsync(
                        orderId,
                        editorToken))
                {
                    await RollBackLocalPortableLinkAsync(
                        _selectedOrder,
                        link);
                    return;
                }

                var orderToSave = TradeOrderWorkflow.CopyOrder(_selectedOrder);
                orderToSave.CommissionPublication = new TradeCommissionPublication
                {
                    PublicId = link.PublicId,
                    PublicUrl = link.PublicUrl,
                    Version = link.Version,
                    PublishedAtUtc = link.PublishedAtUtc,
                    IsTestFixture = brief.IsTestFixture
                };
                AppendCommissionHistory(
                    orderToSave,
                    TradeOrderHistoryEventKind.CommissionPublished,
                    $"Published crafter brief v{link.Version}.",
                    link.PublishedAtUtc);
                if (!await TradeOperationsPersistence.SaveOrderAsync(orderToSave))
                {
                    await RollBackLocalPortableLinkAsync(
                        _selectedOrder,
                        link);
                    return;
                }

                localOrderAttached = true;
                await ProfileSync.QueueLocalSaveAsync(
                    ProfileSyncCollections.TradeOrders,
                    orderId.ToString("D"));
            }

            AppState.NotifyTradeOperationsDataChanged();
            await LoadAsync();
            SelectOrderAfterReload(orderId, "The brief was published, but the order could not be reloaded.");
            _activeOpsTab = SharingTabIndex;
            await CopyTextToClipboardAsync(
                link.Url,
                "Commission published and link copied");
        }
        catch (Exception exception)
        {
            if (localLink != null && !localOrderAttached)
            {
                await RollBackLocalPortableLinkAsync(
                    _selectedOrder,
                    localLink);
            }

            if (localOrderAttached || ownership != null)
            {
                await LoadAsync();
                SelectOrderAfterReload(
                    publicationOrderId,
                    "The publication may be attached remotely, but the order could not be reloaded.");
                _activeOpsTab = SharingTabIndex;
            }

            Snackbar.Add(
                localOrderAttached || ownership != null
                    ? $"The commission publication status could not be confirmed: {exception.Message}"
                    : $"Commission publication failed: {exception.Message}",
                localOrderAttached || ownership != null
                    ? Severity.Warning
                    : Severity.Error);
        }
        finally
        {
            _isPublishingCommission = false;
        }
    }

    private async Task PublishCanonicalCommissionToDiscordAsync()
    {
        var owner = SelectedCommissionOwner;
        if (owner?.Order.CompanyCommission is not { } commission ||
            !CanPublishCanonicalCommissionToDiscord)
        {
            return;
        }

        _isPublishingCommission = true;
        try
        {
            await PublishSelectedCommissionToDiscordAsync(
                owner.Order,
                BuildCanonicalCommissionBrief(owner.Order, commission));
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

    private async Task PublishSelectedCommissionToDiscordAsync(
        TradeOrder order,
        CommissionBriefDocument brief)
    {
        var orderId = order.Id;
        var result = await TradeCollaboration.PublishToDiscordAsync(
            order,
            brief);
        if (!result.Success)
        {
            if (result.Publication != null)
            {
                await LoadAsync();
                SelectOrderAfterReload(
                    orderId,
                    "The commission terms were committed, but the order could not be reloaded.");
                _activeOpsTab = SharingTabIndex;
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

        await LoadAsync();
        SelectOrderAfterReload(orderId, "The publication was accepted, but the order could not be reloaded.");
        _activeOpsTab = SharingTabIndex;
        Snackbar.Add(
            result.Publication?.State == TradeCommissionDeliveryState.Published
                ? "Commission published to Discord"
                : "Discord publication queued",
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

    private bool CanPublishToConfiguredTradeChannel(
        TradeOrder order,
        out string reason) =>
        TradeCollaboration.CanPerformExternalAction(order, out reason);

    private async Task RollBackLocalPortableLinkAsync(
        TradeOrder order,
        PortableCommissionLink link)
    {
        try
        {
            var editorToken = link.EditorToken ??
                throw new InvalidOperationException(
                    "The local brief revoke capability was unavailable.");
            await CommissionBriefs.RevokeAsync(
                link.PublicId,
                editorToken);
        }
        catch (Exception exception)
        {
            Snackbar.Add(
                $"The order could not retain the new link, and the orphaned brief could not be revoked: {exception.Message}",
                Severity.Error);
            return;
        }

        try
        {
            await CommissionBriefLocalState.ForgetEditorTokenAsync(order.Id);
        }
        catch (Exception exception)
        {
            Snackbar.Add(
                $"The unpublished revoke capability could not be cleared from browser storage: {exception.Message}",
                Severity.Warning);
        }

        Snackbar.Add(
            "The order could not retain the new link, so the brief was revoked.",
            Severity.Warning);
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
            _activeOpsTab = SharingTabIndex;
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
            _activeOpsTab = SharingTabIndex;
            Snackbar.Add("Crafter interest declined", Severity.Success);
        }
        finally
        {
            _activeInterestClaimId = null;
        }
    }

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
            _activeOpsTab = SharingTabIndex;
        }

        AppState.NotifyTradeOperationsDataChanged();
        Snackbar.Add("Hosted order version applied", Severity.Success);
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
            _activeOpsTab = SharingTabIndex;
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
        var source = order.SourceSnapshot ?? new TradeOrderSourceSnapshot();
        var evidenceCapturedAt = payment.Materials
            .Where(material => material.EvidenceTimestampUtc.HasValue)
            .Select(material => material.EvidenceTimestampUtc!.Value)
            .DefaultIfEmpty(source.ImportedAtUtc)
            .Max();

        return new CommissionBriefDocument
        {
            IsTestFixture = isTestFixture ??
                order.CompanyCommission?.PublicMetadata.IsTestFixture ??
                false,
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
                terms.Payment.MaterialAdjustment,
                terms.Payment.CraftLabor,
                terms.Payment.Total,
                MaterialAdjustmentPercent: 0m,
                terms.Payment.CraftSynthCount,
                terms.Payment.GilPerSynth),
            Evidence = new CommissionBriefEvidence(
                terms.PricingEvidence.CostBasis,
                terms.PricingEvidence.MarketScope,
                terms.PricingEvidence.Location,
                terms.PricingEvidence.CapturedAtUtc)
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
