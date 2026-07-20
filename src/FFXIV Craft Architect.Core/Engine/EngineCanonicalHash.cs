using System.Security.Cryptography;
using System.Text.Json;

namespace FFXIV_Craft_Architect.Core.Engine;

public static class EngineCanonicalHash
{
    public static string Compute<T>(T value, JsonSerializerOptions? options = null)
    {
        using var document = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(value, options));
        return Compute(document.RootElement);
    }

    public static string Compute(JsonElement value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            WriteCanonical(writer, value);
        }

        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }

    public static string ComputeEngineInput(JsonElement value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteCanonical(writer, value);
        }
        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }

    public static string ComputeUnordered<T>(IEnumerable<T> values, JsonSerializerOptions? options = null)
    {
        var elementHashes = values
            .Select(value => Compute(value, options))
            .OrderBy(hash => hash, StringComparer.Ordinal)
            .ToArray();
        return Compute(elementHashes, options);
    }

    public static string ComputeFinalTransactionHash(
        EngineRequestEnvelope request,
        EngineTerminalStatus status,
        string analysisResultHash,
        string procurementRouteResultHash,
        IReadOnlyDictionary<string, string> terminalEvidence)
    {
        var stableResult = new
        {
            request.ContractVersion,
            request.TransactionId,
            request.InputKind,
            request.Basis,
            request.Settings,
            request.Budgets,
            InputHash = ComputeEngineInput(request.Input),
            request.RootIntentHash,
            request.ExpandedGraphHash,
            request.AnalysisBasisHash,
            request.RouteBasisHash,
            AnalysisResultHash = analysisResultHash,
            ProcurementRouteResultHash = procurementRouteResultHash,
            Status = status,
            TerminalEvidence = terminalEvidence
        };
        return Compute(stableResult);
    }

    private static int CountSignificantDigits(string token)
    {
        var exponentIndex = token.IndexOfAny(['e', 'E']);
        var mantissa = exponentIndex >= 0 ? token[..exponentIndex] : token;
        var digits = mantissa.Where(char.IsDigit).SkipWhile(character => character == '0').Count();
        return Math.Max(1, digits);
    }

    private static void WriteCanonical(
        Utf8JsonWriter writer,
        JsonElement element,
        bool sortArray = false)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var properties = element.EnumerateObject()
                    .OrderBy(property => property.Name, StringComparer.Ordinal)
                    .ToArray();
                for (var index = 1; index < properties.Length; index++)
                {
                    if (string.Equals(properties[index - 1].Name, properties[index].Name, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException($"Duplicate JSON property '{properties[index].Name}' cannot be canonically hashed.");
                    }
                }

                writer.WriteStartObject();
                foreach (var property in properties)
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(
                        writer,
                        property.Value,
                        property.Name is "BlacklistedWorlds" or "ExcludedItemWorlds" or "blacklistedWorlds" or "excludedItemWorlds");
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                var items = element.EnumerateArray().ToArray();
                if (sortArray)
                {
                    items = items
                        .Select(item => (Item: item, Hash: Compute(item)))
                        .OrderBy(item => item.Hash, StringComparer.Ordinal)
                        .DistinctBy(item => item.Hash, StringComparer.Ordinal)
                        .Select(item => item.Item)
                        .ToArray();
                }
                foreach (var item in items)
                {
                    WriteCanonical(writer, item);
                }
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                if (element.TryGetInt64(out var integer))
                {
                    writer.WriteNumberValue(integer);
                }
                else if (element.TryGetDecimal(out var decimalValue))
                {
                    if (CountSignificantDigits(element.GetRawText()) > 29)
                    {
                        throw new NotSupportedException($"JSON number '{element.GetRawText()}' exceeds the canonical decimal precision.");
                    }
                    writer.WriteRawValue(decimalValue.ToString("G29", System.Globalization.CultureInfo.InvariantCulture));
                }
                else
                {
                    throw new NotSupportedException($"JSON number '{element.GetRawText()}' exceeds the canonical decimal domain.");
                }
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                writer.WriteNullValue();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(element), element.ValueKind, "Unsupported JSON value kind.");
        }
    }
}
