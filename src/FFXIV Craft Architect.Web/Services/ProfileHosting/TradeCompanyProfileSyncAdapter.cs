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

        if (!await _tradeOperations.SaveCompanyProfileAsync(profile))
        {
            throw new InvalidOperationException(
                $"Browser storage could not apply hosted Trade company profile '{envelope.ObjectId}'.");
        }
    }

    public async Task DeleteLocalObjectAsync(string objectId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!Guid.TryParse(objectId, out var companyProfileId))
        {
            throw new InvalidOperationException(
                $"Hosted Trade company profile ID '{objectId}' is invalid.");
        }

        if (!await _tradeOperations.DeleteCompanyProfileAsync(companyProfileId))
        {
            throw new InvalidOperationException(
                $"Browser storage could not delete hosted Trade company profile '{objectId}'.");
        }
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
