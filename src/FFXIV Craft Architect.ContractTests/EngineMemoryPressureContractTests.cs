using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FFXIV_Craft_Architect.Core.Engine;

namespace FFXIV_Craft_Architect.ContractTests;

[CollectionDefinition(CollectionName, DisableParallelization = true)]
public sealed class EngineMemoryPressureCollection
{
    public const string CollectionName = "Engine memory pressure";
}

[Collection(EngineMemoryPressureCollection.CollectionName)]
public sealed class EngineMemoryPressureContractTests
{
    [Fact]
    public async Task CanonicalHash_StreamsTheSameKnownCanonicalPayload()
    {
        using var document = JsonDocument.Parse("""
            {"z":"\u0061","a":1.2300}
            """);
        var expected = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes("{\"a\":1.23,\"z\":\"a\"}")))
            .ToLowerInvariant();

        var synchronous = EngineCanonicalHash.ComputeEngineInput(document.RootElement);
        var cooperative = await EngineCanonicalHash.ComputeEngineInputAsync(
            document.RootElement,
            _ => ValueTask.CompletedTask);

        Assert.Equal(expected, synchronous);
        Assert.Equal(expected, cooperative);
        Assert.Equal(expected, EngineCanonicalHash.Compute(document.RootElement));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(54)]
    [InlineData(55)]
    [InlineData(56)]
    [InlineData(62)]
    [InlineData(63)]
    [InlineData(64)]
    [InlineData(65)]
    [InlineData(4096)]
    public void CanonicalHash_MatchesSha256AcrossBlockAndPaddingBoundaries(int valueLength)
    {
        var value = new string('a', valueLength);
        var canonical = JsonSerializer.Serialize(value);
        using var document = JsonDocument.Parse(canonical);
        var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();

        Assert.Equal(expected, EngineCanonicalHash.ComputeEngineInput(document.RootElement));
    }

    [Fact]
    public async Task CanonicalHash_LargeStructuredPayloadKeepsPeakLiveMemoryBounded()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            for (var index = 0; index < 250_000; index++)
            {
                writer.WriteStartObject();
                writer.WriteNumber("itemId", index);
                writer.WriteString("world", $"World {index % 32}");
                writer.WriteNumber("price", index * 17L);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        using var document = JsonDocument.Parse(stream.ToArray());

        using var warmup = JsonDocument.Parse("[]");
        _ = EngineCanonicalHash.ComputeEngineInput(warmup.RootElement);
        var baseline = GC.GetTotalMemory(forceFullCollection: true);
        var peak = baseline;
        var sampling = true;
        using var samplerStarted = new ManualResetEventSlim();
        var sampler = Task.Factory.StartNew(
            () =>
            {
                samplerStarted.Set();
                while (Volatile.Read(ref sampling))
                {
                    var current = GC.GetTotalMemory(forceFullCollection: false);
                    if (current > peak)
                    {
                        peak = current;
                    }
                    Thread.Yield();
                }
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        samplerStarted.Wait();
        var hash = EngineCanonicalHash.ComputeEngineInput(document.RootElement);
        Volatile.Write(ref sampling, false);
        await sampler;
        var peakIncrease = peak - baseline;
        var cooperativeYields = 0;
        var cooperativeHash = await EngineCanonicalHash.ComputeEngineInputAsync(
            document.RootElement,
            _ =>
            {
                cooperativeYields++;
                return ValueTask.CompletedTask;
            });

        Assert.Equal(64, hash.Length);
        Assert.Equal(hash, cooperativeHash);
        Assert.True(cooperativeYields > 0);
        Assert.True(
            peakIncrease < 128 * 1024 * 1024,
            $"Hashing retained {peakIncrease:N0} peak live bytes for a {stream.Length:N0}-byte canonical payload.");
    }

}
