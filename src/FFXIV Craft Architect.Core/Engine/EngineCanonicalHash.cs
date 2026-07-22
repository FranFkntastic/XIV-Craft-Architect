using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;

namespace FFXIV_Craft_Architect.Core.Engine;

public static class EngineCanonicalHash
{
    public static string Compute<T>(T value, JsonSerializerOptions? options = null)
    {
        using var document = JsonSerializer.SerializeToDocument(value, options);
        return Compute(document.RootElement);
    }

    public static string Compute(JsonElement value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
        {
            WriteCanonical(writer, value);
        }

        return Convert.ToHexString(SHA256.HashData(buffer.WrittenSpan)).ToLowerInvariant();
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

    public static async ValueTask<string> ComputeEngineInputAsync(
        JsonElement value,
        Func<CancellationToken, ValueTask>? cooperativeYield = null,
        CancellationToken cancellationToken = default)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
        {
            var state = new CooperativeCanonicalWriteState(cooperativeYield, cancellationToken);
            await WriteCanonicalAsync(writer, value, state);
        }

        return Convert.ToHexString(SHA256.HashData(buffer.WrittenSpan)).ToLowerInvariant();
    }

    public static string ResolveEngineInputHash(EngineRequestEnvelope request) =>
        string.IsNullOrWhiteSpace(request.InputHash)
            ? ComputeEngineInput(request.Input)
            : request.InputHash;

    public static void ValidateBoundEngineInputHash(EngineRequestEnvelope request)
    {
        if (!string.IsNullOrWhiteSpace(request.InputHash) &&
            !string.Equals(request.InputHash, ComputeEngineInput(request.Input), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The bound engine input hash does not match the authoritative input.");
        }
    }

    public static async ValueTask<string> ValidateAndComputeRequestIdentityAsync(
        EngineRequestEnvelope request,
        Func<CancellationToken, ValueTask>? cooperativeYield = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var inputHash = await ComputeEngineInputAsync(
            request.Input,
            cooperativeYield,
            cancellationToken);
        if (!string.IsNullOrWhiteSpace(request.InputHash) &&
            !string.Equals(request.InputHash, inputHash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The bound engine input hash does not match the authoritative input.");
        }

        return ComputeRequestIdentity(request, inputHash);
    }

    public static string ComputeRequestIdentity(EngineRequestEnvelope request) =>
        ComputeRequestIdentity(request, ResolveEngineInputHash(request));

    private static string ComputeRequestIdentity(EngineRequestEnvelope request, string inputHash) =>
        Compute(new
        {
            Domain = "engine-request-identity-v2",
            request.ContractVersion,
            request.TransactionId,
            request.InputKind,
            request.Basis,
            request.Settings,
            request.Budgets,
            InputHash = inputHash,
            request.RootIntentHash,
            request.ExpandedGraphHash,
            request.AnalysisBasisHash,
            request.RouteBasisHash
        });

    public static string ComputeAuthoritativeResultPayloadHash(
        EngineAnalysisSemanticSnapshot? analysis,
        EngineRouteSemanticSnapshot? route) =>
        Compute(new
        {
            Domain = "engine-authoritative-result-v1",
            MarketAnalysis = analysis,
            ProcurementRoute = route
        });

    public static string ComputeAuthoritativeResultPayloadHash(
        string analysisResultHash,
        string procurementRouteResultHash) =>
        Compute(new
        {
            Domain = "engine-authoritative-result-v2",
            AnalysisResultHash = analysisResultHash,
            ProcurementRouteResultHash = procurementRouteResultHash
        });

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
        terminalEvidence = EngineEvidenceSnapshots.Freeze(terminalEvidence);
        var stableResult = new
        {
            request.ContractVersion,
            request.TransactionId,
            request.InputKind,
            request.Basis,
            request.Settings,
            request.Budgets,
            InputHash = ResolveEngineInputHash(request),
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

    public static string ComputeRequestValidationFailureIdentity(EngineRequestEnvelope request) =>
        Compute(new
        {
            Domain = "engine-invalid-request-identity-v1",
            request.ContractVersion,
            request.TransactionId
        });

    public static string ComputeRequestValidationFailureHash(
        EngineRequestEnvelope request,
        EngineTerminalStatus status,
        IReadOnlyDictionary<string, string> terminalEvidence)
    {
        terminalEvidence = EngineEvidenceSnapshots.Freeze(terminalEvidence);
        return Compute(new
        {
            Domain = "engine-invalid-request-terminal-v1",
            request.ContractVersion,
            request.TransactionId,
            Status = status,
            TerminalEvidence = terminalEvidence
        });
    }

    public static string ComputeComputationHash(
        long generation,
        Guid executionId,
        EngineRequestEnvelope request,
        EngineComputationStatus status,
        EnginePhase finalPhase,
        string resultPayloadHash,
        string analysisResultHash,
        string procurementRouteResultHash,
        IReadOnlyDictionary<string, string> computationEvidence,
        EngineFailure? failure)
    {
        computationEvidence = EngineEvidenceSnapshots.Freeze(computationEvidence);
        var boundComputation = new
        {
            Domain = "engine-computation-v1",
            Generation = generation,
            ExecutionId = executionId,
            request.ContractVersion,
            request.TransactionId,
            request.InputKind,
            request.Basis,
            request.Settings,
            request.Budgets,
            InputHash = ResolveEngineInputHash(request),
            request.RootIntentHash,
            request.ExpandedGraphHash,
            request.AnalysisBasisHash,
            request.RouteBasisHash,
            Status = status,
            FinalPhase = finalPhase,
            ResultPayloadHash = resultPayloadHash,
            AnalysisResultHash = analysisResultHash,
            ProcurementRouteResultHash = procurementRouteResultHash,
            ComputationEvidence = computationEvidence,
            Failure = failure
        };
        return Compute(boundComputation);
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

    private static async ValueTask WriteCanonicalAsync(
        Utf8JsonWriter writer,
        JsonElement element,
        CooperativeCanonicalWriteState state,
        bool sortArray = false)
    {
        await state.AdvanceAsync();
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
                    await WriteCanonicalAsync(
                        writer,
                        property.Value,
                        state,
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
                    await WriteCanonicalAsync(writer, item, state);
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

    private sealed class CooperativeCanonicalWriteState(
        Func<CancellationToken, ValueTask>? cooperativeYield,
        CancellationToken cancellationToken)
    {
        private const int YieldInterval = 64;
        private int _nodes;

        public async ValueTask AdvanceAsync()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (cooperativeYield is not null && ++_nodes % YieldInterval == 0)
            {
                await cooperativeYield(cancellationToken);
            }
        }
    }
}
