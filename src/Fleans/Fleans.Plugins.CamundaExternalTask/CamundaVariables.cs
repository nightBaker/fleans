using System.Collections;
using System.Dynamic;
using System.Globalization;
using System.Text.Json;

namespace Fleans.Plugins.CamundaExternalTask;

/// <summary>
/// Converts between Camunda typed values (<c>{value, type, valueInfo}</c>) and Fleans workflow
/// variables (<see cref="ExpandoObject"/> trees of string / long / double / bool / null /
/// <see cref="ExpandoObject"/> / <see cref="List{T}"/>).
/// </summary>
public static class CamundaVariables
{
    private const string JsonDataFormat = "application/json";

    /// <summary>Thrown for a typed value Fleans cannot store (e.g. Bytes, File, Java-serialized Object).</summary>
    public sealed class UnsupportedVariableException(string message) : Exception(message);

    public static Dictionary<string, object?> ToFleans(Dictionary<string, VariableValueDto>? variables)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (variables is null) return result;
        foreach (var (name, typed) in variables)
            result[name] = ToFleans(name, typed);
        return result;
    }

    public static object? ToFleans(string name, VariableValueDto? typed)
    {
        if (typed is null) return null;
        var value = typed.Value is JsonElement element ? element : JsonSerializer.SerializeToElement(typed.Value);
        var type = typed.Type?.ToLowerInvariant();

        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined || type == "null")
            return null;

        switch (type)
        {
            case null:
                return FromJson(value); // untyped: infer from the JSON token, like Camunda
            case "string":
            case "date":
                return value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
            case "boolean":
                return value.ValueKind == JsonValueKind.String ? bool.Parse(value.GetString()!) : value.GetBoolean();
            case "integer":
            case "short":
            case "long":
                return value.ValueKind == JsonValueKind.String
                    ? long.Parse(value.GetString()!, CultureInfo.InvariantCulture)
                    : value.GetInt64();
            case "double":
                return value.ValueKind == JsonValueKind.String
                    ? double.Parse(value.GetString()!, CultureInfo.InvariantCulture)
                    : value.GetDouble();
            case "json":
                return ParseSerializedJson(value);
            case "object" when IsJsonDataFormat(typed.ValueInfo):
                return ParseSerializedJson(value);
            default:
                throw new UnsupportedVariableException(
                    $"Variable '{name}' has type '{typed.Type}', which Fleans cannot store. " +
                    "Supported: String, Boolean, Integer, Short, Long, Double, Date, Null, Json, " +
                    "and Object with serializationDataFormat 'application/json'.");
        }
    }

    public static Dictionary<string, VariableValueDto> ToCamundaVariables(IEnumerable<KeyValuePair<string, object?>> variables)
    {
        var result = new Dictionary<string, VariableValueDto>(StringComparer.Ordinal);
        foreach (var (name, value) in variables)
            result[name] = ToCamunda(value);
        return result;
    }

    public static VariableValueDto ToCamunda(object? value) => value switch
    {
        null => Typed(null, "Null"),
        string s => Typed(s, "String"),
        bool b => Typed(b, "Boolean"),
        int or short or byte or sbyte or ushort => Typed(Convert.ToInt32(value, CultureInfo.InvariantCulture), "Integer"),
        long l when l is >= int.MinValue and <= int.MaxValue => Typed((int)l, "Integer"),
        long or uint or ulong => Typed(Convert.ToInt64(value, CultureInfo.InvariantCulture), "Long"),
        double or float or decimal => Typed(Convert.ToDouble(value, CultureInfo.InvariantCulture), "Double"),
        DateTime dt => Typed(FormatDate(dt), "Date"),
        DateTimeOffset dto => Typed(FormatDate(dto.UtcDateTime), "Date"),
        Guid g => Typed(g.ToString("D"), "String"),
        _ => Typed(JsonSerializer.Serialize(ToSerializable(value)), "Json"),
    };

    /// <summary>Camunda's REST date format, <c>yyyy-MM-dd'T'HH:mm:ss.SSSZ</c>, rendered in UTC.</summary>
    public static string FormatDate(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToUniversalTime()
            .ToString("yyyy-MM-dd'T'HH:mm:ss.fff", CultureInfo.InvariantCulture) + "+0000";

    private static VariableValueDto Typed(object? value, string type) =>
        new(value, type, new Dictionary<string, object?>());

    private static bool IsJsonDataFormat(Dictionary<string, object?>? valueInfo)
    {
        if (valueInfo is null || !valueInfo.TryGetValue("serializationDataFormat", out var format))
            return false;
        var text = format is JsonElement { ValueKind: JsonValueKind.String } e ? e.GetString() : format?.ToString();
        return string.Equals(text, JsonDataFormat, StringComparison.OrdinalIgnoreCase);
    }

    private static object? ParseSerializedJson(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
            return FromJson(value); // already structured JSON
        using var doc = JsonDocument.Parse(value.GetString()!);
        return FromJson(doc.RootElement);
    }

    /// <summary>JSON → Fleans variable tree (objects become <see cref="ExpandoObject"/>).</summary>
    public static object? FromJson(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var obj = new ExpandoObject();
                var dict = (IDictionary<string, object?>)obj;
                foreach (var prop in element.EnumerateObject())
                    dict[prop.Name] = FromJson(prop.Value);
                return obj;
            case JsonValueKind.Array:
                return element.EnumerateArray().Select(FromJson).ToList();
            case JsonValueKind.String:
                return element.GetString();
            case JsonValueKind.Number:
                return element.TryGetInt64(out var l) ? (object)l : element.GetDouble();
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            default:
                return null;
        }
    }

    /// <summary>Normalises engine-side objects (<see cref="ExpandoObject"/>, lists) for System.Text.Json.</summary>
    private static object? ToSerializable(object? value) => value switch
    {
        null or string => value,
        IDictionary<string, object?> map => map.ToDictionary(kv => kv.Key, kv => ToSerializable(kv.Value)),
        IEnumerable list => list.Cast<object?>().Select(ToSerializable).ToList(),
        _ => value,
    };
}
