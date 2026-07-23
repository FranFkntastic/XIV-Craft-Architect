using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;
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
        var hash = new StreamingSha256();
        using var buffer = new IncrementalHashBufferWriter(hash);
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
        {
            WriteCanonical(writer, value);
        }

        return FormatHash(hash.GetHash());
    }

    public static string ComputeEngineInput(JsonElement value)
    {
        var hash = new StreamingSha256();
        using var buffer = new IncrementalHashBufferWriter(hash);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteCanonical(writer, value);
        }
        return FormatHash(hash.GetHash());
    }

    public static async ValueTask<string> ComputeEngineInputAsync(
        JsonElement value,
        Func<CancellationToken, ValueTask>? cooperativeYield = null,
        CancellationToken cancellationToken = default)
    {
        var hash = new StreamingSha256();
        using var buffer = new IncrementalHashBufferWriter(hash);
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
        {
            var state = new CooperativeCanonicalWriteState(cooperativeYield, cancellationToken);
            await WriteCanonicalAsync(writer, value, state);
        }

        return FormatHash(hash.GetHash());
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

    private static string FormatHash(byte[] hash) =>
        Convert.ToHexString(hash).ToLowerInvariant();

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
        var frames = new Stack<CanonicalWriteFrame>();
        var pending = element;
        var pendingSortArray = sortArray;
        var hasPending = true;
        while (hasPending || frames.Count > 0)
        {
            if (hasPending)
            {
                if (state.Advance())
                {
                    await state.YieldAsync();
                }

                switch (pending.ValueKind)
                {
                    case JsonValueKind.Object:
                        var properties = pending.EnumerateObject()
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
                        frames.Push(CanonicalWriteFrame.ForObject(properties));
                        break;
                    case JsonValueKind.Array:
                        writer.WriteStartArray();
                        var arrayItems = pending.EnumerateArray().ToArray();
                        if (pendingSortArray)
                        {
                            arrayItems = arrayItems
                                .Select(item => (Item: item, Hash: Compute(item)))
                                .OrderBy(item => item.Hash, StringComparer.Ordinal)
                                .DistinctBy(item => item.Hash, StringComparer.Ordinal)
                                .Select(item => item.Item)
                                .ToArray();
                        }
                        frames.Push(CanonicalWriteFrame.ForArray(arrayItems));
                        break;
                    case JsonValueKind.String:
                        writer.WriteStringValue(pending.GetString());
                        break;
                    case JsonValueKind.Number:
                        if (pending.TryGetInt64(out var integer))
                        {
                            writer.WriteNumberValue(integer);
                        }
                        else if (pending.TryGetDecimal(out var decimalValue))
                        {
                            if (CountSignificantDigits(pending.GetRawText()) > 29)
                            {
                                throw new NotSupportedException($"JSON number '{pending.GetRawText()}' exceeds the canonical decimal precision.");
                            }
                            writer.WriteRawValue(decimalValue.ToString("G29", System.Globalization.CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            throw new NotSupportedException($"JSON number '{pending.GetRawText()}' exceeds the canonical decimal domain.");
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
                        throw new ArgumentOutOfRangeException(nameof(element), pending.ValueKind, "Unsupported JSON value kind.");
                }

                hasPending = false;
                continue;
            }

            var frame = frames.Pop();
            if (frame.Kind == JsonValueKind.Object)
            {
                var properties = frame.Properties!;
                if (frame.Index >= properties.Length)
                {
                    writer.WriteEndObject();
                    continue;
                }

                var property = properties[frame.Index];
                frames.Push(frame with { Index = frame.Index + 1 });
                writer.WritePropertyName(property.Name);
                pending = property.Value;
                pendingSortArray = property.Name is
                    "BlacklistedWorlds" or "ExcludedItemWorlds" or
                    "blacklistedWorlds" or "excludedItemWorlds";
                hasPending = true;
                continue;
            }

            var frameItems = frame.Items!;
            if (frame.Index >= frameItems.Length)
            {
                writer.WriteEndArray();
                continue;
            }

            frames.Push(frame with { Index = frame.Index + 1 });
            pending = frameItems[frame.Index];
            pendingSortArray = false;
            hasPending = true;
        }
    }

    private readonly record struct CanonicalWriteFrame(
        JsonValueKind Kind,
        JsonProperty[]? Properties,
        JsonElement[]? Items,
        int Index)
    {
        public static CanonicalWriteFrame ForObject(JsonProperty[] properties) =>
            new(JsonValueKind.Object, properties, null, 0);

        public static CanonicalWriteFrame ForArray(JsonElement[] items) =>
            new(JsonValueKind.Array, null, items, 0);
    }

    private sealed class CooperativeCanonicalWriteState(
        Func<CancellationToken, ValueTask>? cooperativeYield,
        CancellationToken cancellationToken)
    {
        private const int YieldInterval = 4096;
        private int _nodes;

        public bool Advance()
        {
            cancellationToken.ThrowIfCancellationRequested();
            return cooperativeYield is not null && ++_nodes % YieldInterval == 0;
        }

        public ValueTask YieldAsync() =>
            cooperativeYield!(cancellationToken);
    }

    private sealed class IncrementalHashBufferWriter : IBufferWriter<byte>, IDisposable
    {
        private const int DefaultBufferSize = 64 * 1024;
        private readonly StreamingSha256 _hash;
        private byte[]? _buffer;

        public IncrementalHashBufferWriter(StreamingSha256 hash)
        {
            _hash = hash;
            _buffer = ArrayPool<byte>.Shared.Rent(DefaultBufferSize);
        }

        public void Advance(int count)
        {
            var buffer = _buffer ?? throw new ObjectDisposedException(nameof(IncrementalHashBufferWriter));
            if ((uint)count > (uint)buffer.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }
            _hash.Append(buffer.AsSpan(0, count));
        }

        public Memory<byte> GetMemory(int sizeHint = 0) =>
            GetBuffer(sizeHint);

        public Span<byte> GetSpan(int sizeHint = 0) =>
            GetBuffer(sizeHint);

        public void Dispose()
        {
            var buffer = Interlocked.Exchange(ref _buffer, null);
            if (buffer is not null)
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        private byte[] GetBuffer(int sizeHint)
        {
            var buffer = _buffer ?? throw new ObjectDisposedException(nameof(IncrementalHashBufferWriter));
            var requiredSize = Math.Max(1, sizeHint);
            if (requiredSize <= buffer.Length)
            {
                return buffer;
            }

            var replacement = ArrayPool<byte>.Shared.Rent(requiredSize);
            _buffer = replacement;
            ArrayPool<byte>.Shared.Return(buffer);
            return replacement;
        }
    }

    /// <summary>
    /// Incremental SHA-256 with fixed working storage. Browser WebAssembly falls back to
    /// SHAManagedHashProvider, whose IncrementalHash implementation retains every appended
    /// byte in a MemoryStream. Canonical inputs can be hundreds of megabytes, so that
    /// implementation turns a streaming identity check into a second full payload copy.
    /// </summary>
    private sealed class StreamingSha256
    {
        private static ReadOnlySpan<uint> RoundConstants =>
        [
            0x428a2f98u, 0x71374491u, 0xb5c0fbcfu, 0xe9b5dba5u,
            0x3956c25bu, 0x59f111f1u, 0x923f82a4u, 0xab1c5ed5u,
            0xd807aa98u, 0x12835b01u, 0x243185beu, 0x550c7dc3u,
            0x72be5d74u, 0x80deb1feu, 0x9bdc06a7u, 0xc19bf174u,
            0xe49b69c1u, 0xefbe4786u, 0x0fc19dc6u, 0x240ca1ccu,
            0x2de92c6fu, 0x4a7484aau, 0x5cb0a9dcu, 0x76f988dau,
            0x983e5152u, 0xa831c66du, 0xb00327c8u, 0xbf597fc7u,
            0xc6e00bf3u, 0xd5a79147u, 0x06ca6351u, 0x14292967u,
            0x27b70a85u, 0x2e1b2138u, 0x4d2c6dfcu, 0x53380d13u,
            0x650a7354u, 0x766a0abbu, 0x81c2c92eu, 0x92722c85u,
            0xa2bfe8a1u, 0xa81a664bu, 0xc24b8b70u, 0xc76c51a3u,
            0xd192e819u, 0xd6990624u, 0xf40e3585u, 0x106aa070u,
            0x19a4c116u, 0x1e376c08u, 0x2748774cu, 0x34b0bcb5u,
            0x391c0cb3u, 0x4ed8aa4au, 0x5b9cca4fu, 0x682e6ff3u,
            0x748f82eeu, 0x78a5636fu, 0x84c87814u, 0x8cc70208u,
            0x90befffau, 0xa4506cebu, 0xbef9a3f7u, 0xc67178f2u
        ];

        private readonly byte[] _partialBlock = new byte[64];
        private int _partialLength;
        private ulong _totalBytes;
        private uint _h0 = 0x6a09e667u;
        private uint _h1 = 0xbb67ae85u;
        private uint _h2 = 0x3c6ef372u;
        private uint _h3 = 0xa54ff53au;
        private uint _h4 = 0x510e527fu;
        private uint _h5 = 0x9b05688cu;
        private uint _h6 = 0x1f83d9abu;
        private uint _h7 = 0x5be0cd19u;
        private bool _finalized;

        public void Append(ReadOnlySpan<byte> data)
        {
            if (_finalized)
            {
                throw new InvalidOperationException("The SHA-256 hash has already been finalized.");
            }

            _totalBytes = checked(_totalBytes + (ulong)data.Length);
            if (_partialLength > 0)
            {
                var copied = Math.Min(64 - _partialLength, data.Length);
                data[..copied].CopyTo(_partialBlock.AsSpan(_partialLength));
                _partialLength += copied;
                data = data[copied..];
                if (_partialLength == 64)
                {
                    ProcessBlock(_partialBlock);
                    _partialLength = 0;
                }
            }

            while (data.Length >= 64)
            {
                ProcessBlock(data[..64]);
                data = data[64..];
            }

            if (!data.IsEmpty)
            {
                data.CopyTo(_partialBlock);
                _partialLength = data.Length;
            }
        }

        public byte[] GetHash()
        {
            if (_finalized)
            {
                throw new InvalidOperationException("The SHA-256 hash has already been finalized.");
            }
            _finalized = true;

            Span<byte> finalBlocks = stackalloc byte[128];
            finalBlocks.Clear();
            _partialBlock.AsSpan(0, _partialLength).CopyTo(finalBlocks);
            finalBlocks[_partialLength] = 0x80;
            var finalLength = _partialLength < 56 ? 64 : 128;
            BinaryPrimitives.WriteUInt64BigEndian(
                finalBlocks.Slice(finalLength - 8, 8),
                checked(_totalBytes * 8));
            ProcessBlock(finalBlocks[..64]);
            if (finalLength == 128)
            {
                ProcessBlock(finalBlocks[64..]);
            }

            var result = new byte[32];
            BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(0, 4), _h0);
            BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(4, 4), _h1);
            BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(8, 4), _h2);
            BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(12, 4), _h3);
            BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(16, 4), _h4);
            BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(20, 4), _h5);
            BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(24, 4), _h6);
            BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(28, 4), _h7);
            return result;
        }

        private void ProcessBlock(ReadOnlySpan<byte> block)
        {
            Span<uint> schedule = stackalloc uint[64];
            for (var index = 0; index < 16; index++)
            {
                schedule[index] = BinaryPrimitives.ReadUInt32BigEndian(block.Slice(index * 4, 4));
            }
            for (var index = 16; index < 64; index++)
            {
                var previous15 = schedule[index - 15];
                var previous2 = schedule[index - 2];
                var sigma0 = BitOperations.RotateRight(previous15, 7) ^
                    BitOperations.RotateRight(previous15, 18) ^
                    (previous15 >> 3);
                var sigma1 = BitOperations.RotateRight(previous2, 17) ^
                    BitOperations.RotateRight(previous2, 19) ^
                    (previous2 >> 10);
                schedule[index] = unchecked(schedule[index - 16] + sigma0 +
                    schedule[index - 7] + sigma1);
            }

            var a = _h0;
            var b = _h1;
            var c = _h2;
            var d = _h3;
            var e = _h4;
            var f = _h5;
            var g = _h6;
            var h = _h7;
            var constants = RoundConstants;
            for (var index = 0; index < 64; index++)
            {
                var sum1 = BitOperations.RotateRight(e, 6) ^
                    BitOperations.RotateRight(e, 11) ^
                    BitOperations.RotateRight(e, 25);
                var choose = (e & f) ^ (~e & g);
                var temp1 = unchecked(h + sum1 + choose + constants[index] + schedule[index]);
                var sum0 = BitOperations.RotateRight(a, 2) ^
                    BitOperations.RotateRight(a, 13) ^
                    BitOperations.RotateRight(a, 22);
                var majority = (a & b) ^ (a & c) ^ (b & c);
                var temp2 = unchecked(sum0 + majority);

                h = g;
                g = f;
                f = e;
                e = unchecked(d + temp1);
                d = c;
                c = b;
                b = a;
                a = unchecked(temp1 + temp2);
            }

            _h0 = unchecked(_h0 + a);
            _h1 = unchecked(_h1 + b);
            _h2 = unchecked(_h2 + c);
            _h3 = unchecked(_h3 + d);
            _h4 = unchecked(_h4 + e);
            _h5 = unchecked(_h5 + f);
            _h6 = unchecked(_h6 + g);
            _h7 = unchecked(_h7 + h);
        }
    }
}
