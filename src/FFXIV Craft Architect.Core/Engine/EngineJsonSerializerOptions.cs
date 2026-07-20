using System.Text.Json;
using System.Text.Json.Serialization;

namespace FFXIV_Craft_Architect.Core.Engine;

public static class EngineJsonSerializerOptions
{
    public static JsonSerializerOptions CreateWire()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new ReadOnlySetJsonConverterFactory());
        return options;
    }

    private sealed class ReadOnlySetJsonConverterFactory : JsonConverterFactory
    {
        public override bool CanConvert(Type typeToConvert) =>
            typeToConvert.IsGenericType &&
            typeToConvert.GetGenericTypeDefinition() == typeof(IReadOnlySet<>);

        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
        {
            var elementType = typeToConvert.GetGenericArguments()[0];
            return (JsonConverter)Activator.CreateInstance(
                typeof(ReadOnlySetJsonConverter<>).MakeGenericType(elementType))!;
        }
    }

    private sealed class ReadOnlySetJsonConverter<T> : JsonConverter<IReadOnlySet<T>>
    {
        public override IReadOnlySet<T>? Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null)
            {
                return null;
            }

            var values = JsonSerializer.Deserialize<List<T>>(ref reader, options);
            var result = new HashSet<T>();
            foreach (var value in values ?? [])
            {
                if (!result.Add(value))
                {
                    throw new JsonException("Set-valued engine input cannot contain duplicate elements.");
                }
            }
            return result;
        }

        public override void Write(
            Utf8JsonWriter writer,
            IReadOnlySet<T> value,
            JsonSerializerOptions options)
        {
            var ordered = value
                .Select(item => new
                {
                    Value = item,
                    Hash = EngineCanonicalHash.Compute(item, options),
                    Json = JsonSerializer.Serialize(item, options)
                })
                .OrderBy(item => item.Hash, StringComparer.Ordinal)
                .ThenBy(item => item.Json, StringComparer.Ordinal)
                .ToArray();
            writer.WriteStartArray();
            foreach (var item in ordered)
            {
                JsonSerializer.Serialize(writer, item.Value, options);
            }
            writer.WriteEndArray();
        }
    }
}
