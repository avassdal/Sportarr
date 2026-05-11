using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sportarr.Api.Converters;

/// <summary>
/// JSON converter for DateTime? that tolerates the shapes the upstream
/// API actually emits — full ISO timestamps, date-only strings
/// ("2026-05-04"), and null. System.Text.Json's default DateTime?
/// converter is strict about ISO 8601 and rejects "yyyy-MM-dd" without
/// a time component, which the broadcastDate field uses.
/// </summary>
public class NullableEventDateConverter : JsonConverter<DateTime?>
{
    public override DateTime? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            var s = reader.GetString();
            if (string.IsNullOrWhiteSpace(s))
            {
                return null;
            }
            // DateTime.TryParse handles both date-only and full ISO
            // timestamp shapes. The result is a local-kind DateTime,
            // which is fine: BroadcastDate carries no time component
            // and only the .Date portion is read by callers.
            if (DateTime.TryParse(s, out var dt))
            {
                return dt;
            }
        }

        return null;
    }

    public override void Write(Utf8JsonWriter writer, DateTime? value, JsonSerializerOptions options)
    {
        if (value.HasValue)
        {
            // Emit as date-only — BroadcastDate has no meaningful time.
            writer.WriteStringValue(value.Value.ToString("yyyy-MM-dd"));
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}
