using Newtonsoft.Json;

namespace Fleans.Persistence.Events;

/// <summary>
/// Reads a JSON string or number token as a C# string.
/// Used for backward compatibility when event records changed ErrorCode from int to string.
/// Old stored events have "ErrorCode": 500 (integer); new records have "ErrorCode": "500" (string).
/// </summary>
internal sealed class StringFromNumberConverter : JsonConverter<string?>
{
    public override string? ReadJson(
        JsonReader reader, Type objectType, string? existingValue, bool hasExistingValue,
        JsonSerializer serializer)
    {
        return reader.TokenType switch
        {
            JsonToken.Null => null,
            JsonToken.String => (string?)reader.Value,
            JsonToken.Integer => reader.Value?.ToString(),
            JsonToken.Float => reader.Value?.ToString(),
            _ => throw new JsonSerializationException(
                $"Cannot convert token type {reader.TokenType} to string error code")
        };
    }

    public override void WriteJson(JsonWriter writer, string? value, JsonSerializer serializer)
    {
        if (value is null)
            writer.WriteNull();
        else
            writer.WriteValue(value);
    }
}
