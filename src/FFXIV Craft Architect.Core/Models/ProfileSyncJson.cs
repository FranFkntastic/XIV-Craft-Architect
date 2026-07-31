using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace FFXIV_Craft_Architect.Core.Models;

public static class ProfileSyncJson
{
    public static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true
        };
        options.Converters.Add(new ProfileSyncDateTimeConverter());
        return options;
    }
}

internal sealed class ProfileSyncDateTimeConverter : JsonConverter<DateTime>
{
    private static readonly Regex LegacyMicrosoftDate = new(
        "^/Date\\((-?\\d+)(?:[+-]\\d{4})?\\)/$",
        RegexOptions.CultureInvariant);

    public override DateTime Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("A profile timestamp must be a JSON string.");
        }

        var value = reader.GetString();
        if (DateTime.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var parsed))
        {
            return parsed;
        }

        var legacyMatch = LegacyMicrosoftDate.Match(value ?? string.Empty);
        if (legacyMatch.Success &&
            long.TryParse(
                legacyMatch.Groups[1].Value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var unixMilliseconds))
        {
            try
            {
                return DateTimeOffset
                    .FromUnixTimeMilliseconds(unixMilliseconds)
                    .UtcDateTime;
            }
            catch (ArgumentOutOfRangeException exception)
            {
                throw new JsonException(
                    "The legacy profile timestamp is outside the supported range.",
                    exception);
            }
        }

        throw new JsonException("The profile timestamp is not recognized.");
    }

    public override void Write(
        Utf8JsonWriter writer,
        DateTime value,
        JsonSerializerOptions options) =>
        writer.WriteStringValue(value);
}
