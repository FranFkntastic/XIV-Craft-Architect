using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Web.Services.ProfileHosting;

public sealed class TradeCrafterProfileSyncAdapter : IProfileSyncCollectionAdapter
{
    private readonly TradeOperationsPersistenceService _tradeOperations;

    public TradeCrafterProfileSyncAdapter(TradeOperationsPersistenceService tradeOperations)
    {
        _tradeOperations = tradeOperations;
    }

    public string Collection => ProfileSyncCollections.TradeCrafters;

    public async Task<IReadOnlyList<ProfileSyncObjectEnvelope>> LoadLocalObjectsAsync(CancellationToken ct)
    {
        var profile = await _tradeOperations.GetOrCreateActiveCompanyProfileAsync();
        var crafters = await _tradeOperations.LoadCraftersAsync(profile.Id);
        var now = DateTime.UtcNow;
        return crafters.Select(crafter => ToEnvelope(crafter, now)).ToArray();
    }

    public async Task ApplyRemoteObjectAsync(ProfileSyncObjectEnvelope envelope, CancellationToken ct)
    {
        var crafter = JsonSerializer.Deserialize<TradeCrafterProfile>(envelope.PayloadJson);
        if (crafter == null)
        {
            throw new InvalidOperationException($"Hosted Trade crafter payload '{envelope.ObjectId}' could not be deserialized.");
        }

        await _tradeOperations.SaveCrafterAsync(crafter);
    }

    public Task DeleteLocalObjectAsync(string objectId, CancellationToken ct)
    {
        throw new NotSupportedException("Deleting Trade crafters through hosted sync is not supported in v1.");
    }

    private static ProfileSyncObjectEnvelope ToEnvelope(TradeCrafterProfile crafter, DateTime updatedAtUtc)
    {
        return new ProfileSyncObjectEnvelope
        {
            Collection = ProfileSyncCollections.TradeCrafters,
            ObjectId = crafter.Id.ToString("D"),
            PayloadJson = JsonSerializer.Serialize(crafter),
            UpdatedAtUtc = updatedAtUtc
        };
    }
}
