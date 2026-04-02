using Newtonsoft.Json;
using System.Dynamic;

namespace Fleans.Api.Controllers;

internal static class VariableConverter
{
    /// <summary>
    /// Converts request variables to an Orleans-safe ExpandoObject.
    /// System.Text.Json deserializes object values as JsonElement, which Orleans
    /// cannot serialize. Re-parsing via Newtonsoft yields proper .NET primitives.
    /// </summary>
    internal static ExpandoObject ToExpandoObject(object? variables)
    {
        if (variables is null)
            return new ExpandoObject();

        var json = System.Text.Json.JsonSerializer.Serialize(variables);
        return JsonConvert.DeserializeObject<ExpandoObject>(json)
            ?? new ExpandoObject();
    }
}
