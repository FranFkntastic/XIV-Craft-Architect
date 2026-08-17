using System.Globalization;
using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.LodestoneLookup.Services.ProfileHosting;

public sealed partial class SqliteProfileHostStore
{
    public async Task<IReadOnlyList<HostedProfileObject>> LoadProfileObjectsAsync(
        string profileId,
        string collection,
        IReadOnlyCollection<string> objectIds,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        ValidateCollection(collection);
        ArgumentNullException.ThrowIfNull(objectIds);
        var ids = objectIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (ids.Length == 0)
        {
            return [];
        }
        if (ids.Length > 50)
        {
            throw new ArgumentOutOfRangeException(
                nameof(objectIds),
                "A hosted object batch cannot exceed 50 identities.");
        }

        await EnsureSchemaAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        var parameters = ids.Select((_, index) => $"$objectId{index}").ToArray();
        command.CommandText = $"""
            select o.profile_id,
                   o.object_id,
                   o.payload_json,
                   o.revision,
                   o.updated_at_utc,
                   o.deleted,
                   o.deleted_at_utc
            from sync_objects o
            inner join hosted_profiles p on p.id = o.profile_id
            where p.disabled_at_utc is null
              and o.profile_id = $profileId
              and o.collection = $collection
              and o.object_id in ({string.Join(", ", parameters)})
            order by o.object_id;
            """;
        command.Parameters.AddWithValue("$profileId", profileId);
        command.Parameters.AddWithValue("$collection", collection);
        for (var index = 0; index < ids.Length; index++)
        {
            command.Parameters.AddWithValue(parameters[index], ids[index]);
        }

        var found = new List<HostedProfileObject>(ids.Length);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            found.Add(new HostedProfileObject(
                reader.GetString(0),
                new ProfileSyncObjectEnvelope
                {
                    Collection = collection,
                    ObjectId = reader.GetString(1),
                    PayloadJson = NormalizePortablePayload(
                        collection,
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.GetInt64(5) == 1),
                    Revision = reader.GetInt64(3),
                    UpdatedAtUtc = DateTime.Parse(
                        reader.GetString(4),
                        null,
                        DateTimeStyles.RoundtripKind),
                    Deleted = reader.GetInt64(5) == 1,
                    DeletedAtUtc = reader.IsDBNull(6)
                        ? null
                        : DateTime.Parse(
                            reader.GetString(6),
                            null,
                            DateTimeStyles.RoundtripKind)
                }));
        }

        return found;
    }
}
