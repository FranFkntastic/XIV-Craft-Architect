using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Web.Dialogs;
using FFXIV_Craft_Architect.Web.Services;
using MudBlazor;

namespace FFXIV_Craft_Architect.Web.Pages;

public partial class TradeOrders
{
    private void ShowCompanyPricingRequired(TradeOrderPricingWorkflowResult result)
    {
        Snackbar.Add(result.Message, Severity.Warning, options =>
        {
            options.Action = "Company pricing";
            options.Onclick = async _ => await OpenCommissionPricingSettingsAsync();
        });
    }

    private bool CompanyQuoteScopeChanged =>
        _selectedOrder is { } order &&
        _companyProfile?.CommissionPricingScope is { IsValid: true } scope &&
        (!string.Equals(order.SourceSnapshot.Region, scope.Region, StringComparison.OrdinalIgnoreCase) ||
         order.SourceSnapshot.MarketFetchScope != scope.MarketFetchScope ||
         scope.MarketFetchScope == MarketFetchScope.SelectedDataCenter &&
         !string.Equals(order.SourceSnapshot.DataCenter, scope.DataCenter, StringComparison.OrdinalIgnoreCase));

    private async Task OpenCommissionPricingSettingsAsync()
    {
        var parameters = new DialogParameters
        {
            [nameof(SettingsDialog.InitialSection)] = SettingsSection.CompanyAdministration,
            [nameof(SettingsDialog.InitialWorkspaceCompanyId)] = _selectedOrder?.CompanyProfileId.ToString("D") ?? _companyProfile?.Id.ToString("D")
        };
        var dialog = await DialogService.ShowAsync<SettingsDialog>("Settings", parameters);
        await dialog.Result;
        if (_companyProfile != null)
        {
            _companyProfile = ProfileSync.CurrentStatus.IsConnected
                ? (await CompanyHubs.LoadWorkspaceProfileAsync(_companyProfile.Id)).ToTransientProfile()
                : (await TradeOperationsPersistence.LoadCompanyProfilesAsync())
                    .FirstOrDefault(profile => profile.Id == _companyProfile.Id);
        }
    }
}
