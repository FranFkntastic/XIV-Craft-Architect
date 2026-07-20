using System.Text.Json;

namespace FFXIV_Craft_Architect.Core.Engine;

public static class EngineSemanticProjection
{
    private static readonly HashSet<string> ExcludedPropertyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Timings",
        "LoadedAtUtc",
        "LastReconciledAtUtc",
        "EvaluatedAtUtc",
        "FetchedAtUtc",
        "FetchedAtUnix",
        "MarketUploadedAtUtc",
        "LastUploadTimeUnixMilliseconds",
        "ObservedAtUnixMilliseconds",
        "LastReviewTimeUtc",
        "LastReviewTimeUnix",
        "DataAge",
        "DataAgeSource",
        "Age",
        "CacheDecision"
    };

    public static JsonElement Create<T>(T value)
    {
        using var source = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(value));
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteSemantic(writer, source.RootElement);
        }

        using var result = JsonDocument.Parse(stream.ToArray());
        return result.RootElement.Clone();
    }

    public static string ComputeHash<T>(T value) => EngineCanonicalHash.Compute(Create(value));

    private static void WriteSemantic(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject()
                             .Where(property => !ExcludedPropertyNames.Contains(property.Name))
                             .OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteSemantic(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                var projectedItems = element.EnumerateArray()
                    .Select(ProjectItem)
                    .OrderBy(EngineCanonicalHash.Compute, StringComparer.Ordinal)
                    .ToArray();
                writer.WriteStartArray();
                foreach (var item in projectedItems)
                {
                    item.WriteTo(writer);
                }
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static JsonElement ProjectItem(JsonElement item)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteSemantic(writer, item);
        }
        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }
}
