using Newtonsoft.Json;

namespace Fleans.Application.QueryModels;

/// <summary>
/// Flattens a workflow variable value into the string form used by
/// <see cref="VariableStateSnapshot"/> and <see cref="CompensationLogEntrySnapshot"/>.
/// Nested dictionaries / lists are serialized via Newtonsoft.Json (project convention
/// for dynamic workflow variable state); scalars use <see cref="object.ToString"/>.
/// </summary>
public static class VariableValueFormatter
{
    public static string Format(object? value) => value switch
    {
        null => "",
        string s => s,
        IList<object> or IDictionary<string, object> => JsonConvert.SerializeObject(value, Formatting.None),
        _ => value.ToString() ?? ""
    };
}
