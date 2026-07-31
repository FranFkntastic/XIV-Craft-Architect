using System.Text.Json;
using System.Text.Json.Serialization;

namespace FFXIV_Craft_Architect.Core.Models;

[JsonConverter(typeof(CompanyIdJsonConverter))]
public readonly record struct CompanyId
{
    public CompanyId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("Company IDs cannot be empty.", nameof(value));
        }

        Value = value;
    }

    public Guid Value { get; }

    public static CompanyId Parse(string value)
    {
        if (!TryParse(value, out var companyId))
        {
            throw new FormatException("The company ID is not a non-empty GUID.");
        }

        return companyId;
    }

    public static bool TryParse(string? value, out CompanyId companyId)
    {
        if (Guid.TryParse(value, out var parsed) && parsed != Guid.Empty)
        {
            companyId = new CompanyId(parsed);
            return true;
        }

        companyId = default;
        return false;
    }

    public override string ToString() => Value.ToString("D");
}

public sealed class CompanyIdJsonConverter : JsonConverter<CompanyId>
{
    public override CompanyId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String ||
            !CompanyId.TryParse(reader.GetString(), out var companyId))
        {
            throw new JsonException("Company IDs must be non-empty GUID strings.");
        }

        return companyId;
    }

    public override void Write(Utf8JsonWriter writer, CompanyId value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToString());
    }
}

[JsonConverter(typeof(CompanyRecordRevisionJsonConverter))]
public readonly record struct CompanyRecordRevision
{
    public CompanyRecordRevision(long value)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Company record revisions cannot be negative.");
        }

        Value = value;
    }

    public long Value { get; }

    public static CompanyRecordRevision None => new(0);

    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

public sealed class CompanyRecordRevisionJsonConverter : JsonConverter<CompanyRecordRevision>
{
    public override CompanyRecordRevision Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt64(out var value) || value < 0)
        {
            throw new JsonException("Company record revisions must be non-negative integers.");
        }

        return new CompanyRecordRevision(value);
    }

    public override void Write(
        Utf8JsonWriter writer,
        CompanyRecordRevision value,
        JsonSerializerOptions options)
    {
        writer.WriteNumberValue(value.Value);
    }
}

public enum TradeCompanyRole
{
    ReadOnly,
    Operator,
    Owner
}

public sealed record TradeCompanyAccessContext(
    CompanyId CompanyId,
    Guid GrantId,
    TradeCompanyRole Role,
    Guid? HostProfileId = null);

public static class TradeCompanyRecordKinds
{
    public const string Crafter = "crafter";
    public const string Order = "order";
    public const string Publication = "publication";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(
        [
            Crafter,
            Order,
            Publication
        ],
        StringComparer.Ordinal);
}

public sealed record TradeCompanyRecordEnvelope(
    CompanyId CompanyId,
    string RecordKind,
    string RecordId,
    string PayloadJson,
    CompanyRecordRevision RecordRevision,
    DateTime UpdatedAtUtc,
    bool Deleted = false,
    DateTime? DeletedAtUtc = null);

public enum TradeCompanyMutationStatus
{
    Applied,
    Replayed,
    Conflict,
    Rejected
}

public sealed record TradeCompanyMutationResult(
    TradeCompanyMutationStatus Status,
    TradeCompanyRecordEnvelope? Record,
    TradeCompanyRecordEnvelope? CurrentRecord = null,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    CompanyRecordRevision? CompanyRevision = null)
{
    public bool Success => Status is TradeCompanyMutationStatus.Applied or TradeCompanyMutationStatus.Replayed;
}

public sealed record TradeCompanyPublicationOwnership(
    CompanyId CompanyId,
    Guid OrderId,
    CompanyRecordRevision OrderRevision);
