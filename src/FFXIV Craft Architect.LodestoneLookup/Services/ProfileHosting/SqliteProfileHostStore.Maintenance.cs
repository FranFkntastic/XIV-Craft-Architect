using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.LodestoneLookup.Services.ProfileHosting;

public sealed partial class SqliteProfileHostStore
{
    // Rewrites a deep-archived order in place (maintenance only). The summary is
    // re-derived so archive search results stay consistent with the payload, and the
    // write only applies when the stored payload is still the one the caller read.
    public async Task<bool> ReplaceDeepArchivedOrderPayloadAsync(
        string profileId,
        string objectId,
        string expectedPayloadJson,
        string payloadJson,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);
        var summary = TradeOrderArchiveSummaryCodec.TryCreate(payloadJson, objectId);
        if (summary == null)
        {
            return false;
        }

        await EnsureSchemaAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            update deep_archived_trade_orders
            set payload_json = $payloadJson,
                summary_json = $summaryJson
            where profile_id = $profileId
              and object_id = $objectId
              and payload_json = $expectedPayloadJson;
            """;
        command.Parameters.AddWithValue("$profileId", profileId);
        command.Parameters.AddWithValue("$objectId", objectId);
        command.Parameters.AddWithValue("$payloadJson", payloadJson);
        command.Parameters.AddWithValue("$summaryJson", TradeOrderArchiveSummaryCodec.Serialize(summary));
        command.Parameters.AddWithValue("$expectedPayloadJson", expectedPayloadJson);
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }
}
