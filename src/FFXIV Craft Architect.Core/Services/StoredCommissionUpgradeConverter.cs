using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services;

/// <summary>
/// Stored-data upgrade only: shared by host sync payloads and browser IndexedDB reads.
/// Never apply this converter to command input. Retain until all persisted pre-account
/// commissions (including archives and offline browsers) have been rewritten.
/// </summary>
public sealed class StoredCommissionUpgradeConverter : JsonConverter<TradeCompanyCommission>
{
    public override TradeCompanyCommission? Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var stored = JsonNode.Parse(ref reader);
        TradeCompanyCommissionMigrationService.UpgradeStoredClaimIdentity(stored);
        return stored.Deserialize<TradeCompanyCommission>(options);
    }

    public override void Write(
        Utf8JsonWriter writer, TradeCompanyCommission value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value, options);
}
