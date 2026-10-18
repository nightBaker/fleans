using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Fleans.Application.QueryModels;

/// <summary>
/// Flattens a workflow variable value into the string form used by
/// <see cref="VariableStateSnapshot"/> and <see cref="CompensationLogEntrySnapshot"/>.
/// Nested dictionaries / lists — and the JObject / JArray values variables come back as from
/// storage — are serialized compactly via Newtonsoft.Json (project convention for dynamic
/// workflow variable state); scalars use <see cref="object.ToString"/>.
/// </summary>
public static class VariableValueFormatter
{
    public static string Format(object? value) => value switch
    {
        null => "",
        string s => s,
        JContainer container => container.ToString(Formatting.None),
        IList<object> or IDictionary<string, object> => JsonConvert.SerializeObject(value, Formatting.None),
        _ => value.ToString() ?? ""
    };
}
