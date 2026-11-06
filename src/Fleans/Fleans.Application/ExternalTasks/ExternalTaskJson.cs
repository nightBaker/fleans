using System.Text.Json;
using Newtonsoft.Json;

namespace Fleans.Application.ExternalTasks;

/// <summary>
/// Serializes a task's inputs and variables to the JSON a worker receives. Values may be
/// <see cref="JsonElement"/>s — the Redis stream provider deserializes event payloads with
/// System.Text.Json — which Newtonsoft would otherwise write as their properties.
/// </summary>
internal static class ExternalTaskJson
{
    private static readonly JsonSerializerSettings Settings = new() { Converters = { new JsonElementConverter() } };

    public static string Serialize(object value) => JsonConvert.SerializeObject(value, Settings);

    private sealed class JsonElementConverter : Newtonsoft.Json.JsonConverter<JsonElement>
    {
        public override void WriteJson(JsonWriter writer, JsonElement value, Newtonsoft.Json.JsonSerializer serializer) =>
            writer.WriteRawValue(value.GetRawText());

        public override JsonElement ReadJson(JsonReader reader, Type objectType, JsonElement existingValue,
            bool hasExistingValue, Newtonsoft.Json.JsonSerializer serializer) =>
            throw new NotSupportedException();

        public override bool CanRead => false;
    }
}
