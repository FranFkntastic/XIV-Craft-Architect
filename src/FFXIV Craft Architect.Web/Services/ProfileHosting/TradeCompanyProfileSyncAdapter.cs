using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Web.Services.ProfileHosting;

public sealed class TradeCompanyProfileSyncAdapter : IProfileSyncCollectionAdapter
{
    private readonly TradeOperationsPersistenceService _tradeOperations;

    public TradeCompanyProfileSyncAdapter(TradeOperationsPersistenceService tradeOperations)
    {
        _tradeOperations = tradeOperations;
    }

    public string Collection => ProfileSyncCollections.TradeCompanyProfiles;

    public async Task<IReadOnlyList<ProfileSyncObjectEnvelope>> LoadLocalObjectsAsync(CancellationToken ct)
    {
        var profiles = await _tradeOperations.LoadCompanyProfilesAsync();
        var now = DateTime.UtcNow;
        return profiles.Select(profile => ToEnvelope(profile, now)).ToArray();
    }

    public async Task ApplyRemoteObjectAsync(ProfileSyncObjectEnvelope envelope, CancellationToken ct)
    {
        var profile = JsonSerializer.Deserialize<TradeCompanyProfile>(envelope.PayloadJson);
        if (profile == null)
        {
            throw new InvalidOperationException($"Hosted Trade company profile payload '{envelope.ObjectId}' could not be deserialized.");
        }

        await _tradeOperations.SaveCompanyProfileAsync(profile);
    }

    public Task DeleteLocalObjectAsync(string objectId, CancellationToken ct)
    {
        throw new NotSupportedException("Deleting Trade company profiles through hosted sync is not supported in v1.");
    }

    private static ProfileSyncObjectEnvelope ToEnvelope(TradeCompanyProfile profile, DateTime updatedAtUtc)
    {
        return new ProfileSyncObjectEnvelope
        {
            Collection = ProfileSyncCollections.TradeCompanyProfiles,
            ObjectId = profile.Id.ToString("D"),
            PayloadJson = JsonSerializer.Serialize(profile),
            UpdatedAtUtc = updatedAtUtc
        };
    }
}
