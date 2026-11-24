using System.Net;

using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Web.Services;
using FFXIV_Craft_Architect.Web.Services.ProfileHosting;
using FFXIV_Craft_Architect.Web.Services.TradeCompany;

using MudBlazor;

namespace FFXIV_Craft_Architect.Web.Pages;

// Company-wide "always company-supplied" materials: the Plan grid pin, the rebase-and-retry
// save, and seeding the defaults into every new draft.
public partial class TradeOrders
{
    private bool _isSavingCompanySupplyDefaults;
    private bool _newOrderSupplyDefaultsSeeded;

    // (ItemId, RequiresHq) keys the loaded template chose explicitly; every other staged choice
    // that matches a company default came from the defaults.
    private HashSet<(int ItemId, bool RequiresHq)> _newOrderTemplateSupplyKeys = [];

    private IReadOnlyList<TradeCompanySupplyDefault> CompanySupplyDefaults =>
        _companyProfile?.DefaultCompanySuppliedItems ?? [];

    private bool IsCompanySupplyDefault(int itemId, bool requiresHq) =>
        CompanySupplyDefaults.Any(item => item.ItemId == itemId && item.RequiresHq == requiresHq);

    /// <summary>
    /// The pin shows on editable plans for material rows; managing defaults follows the same
    /// access as saving templates (the Orders board itself is operator-only).
    /// </summary>
    private bool CanPinCompanySupplyDefault(TradeOrderProcurementRow row) =>
        _companyProfile != null &&
        CanEditSelectedOrderPlan &&
        !IsRequestedOutputRow(row);

    private async Task ToggleCompanySupplyDefaultAsync(TradeOrderProcurementRow row)
    {
        if (_companyProfile == null || _isSavingCompanySupplyDefaults || !CanPinCompanySupplyDefault(row))
        {
            return;
        }

        var wasPinned = IsCompanySupplyDefault(row.ItemId, row.RequiresHq);
        var item = TradeCompanySupplyDefaultValidator.Normalize(
            new TradeCompanySupplyDefault(row.ItemId, row.ItemName, row.RequiresHq));
        _isSavingCompanySupplyDefaults = true;
        try
        {
            await SaveCompanySupplyDefaultsAsync(defaults =>
            {
                var others = defaults
                    .Where(existing => existing.ItemId != item.ItemId || existing.RequiresHq != item.RequiresHq);
                return wasPinned ? others.ToArray() : others.Append(item).ToArray();
            });
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Failed to update company defaults: {ex.Message}", Severity.Error);
            return;
        }
        finally
        {
            _isSavingCompanySupplyDefaults = false;
        }

        // Pinning means "the company supplies this"; apply it to this order right away.
        // Unpinning only changes future defaults and leaves every order as it is.
        if (!wasPinned && row.Responsibility == CommissionMaterialResponsibility.Crafter)
        {
            await ChangeProcurementRowResponsibilityAsync(row, CommissionMaterialResponsibility.Provided);
        }
    }

    /// <summary>
    /// Applies one edit to the latest company defaults and saves them, rebasing and retrying
    /// once on a concurrent hosted write.
    /// </summary>
    private async Task SaveCompanySupplyDefaultsAsync(
        Func<IReadOnlyList<TradeCompanySupplyDefault>, IReadOnlyList<TradeCompanySupplyDefault>> edit)
    {
        if (_companyProfile == null)
        {
            throw new InvalidOperationException("Company is unavailable.");
        }

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var current = await WorkspaceProfiles.ResolveAsync(_companyProfile.Id);
            var defaults = edit(current.Profile.DefaultCompanySuppliedItems ?? [])
                .Select(TradeCompanySupplyDefaultValidator.Normalize)
                .ToArray();
            if (!TradeCompanySupplyDefaultValidator.IsValid(defaults, out var error))
            {
                throw new InvalidOperationException(error ?? "The company defaults are invalid.");
            }

            current.Profile.DefaultCompanySuppliedItems = defaults;
            try
            {
                if (current.IsHosted)
                {
                    await CompanyHubs.UpdateWorkspaceProfileAsync(
                        current.Profile,
                        current.HostedRevision!.Value,
                        updateDefaultCompanySuppliedItems: true);
                }
                else
                {
                    if (!await TradeOperationsPersistence.SaveCompanyProfileAsync(current.Profile))
                    {
                        throw new InvalidOperationException("Could not save the company.");
                    }

                    // The local save succeeded; a sync failure must not roll the defaults back.
                    try
                    {
                        await ProfileSync.QueueLocalSaveAsync(
                            ProfileSyncCollections.TradeCompanyProfiles,
                            current.Profile.Id.ToString("D"));
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"Company supply defaults sync queue failed: {ex.Message}");
                    }
                }

                if (_companyProfile?.Id == current.Profile.Id)
                {
                    _companyProfile.DefaultCompanySuppliedItems = defaults;
                }
                return;
            }
            catch (CompanyHubRequestException ex) when (ex.StatusCode == HttpStatusCode.Conflict && attempt == 0)
            {
            }
        }
    }

    /// <summary>
    /// Seeds a fresh New Order draft with the company defaults once; later opens keep whatever
    /// the operator removed for this order.
    /// </summary>
    private void SeedNewOrderCompanySupplyDefaults()
    {
        if (_newOrderSupplyDefaultsSeeded || _companyProfile == null)
        {
            return;
        }

        _newOrderSupplyDefaultsSeeded = true;
        _newOrderTemplateSupplyKeys = [];
        _newRequestedOrderSupplyChoices = TradeCommissionTemplateMapper
            .MergeCompanySupplyDefaults(CompanySupplyDefaults, _newRequestedOrderSupplyChoices)
            .ToList();
    }

    /// <summary>Company defaults with explicit template choices layered on top.</summary>
    private void SetNewOrderSupplyChoicesFromTemplate(TradeCommissionTemplate template)
    {
        _newRequestedOrderSupplyChoices = TradeCommissionTemplateMapper
            .MergeCompanySupplyDefaults(CompanySupplyDefaults, template.SupplyChoices)
            .ToList();
        _newOrderTemplateSupplyKeys = template.SupplyChoices
            .Select(choice => (choice.ItemId, choice.RequiresHq))
            .ToHashSet();
        _newOrderSupplyDefaultsSeeded = _companyProfile != null;
    }

    private bool IsStagedCompanyDefaultChoice(TradeCommissionTemplateSupplyChoice choice) =>
        !_newOrderTemplateSupplyKeys.Contains((choice.ItemId, choice.RequiresHq)) &&
        IsCompanySupplyDefault(choice.ItemId, choice.RequiresHq);

    private string FormatStagedSupplyChoiceTitle(TradeCommissionTemplateSupplyChoice choice) =>
        IsStagedCompanyDefaultChoice(choice)
            ? $"{choice.Name}: company default"
            : string.IsNullOrWhiteSpace(_appliedTemplateName)
                ? $"{choice.Name}: supply choice"
                : $"{choice.Name}: from template {_appliedTemplateName}";

    /// <summary>
    /// Company defaults that apply to an order: never one of its requested outputs, which are
    /// always crafted by the crafter.
    /// </summary>
    private IReadOnlyList<TradeCommissionTemplateSupplyChoice> GetCompanyDefaultSupplyChoicesFor(
        IEnumerable<int> outputItemIds)
    {
        var outputs = outputItemIds.ToHashSet();
        return TradeCommissionTemplateMapper
            .MergeCompanySupplyDefaults(CompanySupplyDefaults, explicitChoices: null)
            .Where(choice => !outputs.Contains(choice.ItemId))
            .ToArray();
    }

    /// <summary>Drops staged supply choices for the order's own requested outputs (always crafted).</summary>
    private static TradeCommissionTemplateSupplyChoice[] ExcludeOutputSupplyChoices(
        IEnumerable<TradeCommissionTemplateSupplyChoice> choices,
        IEnumerable<TradeRequestedOrderOutput> outputs)
    {
        var outputItemIds = outputs.Select(output => output.ItemId).ToHashSet();
        return choices
            .Where(choice => !outputItemIds.Contains(choice.ItemId))
            .ToArray();
    }
}
