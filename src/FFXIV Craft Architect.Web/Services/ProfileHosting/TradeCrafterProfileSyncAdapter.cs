using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Web.Services.ProfileHosting;

public sealed class TradeCrafterProfileSyncAdapter : IProfileSyncCollectionAdapter
{
    private static readonly JsonSerializerOptions JsonOptions =
        ProfileSyncJson.CreateOptions();
    private readonly TradeOperationsPersistenceService _tradeOperations;

    public TradeCrafterProfileSyncAdapter(TradeOperationsPersistenceService tradeOperations)
    {
        _tradeOperations = tradeOperations;
    }

    public string Collection => ProfileSyncCollections.TradeCrafters;

    public async Task<IReadOnlyList<ProfileSyncObjectEnvelope>> LoadLocalObjectsAsync(CancellationToken ct)
    {
        var profiles = await _tradeOperations.LoadCompanyProfilesAsync();
        var crafters = new List<TradeCrafterProfile>();
        foreach (var profile in profiles.OrderBy(profile => profile.Id))
        {
            ct.ThrowIfCancellationRequested();
            crafters.AddRange(await _tradeOperations.LoadCraftersAsync(profile.Id));
        }

        var now = DateTime.UtcNow;
        return crafters.Select(crafter => ToEnvelope(crafter, now)).ToArray();
    }

    public async Task ApplyRemoteObjectAsync(ProfileSyncObjectEnvelope envelope, CancellationToken ct)
    {
        var crafter = JsonSerializer.Deserialize<TradeCrafterProfile>(
            envelope.PayloadJson,
            JsonOptions);
        if (crafter == null)
        {
            throw new InvalidOperationException($"Hosted Trade crafter payload '{envelope.ObjectId}' could not be deserialized.");
        }

        await _tradeOperations.RequireCompanyProfileAsync(
            crafter.CompanyProfileId,
            "crafter",
            envelope.ObjectId);
        if (!await _tradeOperations.SaveCrafterAsync(crafter))
        {
            throw new InvalidOperationException(
                $"Browser storage could not apply hosted Trade crafter '{envelope.ObjectId}'.");
        }
    }

    public async Task DeleteLocalObjectAsync(string objectId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!Guid.TryParse(objectId, out var crafterId))
        {
            throw new InvalidOperationException(
                $"Hosted Trade crafter ID '{objectId}' is invalid.");
        }

        if (!await _tradeOperations.DeleteCrafterAsync(crafterId))
        {
            throw new InvalidOperationException(
                $"Browser storage could not delete hosted Trade crafter '{objectId}'.");
        }
    }

    private static ProfileSyncObjectEnvelope ToEnvelope(TradeCrafterProfile crafter, DateTime updatedAtUtc)
    {
        return new ProfileSyncObjectEnvelope
        {
            Collection = ProfileSyncCollections.TradeCrafters,
            ObjectId = crafter.Id.ToString("D"),
            PayloadJson = JsonSerializer.Serialize(crafter, JsonOptions),
            UpdatedAtUtc = updatedAtUtc
        };
    }
}
