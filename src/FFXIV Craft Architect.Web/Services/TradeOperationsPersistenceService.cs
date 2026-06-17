using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Web.Services;

public sealed class TradeOperationsPersistenceService
{
    private const string DefaultCompanyProfileName = "FFXIV Trade Company";

    private readonly IndexedDbService _indexedDb;

    public TradeOperationsPersistenceService(IndexedDbService indexedDb)
    {
        _indexedDb = indexedDb;
    }

    public async Task<TradeCompanyProfile> GetOrCreateActiveCompanyProfileAsync()
    {
        var profiles = await _indexedDb.LoadTradeCompanyProfilesAsync();
        var profile = profiles
            .OrderByDescending(profile => profile.UpdatedAtUtc)
            .FirstOrDefault();
        if (profile != null)
        {
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

    public async Task<IReadOnlyList<TradeCrafterProfile>> LoadCraftersAsync(Guid companyProfileId)
    {
        return await _indexedDb.LoadTradeCraftersAsync(companyProfileId);
    }

    public async Task<bool> SaveCrafterAsync(TradeCrafterProfile crafter)
    {
        crafter.UpdatedAtUtc = DateTime.UtcNow;
        return await _indexedDb.SaveTradeCrafterAsync(crafter);
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
