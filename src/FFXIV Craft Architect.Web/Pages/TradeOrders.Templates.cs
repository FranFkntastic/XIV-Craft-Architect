using System.Net;

using FFXIV_Craft_Architect.Core.Helpers;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Web.Dialogs;
using FFXIV_Craft_Architect.Web.Services;
using FFXIV_Craft_Architect.Web.Services.ProfileHosting;
using FFXIV_Craft_Architect.Web.Services.TradeCompany;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

using MudBlazor;

namespace FFXIV_Craft_Architect.Web.Pages;

// Company commission templates, order duplication, and the New Order
// Initialize Plan / Save & Price split.
public partial class TradeOrders
{
    private CompanyCommissionPaymentSchedule _newRequestedOrderPaymentSchedule =
        CompanyCommissionPaymentSchedule.Advance;
    private string? _newRequestedOrderCustomPaymentTerms;
    private List<TradeCommissionTemplateSupplyChoice> _newRequestedOrderSupplyChoices = [];
    private string? _appliedTemplateName;
    private bool _isDuplicatingSelectedOrder;
    private bool _isSavingCommissionTemplate;

    private IReadOnlyList<TradeCommissionTemplate> CommissionTemplates =>
        (_companyProfile?.CommissionTemplates ?? [])
            .OrderBy(template => template.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private bool IsSelectedOrderUnpricedSkeleton => TradeOrderWorkflow.IsUnpricedSkeleton(_selectedOrder);

    /// <summary>
    /// Plan-pane warnings. An unpriced skeleton has no quote by design, so only explicit
    /// non-pricing snapshot warnings (such as an inapplicable supply choice) show.
    /// </summary>
    private IReadOnlyList<string> GetVisiblePlanWarnings(TradeCommissionPaymentSummary paymentSummary) =>
        IsSelectedOrderUnpricedSkeleton
            ? (_selectedOrder?.SourceSnapshot?.Warnings ?? [])
                .Where(warning => !string.IsNullOrWhiteSpace(warning) &&
                    (IsSupplyChoiceWarning(warning) || !IsPricingWarning(warning)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : paymentSummary.Warnings;

    private static bool IsSupplyChoiceWarning(string warning) =>
        warning.Contains("supply choice", StringComparison.OrdinalIgnoreCase) ||
        warning.Contains("can no longer be acquired", StringComparison.OrdinalIgnoreCase);

    private bool CanDuplicateOrTemplateSelectedOrder =>
        _selectedOrder != null &&
        !_isDuplicatingSelectedOrder &&
        !_isSavingCommissionTemplate &&
        !IsPlanMutationTransactionRunning &&
        TradeCommissionTemplateMapper.ResolveOutputs(_selectedOrder).Count > 0;

    private static string FormatTemplateOutputSummary(TradeCommissionTemplate template)
    {
        if (template.Outputs.Count == 0)
        {
            return "No outputs";
        }

        var first = template.Outputs[0];
        var label = $"{first.Name} ×{TradeDisplayFormatter.FormatQuantity(first.Quantity)}";
        return template.Outputs.Count == 1
            ? label
            : $"{label} +{template.Outputs.Count - 1:N0}";
    }

    private static IReadOnlyList<string> GetTemplateCompanySupplyNames(TradeCommissionTemplate template) =>
        template.SupplyChoices
            .Where(choice => choice.Responsibility == CommissionMaterialResponsibility.Provided)
            .Select(choice => choice.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string FormatTemplateTooltip(TradeCommissionTemplate template)
    {
        var outputs = string.Join(
            ", ",
            template.Outputs.Select(output =>
                $"{output.Name} ×{TradeDisplayFormatter.FormatQuantity(output.Quantity)}{(output.MustBeHq ? " HQ" : string.Empty)}"));
        return $"Load template {template.Name}: {outputs} · {FormatTemplatePaymentTiming(template.PaymentSchedule, template.CustomPaymentTerms)}";
    }

    private static string FormatTemplatePaymentTiming(
        CompanyCommissionPaymentSchedule schedule,
        string? customTerms) =>
        schedule switch
        {
            CompanyCommissionPaymentSchedule.OnDelivery => "Payment on delivery",
            CompanyCommissionPaymentSchedule.Custom => string.IsNullOrWhiteSpace(customTerms)
                ? "Custom timing"
                : $"Custom timing: {customTerms.Trim()}",
            _ => "Advance payment"
        };

    private static string FormatStagedSupplyChoice(TradeCommissionTemplateSupplyChoice choice) =>
        choice.Responsibility == CommissionMaterialResponsibility.Provided
            ? "Company"
            : choice.AcquisitionSource is { } source
                ? RecipePlanDisplayHelpers.GetSourceDisplayName(source)
                : "Crafter";

    private void ApplyCommissionTemplateToNewOrder(TradeCommissionTemplate template) =>
        ApplyCommissionTemplateToNewOrder(template, scale: 1);

    private void ApplyCommissionTemplateToNewOrder(TradeCommissionTemplate template, int scale)
    {
        ++_requestedOrderSearchGeneration;
        _requestedOrderSearchQuery = string.Empty;
        _requestedOrderSearchResults = [];
        _requestedOrderSearchResultsQuery = null;
        _isSearchingRequestedOrderItems = false;
        _requestedOrderAddQuantity = 1;

        _newRequestedOrderTitle = template.DefaultOrderTitle ?? template.Name;
        _usingSuggestedRequestedOrderTitle = false;
        _newRequestedOrderNotes = template.Notes;
        _newOrderBaseOutputs = template.Outputs
            .Where(output => output.Quantity > 0)
            .Select(output => output with
            {
                Quantity = Math.Clamp(output.Quantity, 1, TradeCommissionTemplateValidator.MaxOutputQuantity)
            })
            .ToArray();
        ApplyNewOrderScale(scale);
        _newRequestedOrderPaymentSchedule = template.PaymentSchedule;
        _newRequestedOrderCustomPaymentTerms = template.CustomPaymentTerms;
        SetNewOrderSupplyChoicesFromTemplate(template);
        _appliedTemplateName = template.Name;
    }

    // New Order scale: _newOrderBaseOutputs is the 1x snapshot and every multiplier applies to
    // it, so switching between multipliers never drifts. A manual row edit makes the edited
    // rows the new 1x base.
    private static readonly int[] NewOrderScalePresets = [1, 2, 3, 4];
    private IReadOnlyList<TradeCommissionTemplateOutput> _newOrderBaseOutputs = [];
    private int _newOrderScaleMultiplier = 1;

    private void SnapshotNewOrderScaleBase()
    {
        _newOrderBaseOutputs = _requestedOrderOutputs
            .Select(output => new TradeCommissionTemplateOutput(
                output.ItemId,
                output.Name,
                output.Quantity,
                output.MustBeHq))
            .ToArray();
        _newOrderScaleMultiplier = 1;
    }

    private void ResetNewOrderScale()
    {
        _newOrderBaseOutputs = [];
        _newOrderScaleMultiplier = 1;
    }

    private void UpdateNewOrderScaleBaseHq(int index, bool mustBeHq)
    {
        if (index < 0 ||
            index >= _newOrderBaseOutputs.Count ||
            _newOrderBaseOutputs.Count != _requestedOrderOutputs.Count)
        {
            SnapshotNewOrderScaleBase();
            return;
        }

        _newOrderBaseOutputs = _newOrderBaseOutputs
            .Select((output, position) => position == index ? output with { MustBeHq = mustBeHq } : output)
            .ToArray();
    }

    private void SetNewOrderScaleMultiplier(int multiplier)
    {
        if (_newOrderBaseOutputs.Count != _requestedOrderOutputs.Count)
        {
            SnapshotNewOrderScaleBase();
        }

        ApplyNewOrderScale(multiplier);
        RefreshRequestedOrderSuggestedTitle(force: _usingSuggestedRequestedOrderTitle);
    }

    private void ApplyNewOrderScale(int multiplier)
    {
        _newOrderScaleMultiplier = Math.Clamp(
            multiplier,
            TradeCommissionTemplateMapper.MinScaleMultiplier,
            TradeCommissionTemplateMapper.MaxScaleMultiplier);
        _requestedOrderOutputs = TradeCommissionTemplateMapper
            .ScaleOutputs(_newOrderBaseOutputs, _newOrderScaleMultiplier)
            .Select(output => new RequestedOrderOutputEditor
            {
                ItemId = output.ItemId,
                Name = output.Name,
                Quantity = output.Quantity,
                MustBeHq = output.MustBeHq
            })
            .ToList();
    }

    // Drops the choice from this order only; company defaults stay as they are.
    private void RemoveStagedNewOrderSupplyChoice(TradeCommissionTemplateSupplyChoice choice)
    {
        _newRequestedOrderSupplyChoices.Remove(choice);
    }

    /// <summary>
    /// Applies one template-list edit to the latest company profile and saves it, rebasing and
    /// retrying once on a concurrent hosted write.
    /// </summary>
    private async Task SaveCommissionTemplatesAsync(
        Func<IReadOnlyList<TradeCommissionTemplate>, IReadOnlyList<TradeCommissionTemplate>> edit)
    {
        if (_companyProfile == null)
        {
            throw new InvalidOperationException("Company is unavailable.");
        }

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var current = await WorkspaceProfiles.ResolveAsync(_companyProfile.Id);
            var templates = edit(current.Profile.CommissionTemplates ?? [])
                .Select(TradeCommissionTemplateValidator.Normalize)
                .ToArray();
            if (!TradeCommissionTemplateValidator.IsValid(templates, out var error))
            {
                throw new InvalidOperationException(error ?? "The commission templates are invalid.");
            }

            current.Profile.CommissionTemplates = templates;
            try
            {
                if (current.IsHosted)
                {
                    await CompanyHubs.UpdateWorkspaceProfileAsync(
                        current.Profile,
                        current.HostedRevision!.Value,
                        updateCommissionTemplates: true);
                }
                else
                {
                    if (!await TradeOperationsPersistence.SaveCompanyProfileAsync(current.Profile))
                    {
                        throw new InvalidOperationException("Could not save the company.");
                    }

                    // The local save succeeded; a sync failure must not roll the templates back.
                    try
                    {
                        await ProfileSync.QueueLocalSaveAsync(
                            ProfileSyncCollections.TradeCompanyProfiles,
                            current.Profile.Id.ToString("D"));
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"Commission template sync queue failed: {ex.Message}");
                    }
                }

                if (_companyProfile?.Id == current.Profile.Id)
                {
                    _companyProfile.CommissionTemplates = templates;
                }
                return;
            }
            catch (CompanyHubRequestException ex) when (ex.StatusCode == HttpStatusCode.Conflict && attempt == 0)
            {
            }
        }
    }

    private async Task<StoredPlan?> TryLoadLinkedPlanAsync(TradeOrder order)
    {
        if (!HasLinkedCraftPlan(order))
        {
            return null;
        }

        try
        {
            return await PlanPersistence.LoadPlanPayloadAsync(order.CraftPlanId!);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private async Task SaveSelectedOrderAsTemplateAsync()
    {
        var order = _selectedOrder;
        if (order == null || _companyProfile == null || !CanDuplicateOrTemplateSelectedOrder)
        {
            return;
        }

        _isSavingCommissionTemplate = true;
        try
        {
            var responsibilities = GetSelectedOrderResponsibilityProjection();
            var linkedPlan = await TryLoadLinkedPlanAsync(order);
            var defaultName = string.IsNullOrWhiteSpace(order.Title) ? "Commission template" : order.Title.Trim();
            var preview = TradeCommissionTemplateMapper.CreateTemplateFromOrder(
                order,
                defaultName,
                responsibilities,
                linkedPlan,
                DateTime.UtcNow);
            var parameters = new DialogParameters<SaveCommissionTemplateDialog>
            {
                { dialog => dialog.DefaultName, defaultName },
                { dialog => dialog.Outputs, preview.Outputs },
                { dialog => dialog.SupplyChoices, preview.SupplyChoices },
                { dialog => dialog.ExistingTemplateNames, CommissionTemplates.Select(template => template.Name).ToArray() },
                { dialog => dialog.PaymentSchedule, preview.PaymentSchedule },
                { dialog => dialog.CustomPaymentTerms, preview.CustomPaymentTerms },
                { dialog => dialog.Notes, preview.Notes }
            };
            var dialog = await DialogService.ShowAsync<SaveCommissionTemplateDialog>(
                "Save Template",
                parameters,
                new DialogOptions { CloseOnEscapeKey = true, MaxWidth = MaxWidth.Small });
            var result = await dialog.Result;
            if (result?.Data is not string name || string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            name = name.Trim();
            var now = DateTime.UtcNow;
            var replaced = false;
            await SaveCommissionTemplatesAsync(templates =>
            {
                var existing = templates.FirstOrDefault(template =>
                    string.Equals(template.Name.Trim(), name, StringComparison.OrdinalIgnoreCase));
                replaced = existing != null;
                var saved = preview with
                {
                    Id = existing?.Id ?? preview.Id,
                    Name = name,
                    CreatedAtUtc = existing?.CreatedAtUtc ?? now,
                    UpdatedAtUtc = now
                };
                return existing == null
                    ? templates.Append(saved).ToArray()
                    : templates.Select(template => template.Id == existing.Id ? saved : template).ToArray();
            });
            Snackbar.Add(replaced ? $"Template '{name}' updated" : $"Template '{name}' saved", Severity.Success);
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Failed to save template: {ex.Message}", Severity.Error);
        }
        finally
        {
            _isSavingCommissionTemplate = false;
        }
    }

    private async Task DeleteCommissionTemplateAsync(TradeCommissionTemplate template)
    {
        if (_companyProfile == null || _isSavingCommissionTemplate)
        {
            return;
        }

        var confirmed = await DialogService.ShowMessageBox(
            "Delete Template",
            $"Delete template '{template.Name}'?",
            yesText: "Delete",
            cancelText: "Cancel");
        if (confirmed != true)
        {
            return;
        }

        _isSavingCommissionTemplate = true;
        try
        {
            await SaveCommissionTemplatesAsync(templates =>
                templates.Where(existing => existing.Id != template.Id).ToArray());
            if (string.Equals(_appliedTemplateName, template.Name, StringComparison.Ordinal))
            {
                _appliedTemplateName = null;
            }
            Snackbar.Add($"Template '{template.Name}' deleted", Severity.Normal);
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Failed to delete template: {ex.Message}", Severity.Error);
        }
        finally
        {
            _isSavingCommissionTemplate = false;
        }
    }

    /// <summary>
    /// Copies the selected order (any lifecycle state, archived included) into a fresh private
    /// draft with an unpriced plan skeleton and the source's supply choices.
    /// </summary>
    private async Task DuplicateSelectedOrderAsync()
    {
        var source = _selectedOrder;
        var companyProfile = _companyProfile;
        if (source == null || companyProfile == null || !CanDuplicateOrTemplateSelectedOrder)
        {
            return;
        }

        _isDuplicatingSelectedOrder = true;
        try
        {
            if (!await ConfirmActiveCraftPlanCanBeReplacedAsync(
                "Duplicating this order",
                targetPlanId: null))
            {
                return;
            }

            var linkedPlan = await TryLoadLinkedPlanAsync(source);
            var seeded = TradeOrderDraftFactory.CreateDuplicate(new TradeOrderDuplicateRequest(
                source,
                DateTime.UtcNow,
                Responsibilities: GetSelectedOrderResponsibilityProjection(),
                LinkedPlan: linkedPlan));
            if (!seeded.CanCreate || seeded.Order == null)
            {
                Snackbar.Add(seeded.Draft.UnavailableReason ?? "Could not duplicate the order.", Severity.Warning);
                return;
            }

            var result = await TradeOrderPricingWorkflow.InitializePlanAsync(
                seeded.Order,
                new TradeOrderInitializePlanOptions(seeded.SupplyChoices, seeded.Responsibilities));
            if (await SaveSeededOrderAsync(result, seeded.Responsibilities, companyProfile) is not { } created)
            {
                return;
            }

            if (await SelectCreatedOrderAsync(created.Id))
            {
                await SetActiveOpsTabAsync(PlanTabIndex);
            }
            Snackbar.Add("Order duplicated", Severity.Success);
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Failed to duplicate the order: {ex.Message}", Severity.Error);
        }
        finally
        {
            _isDuplicatingSelectedOrder = false;
        }
    }

    private Task InitializeRequestedOrderPlanAsync() => CreateRequestedOrderCoreAsync(price: false);

    private Task CreateRequestedOrderAsync() => CreateRequestedOrderCoreAsync(price: true);

    /// <summary>
    /// Creates the New Order draft. Initialize Plan builds an unpriced skeleton with no market
    /// fetch; Save &amp; Price builds and prices it in one pass. Both carry the staged payment
    /// timing and supply choices.
    /// </summary>
    private async Task CreateRequestedOrderCoreAsync(bool price)
    {
        var companyProfile = _companyProfile;
        if (companyProfile == null || !CanCreateRequestedOrder || _isCreatingRequestedOrder)
        {
            return;
        }

        _isCreatingRequestedOrder = true;
        try
        {
            var created = await CreateSeededRequestedOrderAsync(
                companyProfile,
                new NewOrderDraftSpec(
                    _newRequestedOrderTitle,
                    ToRequestedOrderOutputs(),
                    _newRequestedOrderNotes,
                    _newRequestedOrderPaymentSchedule,
                    _newRequestedOrderCustomPaymentTerms,
                    _newRequestedOrderSupplyChoices.ToArray()),
                price);
            if (created == null)
            {
                return;
            }

            // Close first so the reset does not re-seed a draft nobody is editing.
            _showNewOrderPanel = false;
            ResetNewOrderDraft();
            if (!await SelectCreatedOrderAsync(created.Id))
            {
                return;
            }

            await SetActiveOpsTabAsync(PlanTabIndex);
            Snackbar.Add(
                price ? "Trade order created and priced" : "Trade order created · plan ready to price",
                Severity.Success);
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Failed to create Trade order: {ex.Message}", Severity.Error);
        }
        finally
        {
            _isCreatingRequestedOrder = false;
        }
    }

    private sealed record NewOrderDraftSpec(
        string Title,
        IReadOnlyList<TradeRequestedOrderOutput> Outputs,
        string? Notes,
        CompanyCommissionPaymentSchedule PaymentSchedule,
        string? CustomPaymentTerms,
        IReadOnlyList<TradeCommissionTemplateSupplyChoice> SupplyChoices);

    /// <summary>
    /// Builds, optionally prices, and saves one new draft from requested outputs. Returns the
    /// saved order, or null after reporting why it was not created.
    /// </summary>
    private async Task<TradeOrder?> CreateSeededRequestedOrderAsync(
        TradeCompanyProfile companyProfile,
        NewOrderDraftSpec spec,
        bool price)
    {
        if (!await ConfirmActiveCraftPlanCanBeReplacedAsync(
            "Creating this Trade order",
            targetPlanId: null))
        {
            return null;
        }

        var draftResult = TradeOrderDraftFactory.CreateFromRequestedOutputs(new TradeRequestedOrderCreateRequest(
            companyProfile.Id,
            null,
            spec.Title,
            spec.Outputs,
            AppState.SelectedDataCenter,
            AppState.SelectedRegion,
            AppState.DefaultMarketFetchScope,
            MarketFetchScopeResolver.GetDataCenters(
                AppState.DefaultMarketFetchScope,
                AppState.SelectedDataCenter,
                AppState.SelectedRegion,
                AppState.AnalysisRegions),
            World: null,
            spec.Notes,
            DateTime.UtcNow));
        if (!draftResult.CanCreate || draftResult.Order == null)
        {
            Snackbar.Add(draftResult.UnavailableReason ?? "Could not create Trade order.", Severity.Warning);
            return null;
        }

        var order = draftResult.Order;
        order.PaymentSchedule = spec.PaymentSchedule;
        order.CustomPaymentTerms = spec.PaymentSchedule == CompanyCommissionPaymentSchedule.Custom &&
            !string.IsNullOrWhiteSpace(spec.CustomPaymentTerms)
                ? spec.CustomPaymentTerms.Trim()
                : null;
        var supplyChoices = ExcludeOutputSupplyChoices(spec.SupplyChoices, spec.Outputs);
        var responsibilities = TradeCommissionTemplateMapper.ToResponsibilityLines(supplyChoices);
        var result = price
            ? await TradeOrderPricingWorkflow.RebuildAndPriceAsync(
                order,
                new TradeOrderPricingWorkflowOptions(
                    ForceRefreshMarketData: false,
                    MaterialPricingPolicy: companyProfile.MaterialPricingPolicy,
                    MaterialResponsibilities: responsibilities,
                    InitialSupplyChoices: supplyChoices))
            : await TradeOrderPricingWorkflow.InitializePlanAsync(
                order,
                new TradeOrderInitializePlanOptions(supplyChoices, responsibilities));
        return await SaveSeededOrderAsync(result, responsibilities, companyProfile);
    }

    // Orders rail template launcher.
    private static readonly int[] TemplateLauncherScalePresets = [1, 2, 4];
    private bool _isTemplateLauncherOpen;
    private int _templateLauncherScale = 1;

    private void ToggleTemplateLauncher() => _isTemplateLauncherOpen = !_isTemplateLauncherOpen;

    private void CloseTemplateLauncher() => _isTemplateLauncherOpen = false;

    private void OnTemplateLauncherKeyDown(KeyboardEventArgs e)
    {
        if (e.Key == "Escape")
        {
            _isTemplateLauncherOpen = false;
        }
    }

    private void OnNewOrderScaleInputChanged(ChangeEventArgs e)
    {
        if (int.TryParse(e.Value?.ToString(), out var multiplier))
        {
            SetNewOrderScaleMultiplier(multiplier);
        }
    }

    private void OpenTemplateInNewOrderEditor(TradeCommissionTemplate template)
    {
        _isTemplateLauncherOpen = false;
        StartNewOrderWorkspace();
        ApplyCommissionTemplateToNewOrder(template, _templateLauncherScale);
    }

    /// <summary>
    /// One-click template order from the Orders rail: scaled outputs, company defaults with the
    /// template's supply choices on top, then Initialize Plan or Save &amp; Price. The New Order
    /// editor's own draft is left untouched.
    /// </summary>
    private async Task LaunchTemplateOrderAsync(TradeCommissionTemplate template, bool price)
    {
        _isTemplateLauncherOpen = false;
        var companyProfile = _companyProfile;
        if (companyProfile == null || _isCreatingRequestedOrder || IsPlanMutationTransactionRunning)
        {
            return;
        }

        var outputs = TradeCommissionTemplateMapper
            .ScaleOutputs(
                template.Outputs.Where(output => output.Quantity > 0).ToArray(),
                _templateLauncherScale)
            .Select(output => new TradeRequestedOrderOutput(
                output.ItemId,
                output.Name,
                output.Quantity,
                output.MustBeHq,
                EstimatedSaleValue: 0m))
            .ToArray();
        if (outputs.Length == 0)
        {
            Snackbar.Add($"Template '{template.Name}' has no outputs.", Severity.Warning);
            return;
        }

        _isCreatingRequestedOrder = true;
        try
        {
            var created = await CreateSeededRequestedOrderAsync(
                companyProfile,
                new NewOrderDraftSpec(
                    string.IsNullOrWhiteSpace(template.DefaultOrderTitle) ? template.Name : template.DefaultOrderTitle,
                    outputs,
                    template.Notes,
                    template.PaymentSchedule,
                    template.CustomPaymentTerms,
                    TradeCommissionTemplateMapper.MergeCompanySupplyDefaults(
                        CompanySupplyDefaults,
                        template.SupplyChoices)),
                price);
            if (created == null)
            {
                return;
            }

            _showNewOrderPanel = false;
            if (!await SelectCreatedOrderAsync(created.Id))
            {
                return;
            }

            await SetActiveOpsTabAsync(PlanTabIndex);
            Snackbar.Add(
                price ? "Trade order created and priced" : "Trade order created · plan ready to price",
                Severity.Success);
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Failed to create Trade order: {ex.Message}", Severity.Error);
        }
        finally
        {
            _isCreatingRequestedOrder = false;
        }
    }

    /// <summary>
    /// "Duplicate ×N": N independent drafts of the selected order sharing one plan build (and
    /// one pricing run when requested), each saved with its own linked plan revision and
    /// responsibility draft through the same path as a single new draft.
    /// </summary>
    private async Task BatchDuplicateSelectedOrderAsync()
    {
        var source = _selectedOrder;
        var companyProfile = _companyProfile;
        if (source == null || companyProfile == null || !CanDuplicateOrTemplateSelectedOrder)
        {
            return;
        }

        var parameters = new DialogParameters<BatchDuplicateOrderDialog>
        {
            { dialog => dialog.SourceTitle, source.Title }
        };
        var dialog = await DialogService.ShowAsync<BatchDuplicateOrderDialog>(
            "Duplicate ×N",
            parameters,
            new DialogOptions { CloseOnEscapeKey = true, MaxWidth = MaxWidth.ExtraSmall, FullWidth = true });
        var dialogResult = await dialog.Result;
        if (dialogResult is not { Canceled: false, Data: BatchDuplicateOrderRequest request } ||
            _selectedOrder?.Id != source.Id)
        {
            return;
        }

        _isDuplicatingSelectedOrder = true;
        try
        {
            if (!await ConfirmActiveCraftPlanCanBeReplacedAsync(
                "Duplicating this order",
                targetPlanId: null))
            {
                return;
            }

            var linkedPlan = await TryLoadLinkedPlanAsync(source);
            var batch = TradeOrderDraftFactory.CreateBatchDuplicates(new TradeOrderBatchDuplicateRequest(
                source,
                request.Count,
                DateTime.UtcNow,
                request.BaseTitle,
                GetSelectedOrderResponsibilityProjection(),
                linkedPlan));
            if (!batch.CanCreate || batch.Orders.Count == 0)
            {
                Snackbar.Add(batch.UnavailableReason ?? "Could not duplicate the order.", Severity.Warning);
                return;
            }

            var result = await TradeOrderPricingWorkflow.MaterializeBatchAsync(
                batch.Orders,
                new TradeOrderBatchMaterializeOptions(
                    request.Price,
                    companyProfile.MaterialPricingPolicy,
                    batch.SupplyChoices,
                    batch.Responsibilities));
            if (!result.HasUpdatedOrders)
            {
                // New drafts have no earlier plan revision to restore, so a failed build only
                // reports why.
                if (result.Status == TradeOrderPricingWorkflowStatus.CompanyPricingScopeRequired)
                {
                    ShowCompanyPricingRequired(TradeOrderPricingWorkflowResult.Noop(
                        result.Status,
                        result.Message,
                        result.MessageLevel,
                        result.ActivePlanFence));
                }
                else if (result.Status != TradeOrderPricingWorkflowStatus.Canceled)
                {
                    Snackbar.Add(result.Message, ToSnackbarSeverity(result.MessageLevel));
                }
                return;
            }

            var created = new List<TradeOrder>(result.UpdatedOrders.Count);
            foreach (var order in result.UpdatedOrders)
            {
                if (await PersistSeededOrderAsync(order, batch.Responsibilities, companyProfile) is not { } saved)
                {
                    break;
                }
                created.Add(saved);
            }
            if (created.Count == 0)
            {
                return;
            }

            // The other copies join the board in place; the first is selected because its
            // sealed plan revision is the one left live in the Worker.
            foreach (var order in created.Skip(1))
            {
                var stored = await TradeOperationsPersistence.LoadOrderAsync(order.Id) ?? order;
                var index = _orders.FindIndex(existing => existing.Id == stored.Id);
                if (index >= 0)
                {
                    _orders[index] = stored;
                }
                else
                {
                    _orders.Add(stored);
                }
            }

            if (await SelectCreatedOrderAsync(created[0].Id))
            {
                await SetActiveOpsTabAsync(PlanTabIndex);
            }

            if (created.Count == result.UpdatedOrders.Count)
            {
                Snackbar.Add(result.Message, ToSnackbarSeverity(result.MessageLevel));
            }
            else
            {
                Snackbar.Add(
                    $"{created.Count:N0} of {result.UpdatedOrders.Count:N0} duplicate orders were saved.",
                    Severity.Warning);
            }
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Failed to duplicate the order: {ex.Message}", Severity.Error);
        }
        finally
        {
            _isDuplicatingSelectedOrder = false;
        }
    }

    /// <summary>
    /// Saves a freshly built draft order: stamps the company payment policy, persists any
    /// seeded responsibilities as its linked payroll draft, then saves the order.
    /// Returns the saved order, or null after reporting why it was not saved.
    /// </summary>
    private async Task<TradeOrder?> SaveSeededOrderAsync(
        TradeOrderPricingWorkflowResult result,
        IReadOnlyList<TradePayrollResponsibilityLine> responsibilities,
        TradeCompanyProfile companyProfile)
    {
        if (!result.HasUpdatedOrder || result.UpdatedOrder == null)
        {
            if (result.Status == TradeOrderPricingWorkflowStatus.CompanyPricingScopeRequired)
            {
                ShowCompanyPricingRequired(result);
            }
            else
            {
                Snackbar.Add(result.Message, ToSnackbarSeverity(result.MessageLevel));
            }
            return null;
        }

        return await PersistSeededOrderAsync(result.UpdatedOrder, responsibilities, companyProfile);
    }

    /// <summary>
    /// Persists one built draft order and, when it carries seeded responsibilities, its own
    /// linked payroll draft. Returns the saved order, or null after reporting the failure.
    /// </summary>
    private async Task<TradeOrder?> PersistSeededOrderAsync(
        TradeOrder order,
        IReadOnlyList<TradePayrollResponsibilityLine> responsibilities,
        TradeCompanyProfile companyProfile)
    {
        order.PaymentPolicyOverride = TradeOrderWorkflow.ResolvePaymentPolicy(
            order,
            companyProfile.PaymentPolicy);

        if (responsibilities.Count > 0)
        {
            var policy = order.PaymentPolicyOverride;
            var now = DateTime.UtcNow;
            var draft = new TradePayrollWorkflowDraft
            {
                CompanyProfileId = order.CompanyProfileId,
                OrderId = order.Id,
                PlanSessionVersion = order.SourceSnapshot.PlanSessionVersion,
                MarketAnalysisVersion = order.SourceSnapshot.MarketAnalysisVersion,
                SourcePlanName = string.IsNullOrWhiteSpace(order.SourceSnapshot.SourcePlanName)
                    ? "Active craft plan"
                    : order.SourceSnapshot.SourcePlanName,
                AssignedCrafterId = order.AssignedCrafterId,
                ActivePaymentContract = policy?.ActiveContract ?? TradePaymentContractMode.LaborStandard,
                LaborGilPerSynth = policy?.LaborGilPerSynth > 0
                    ? policy.LaborGilPerSynth
                    : TradePaymentPolicy.DefaultLaborGilPerSynth,
                Responsibilities = responsibilities.ToArray(),
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };
            if (await TradePayrollPersistence.SaveDraftAsync(draft))
            {
                _payrollDrafts = _payrollDrafts
                    .Where(existing => existing.Id != draft.Id)
                    .Append(draft)
                    .ToList();
                order.PayrollDraftId = draft.Id;
            }
            else
            {
                Snackbar.Add("Supply choices could not be saved; the order uses crafter supply.", Severity.Warning);
            }
        }

        if (await SaveNewWorkPackageAsync(order, companyProfile) is not { } saved)
        {
            Snackbar.Add("Failed to save Trade order.", Severity.Error);
            return null;
        }

        return saved;
    }

    /// <summary>
    /// Builds (or rebuilds) the selected order's linked plan as an unpriced skeleton: recipe tree,
    /// responsibilities and the current plan's supply choices, with no market fetch.
    /// </summary>
    private async Task InitializeSelectedOrderPlanAsync()
    {
        if (_selectedOrder == null ||
            _companyProfile == null ||
            IsPlanMutationTransactionRunning)
        {
            return;
        }

        if (TradeOrderStatusWorkflow.IsArchived(_selectedOrder.Status))
        {
            Snackbar.Add("Reopen archived orders before rebuilding the linked craft plan.", Severity.Warning);
            return;
        }

        // Published commissions change their plan only inside a terms revision.
        if (_selectedOrder.CompanyCommission != null &&
            !CanEditCanonicalWorkPackage &&
            CanBeginSelectedOrderPlanReconstruction)
        {
            ShowCommissionTermsRevision();
        }

        if (!CanEditSelectedOrderPlan)
        {
            Snackbar.Add("Published work packages can only change through Revise Terms.", Severity.Warning);
            return;
        }

        if (!await ConfirmActiveCraftPlanCanBeReplacedAsync(
            HasLinkedCraftPlan(_selectedOrder) ? "Rebuilding this order plan" : "Creating this order plan",
            _selectedOrder.CraftPlanId))
        {
            return;
        }

        if (!IsSelectedOrderUnpricedSkeleton)
        {
            var assessment = TradeOrderWorkflow.AssessGeneratedCraftPlanReplacement(_selectedOrder);
            if (assessment.RequiresConfirmation &&
                !await ConfirmCraftPlanReplacementAsync(assessment))
            {
                return;
            }
        }

        _isSavingSelectedOrderCraftPlan = true;
        var orderId = _selectedOrder.Id;
        try
        {
            var responsibilities = GetSelectedOrderResponsibilityProjection();
            var linkedPlan = await TryLoadLinkedPlanAsync(_selectedOrder);
            var supplyChoices = TradeCommissionTemplateMapper.ExtractTemplateSupplyChoices(
                _selectedOrder,
                responsibilities,
                linkedPlan);
            var result = await TradeOrderPricingWorkflow.InitializePlanAsync(
                _selectedOrder,
                new TradeOrderInitializePlanOptions(
                    supplyChoices,
                    responsibilities?.Responsibilities ?? []));
            if (!result.HasUpdatedOrder || result.UpdatedOrder == null)
            {
                if (result.Status == TradeOrderPricingWorkflowStatus.CompanyPricingScopeRequired)
                {
                    ShowCompanyPricingRequired(result);
                }
                else
                {
                    Snackbar.Add(result.Message, ToSnackbarSeverity(result.MessageLevel));
                }
                return;
            }

            var saved = !HasCanonicalCommission
                ? await SaveOrderAndNotifyAsync(result.UpdatedOrder)
                : await UpdateCanonicalDraftAsync(
                    result.UpdatedOrder,
                    BuildCommissionBrief(
                        result.UpdatedOrder,
                        TradeCommissionPaymentSummary.FromOrder(
                            result.UpdatedOrder,
                            responsibilities,
                            GetSelectedOrderEffectivePaymentPolicy())),
                    "Craft plan initialized and saved to the commission draft");
            if (!saved)
            {
                Snackbar.Add("Craft plan built, but failed to link it to the order.", Severity.Error);
                return;
            }

            if (!HasCanonicalCommission)
            {
                await LoadAsync();
                if (string.IsNullOrWhiteSpace(_loadError))
                {
                    SelectOrderAfterReload(orderId, "Craft plan was saved, but the order could not be loaded.");
                }
            }

            Snackbar.Add(result.Message, ToSnackbarSeverity(result.MessageLevel));
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Failed to initialize the linked craft plan: {ex.Message}", Severity.Error);
        }
        finally
        {
            _isSavingSelectedOrderCraftPlan = false;
            ScheduleSelectedOrderPlanRestoration();
        }
    }

    /// <summary>
    /// First pricing run for an unpriced skeleton: reuses warm market evidence and fetches only
    /// what the crafter buys.
    /// </summary>
    private Task PriceSelectedOrderAsync() =>
        RepriceSelectedOrderCoreAsync(stagedResponsibilities: null, forceRefreshMarketData: false);
}
