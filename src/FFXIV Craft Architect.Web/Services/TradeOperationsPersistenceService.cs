using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Web.Services.ProfileHosting;

namespace FFXIV_Craft_Architect.Web.Services;

public sealed class TradeOperationsPersistenceService
{
    private const string DefaultCompanyProfileName = "FFXIV Trade Company";
    private const string PrototypeDefaultCommissionContact = "franfkntastic";
    private const string SelectedCompanyProfileIdKey = "trade.selected_company_profile_id";

    private readonly IndexedDbService _indexedDb;
    private readonly TradeCompanyProfilePackageService _profilePackageService;
    private readonly TradeOrderArchiveSummaryStore? _archiveSummaries;

    public TradeOperationsPersistenceService(
        IndexedDbService indexedDb,
        TradeCompanyProfilePackageService profilePackageService,
        TradeOrderArchiveSummaryStore? archiveSummaries = null)
    {
        _indexedDb = indexedDb;
        _profilePackageService = profilePackageService;
        _archiveSummaries = archiveSummaries;
    }

    public async Task<TradeCompanyProfile> GetOrCreateActiveCompanyProfileAsync()
    {
        var profiles = await _indexedDb.LoadTradeCompanyProfilesAsync();
        var selectedProfileId = await LoadSelectedCompanyProfileIdAsync();
        var profile = selectedProfileId.HasValue
            ? profiles.FirstOrDefault(candidate => candidate.Id == selectedProfileId.Value)
            : null;
        profile ??= profiles
            .OrderBy(candidate => candidate.CreatedAtUtc)
            .ThenBy(candidate => candidate.Id)
            .FirstOrDefault();
        if (profile != null)
        {
            if (selectedProfileId != profile.Id)
            {
                await SaveSelectedCompanyProfileIdAsync(profile.Id);
            }

            if (NormalizeProfile(profile))
            {
                profile.UpdatedAtUtc = DateTime.UtcNow;
                var migrated = await _indexedDb.SaveTradeCompanyProfileAsync(profile);
                if (!migrated)
                {
                    var diagnostics = await _indexedDb.GetTradeStoreDiagnosticsAsync();
                    throw new InvalidOperationException($"Failed to migrate the active Trade company profile. {diagnostics.ToDisplayMessage()}");
                }
            }

            return profile;
        }

        var now = DateTime.UtcNow;
        profile = TradeCompanyProfile.CreateLocal(DefaultCompanyProfileName, now);
        profile.CommissionContact = PrototypeDefaultCommissionContact;
        var saved = await _indexedDb.SaveTradeCompanyProfileAsync(profile);
        if (!saved)
        {
            var diagnostics = await _indexedDb.GetTradeStoreDiagnosticsAsync();
            throw new InvalidOperationException($"Failed to create the default Trade company profile. {diagnostics.ToDisplayMessage()}");
        }

        await SaveSelectedCompanyProfileIdAsync(profile.Id);
        return profile;
    }

    public async Task<IReadOnlyList<TradeCompanyProfile>> LoadCompanyProfilesAsync()
    {
        var profiles = await _indexedDb.LoadTradeCompanyProfilesAsync();
        foreach (var profile in profiles)
        {
            NormalizeProfile(profile);
        }

        return profiles;
    }

    public async Task<bool> SaveCompanyProfileAsync(TradeCompanyProfile profile)
    {
        NormalizeProfile(profile);
        profile.UpdatedAtUtc = DateTime.UtcNow;
        return await _indexedDb.SaveTradeCompanyProfileAsync(profile);
    }

    public Task<bool> DeleteCompanyProfileAsync(Guid companyProfileId)
    {
        return _indexedDb.DeleteTradeCompanyProfileAsync(companyProfileId);
    }

    public async Task SelectCompanyProfileAsync(Guid companyProfileId)
    {
        var profiles = await _indexedDb.LoadTradeCompanyProfilesAsync();
        if (profiles.All(profile => profile.Id != companyProfileId))
        {
            throw new InvalidOperationException(
                $"Trade company profile '{companyProfileId:D}' does not exist in browser storage.");
        }

        await SaveSelectedCompanyProfileIdAsync(companyProfileId);
    }

    public async Task RequireCompanyProfileAsync(
        Guid companyProfileId,
        string childKind,
        string childId)
    {
        var profiles = await LoadCompanyProfilesAsync();
        if (profiles.All(profile => profile.Id != companyProfileId))
        {
            throw new MissingTradeCompanyProfileException(
                companyProfileId,
                childKind,
                childId);
        }
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

    public Task<bool> DeleteCrafterAsync(Guid crafterId)
    {
        return _indexedDb.DeleteTradeCrafterAsync(crafterId);
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

        await SaveSelectedCompanyProfileIdAsync(imported.Profile.Id);
        return imported;
    }

    public async Task<IReadOnlyList<TradeOrder>> LoadOrdersAsync(Guid companyProfileId)
    {
        return await _indexedDb.LoadTradeOrdersAsync(companyProfileId);
    }

    public async Task<bool> SaveOrderAsync(TradeOrder order)
    {
        if (order.CompanyCommission != null)
        {
            return false;
        }

        order.UpdatedAtUtc = DateTime.UtcNow;
        return await _indexedDb.SaveTradeOrderAsync(order);
    }

    public Task<bool> ApplyCanonicalOrderAsync(TradeOrder order) =>
        _indexedDb.SaveTradeOrderAsync(order);

    public async Task<bool> DeleteOrderAsync(Guid orderId)
    {
        var deleted = await _indexedDb.DeleteTradeOrderAsync(orderId);
        if (deleted && _archiveSummaries != null)
        {
            await _archiveSummaries.RemoveAsync(orderId);
        }
        return deleted;
    }

    private async Task<Guid?> LoadSelectedCompanyProfileIdAsync()
    {
        var serializedId = await _indexedDb.LoadSettingAsync<string>(SelectedCompanyProfileIdKey);
        return Guid.TryParse(serializedId, out var selectedProfileId)
            ? selectedProfileId
            : null;
    }

    private async Task SaveSelectedCompanyProfileIdAsync(Guid companyProfileId)
    {
        if (!await _indexedDb.SaveSettingAsync(
                SelectedCompanyProfileIdKey,
                companyProfileId.ToString("D")))
        {
            throw new InvalidOperationException(
                $"Browser storage could not persist selected Trade company profile '{companyProfileId:D}'.");
        }
    }

    private static bool NormalizeProfile(TradeCompanyProfile profile)
    {
        var changed = false;
        var normalizedPaymentPolicy = TradePaymentPolicyNormalizer.Normalize(
            profile.PaymentPolicy ?? TradePaymentPolicy.LegacyDefault);
        if (profile.PaymentPolicy != normalizedPaymentPolicy)
        {
            profile.PaymentPolicy = normalizedPaymentPolicy;
            changed = true;
        }

        if (profile.SchemaVersion < TradeCompanyProfile.CurrentSchemaVersion)
        {
            if (string.IsNullOrWhiteSpace(profile.CommissionContact))
            {
                profile.CommissionContact = PrototypeDefaultCommissionContact;
            }

            profile.SchemaVersion = TradeCompanyProfile.CurrentSchemaVersion;
            changed = true;
        }

        return changed;
    }
}
