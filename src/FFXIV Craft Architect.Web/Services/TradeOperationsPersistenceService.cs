using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Web.Services;

public sealed class TradeOperationsPersistenceService
{
    private const string DefaultCompanyProfileName = "FFXIV Trade Company";

    private readonly IndexedDbService _indexedDb;
    private readonly TradeCompanyProfilePackageService _profilePackageService;

    public TradeOperationsPersistenceService(
        IndexedDbService indexedDb,
        TradeCompanyProfilePackageService profilePackageService)
    {
        _indexedDb = indexedDb;
        _profilePackageService = profilePackageService;
    }

    public async Task<TradeCompanyProfile> GetOrCreateActiveCompanyProfileAsync()
    {
        var profiles = await _indexedDb.LoadTradeCompanyProfilesAsync();
        var profile = profiles
            .OrderByDescending(profile => profile.UpdatedAtUtc)
            .FirstOrDefault();
        if (profile != null)
        {
            profile.PaymentPolicy ??= TradePaymentPolicy.LegacyDefault;
            return profile;
        }

        var now = DateTime.UtcNow;
        profile = TradeCompanyProfile.CreateLocal(DefaultCompanyProfileName, now);
        var saved = await _indexedDb.SaveTradeCompanyProfileAsync(profile);
        if (!saved)
        {
            var diagnostics = await _indexedDb.GetTradeStoreDiagnosticsAsync();
            throw new InvalidOperationException($"Failed to create the default Trade company profile. {diagnostics.ToDisplayMessage()}");
        }

        return profile;
    }

    public async Task<IReadOnlyList<TradeCompanyProfile>> LoadCompanyProfilesAsync()
    {
        var profiles = await _indexedDb.LoadTradeCompanyProfilesAsync();
        foreach (var profile in profiles)
        {
            profile.PaymentPolicy ??= TradePaymentPolicy.LegacyDefault;
        }

        return profiles;
    }

    public async Task<bool> SaveCompanyProfileAsync(TradeCompanyProfile profile)
    {
        profile.PaymentPolicy ??= TradePaymentPolicy.LegacyDefault;
        profile.UpdatedAtUtc = DateTime.UtcNow;
        return await _indexedDb.SaveTradeCompanyProfileAsync(profile);
    }

    public async Task<IReadOnlyList<TradeCrafterProfile>> LoadCraftersAsync(Guid companyProfileId)
    {
        return await _indexedDb.LoadTradeCraftersAsync(companyProfileId);
    }

    public async Task<bool> SaveCrafterAsync(TradeCrafterProfile crafter)
    {
        crafter.UpdatedAtUtc = DateTime.UtcNow;
        return await _indexedDb.SaveTradeCrafterAsync(crafter);
    }

    public async Task<TradeCompanyProfilePackage> ExportActiveCompanyProfilePackageAsync(DateTime exportedAtUtc)
    {
        var profile = await GetOrCreateActiveCompanyProfileAsync();
        var crafters = await LoadCraftersAsync(profile.Id);
        return _profilePackageService.CreateExportPackage(profile, crafters, exportedAtUtc);
    }

    public async Task<TradeCompanyProfileImportResult> ImportCompanyProfilePackageAsync(
        TradeCompanyProfilePackage package,
        DateTime importedAtUtc)
    {
        var imported = _profilePackageService.ImportAsNewProfile(package, importedAtUtc);
        var profileSaved = await _indexedDb.SaveTradeCompanyProfileAsync(imported.Profile);
        if (!profileSaved)
        {
            var diagnostics = await _indexedDb.GetTradeStoreDiagnosticsAsync();
            throw new InvalidOperationException($"Failed to import the Trade company profile. {diagnostics.ToDisplayMessage()}");
        }

        foreach (var crafter in imported.Crafters)
        {
            var crafterSaved = await _indexedDb.SaveTradeCrafterAsync(crafter);
            if (!crafterSaved)
            {
                throw new InvalidOperationException($"Failed to import crafter '{crafter.DisplayName}' into the Trade company profile.");
            }
        }

        return imported;
    }

    public async Task<IReadOnlyList<TradeOrder>> LoadOrdersAsync(Guid companyProfileId)
    {
        return await _indexedDb.LoadTradeOrdersAsync(companyProfileId);
    }

    public async Task<bool> SaveOrderAsync(TradeOrder order)
    {
        order.UpdatedAtUtc = DateTime.UtcNow;
        return await _indexedDb.SaveTradeOrderAsync(order);
    }
}
