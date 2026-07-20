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
            InputHash = Compute(request.Input),
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

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
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
                    writer.WriteRawValue(decimalValue.ToString("G29", System.Globalization.CultureInfo.InvariantCulture));
                }
                else
                {
                    writer.WriteNumberValue(element.GetDouble());
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
