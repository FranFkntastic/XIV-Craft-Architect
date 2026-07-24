using System.IO.Compression;
using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services;

public static class MarketIntelligencePayloadCodec
{
    private const string BrotliBase64Prefix = "br64:";

    public static bool IsCompressed(string? payload) =>
        payload?.StartsWith(BrotliBase64Prefix, StringComparison.Ordinal) == true;

    public static string Serialize(
        StoredMarketIntelligence intelligence,
        bool compress)
    {
        ArgumentNullException.ThrowIfNull(intelligence);
        if (!compress)
        {
            return JsonSerializer.Serialize(intelligence);
        }

        using var compressed = new MemoryStream();
        using (var brotli = new BrotliStream(
                   compressed,
                   CompressionLevel.Fastest,
                   leaveOpen: true))
        {
            JsonSerializer.Serialize(brotli, intelligence);
        }

        return BrotliBase64Prefix +
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
            return JsonSerializer.Deserialize<StoredMarketIntelligence>(payload);
        }

        var compressedBytes = Convert.FromBase64String(
            payload[BrotliBase64Prefix.Length..]);
        using var compressed = new MemoryStream(compressedBytes, writable: false);
        using var brotli = new BrotliStream(
            compressed,
            CompressionMode.Decompress,
            leaveOpen: false);
        return JsonSerializer.Deserialize<StoredMarketIntelligence>(brotli);
    }
}
