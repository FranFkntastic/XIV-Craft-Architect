using System.IO.Compression;
using System.Text.Json;
using FFXIV_Craft_Architect.Core.Engine;
using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services;

public static class MarketIntelligencePayloadCodec
{
    private const string GZipBase64Prefix = "gz64:";
    private static readonly JsonSerializerOptions JsonOptions =
        EngineJsonSerializerOptions.CreateWire();

    public static bool IsCompressed(string? payload) =>
        payload?.StartsWith(GZipBase64Prefix, StringComparison.Ordinal) == true;

    public static string Serialize(
        StoredMarketIntelligence intelligence,
        bool compress)
    {
        ArgumentNullException.ThrowIfNull(intelligence);
        if (!compress)
        {
            return JsonSerializer.Serialize(intelligence, JsonOptions);
        }

        using var compressed = new MemoryStream();
        using (var gzip = new GZipStream(
                   compressed,
                   CompressionLevel.Fastest,
                   leaveOpen: true))
        {
            JsonSerializer.Serialize(gzip, intelligence, JsonOptions);
        }

        return GZipBase64Prefix +
               Convert.ToBase64String(
                   compressed.GetBuffer(),
                   0,
                   checked((int)compressed.Length));
    }

    public static StoredMarketIntelligence? Deserialize(string payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);
        if (!IsCompressed(payload))
        {
            return JsonSerializer.Deserialize<StoredMarketIntelligence>(payload, JsonOptions);
        }

        var compressedBytes = Convert.FromBase64String(
            payload[GZipBase64Prefix.Length..]);
        using var compressed = new MemoryStream(compressedBytes, writable: false);
        using var gzip = new GZipStream(
            compressed,
            CompressionMode.Decompress,
            leaveOpen: false);
        return JsonSerializer.Deserialize<StoredMarketIntelligence>(gzip, JsonOptions);
    }
}
