using Orleans;

namespace Fleans.Domain.States;

[GenerateSerializer]
public class EnvironmentVariablesState
{
    [Id(0)] public string Key { get; set; } = string.Empty;
    [Id(1)] public string? ETag { get; set; }
    [Id(2)] public List<EnvironmentVariableEntry> Variables { get; set; } = new();
}

[GenerateSerializer]
public class EnvironmentVariableEntry
{
    [Id(0)] public Guid Id { get; set; } = Guid.NewGuid();
    [Id(1)] public string Name { get; set; } = string.Empty;
    [Id(2)] public string Value { get; set; } = string.Empty;
    [Id(3)] public string ValueType { get; set; } = "string";
    [Id(4)] public bool IsSecret { get; set; }
    [Id(5)] public List<string>? ProcessKeys { get; set; }

    private static readonly HashSet<string> ValidTypes = new() { "string", "int", "float", "bool" };

    public object GetTypedValue() => ValueType switch
    {
        "int" => int.Parse(Value),
        "float" => double.Parse(Value, System.Globalization.CultureInfo.InvariantCulture),
        "bool" => bool.Parse(Value),
        _ => Value
    };

    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
            return "Name is required.";

        if (!ValidTypes.Contains(ValueType))
            return $"Invalid type '{ValueType}'. Must be one of: string, int, float, bool.";

        return ValueType switch
        {
            "int" when !int.TryParse(Value, out _) => $"Value '{Value}' is not a valid integer.",
            "float" when !double.TryParse(Value, System.Globalization.CultureInfo.InvariantCulture, out _)
                => $"Value '{Value}' is not a valid number.",
            "bool" when !bool.TryParse(Value, out _) => $"Value '{Value}' is not a valid boolean (true/false).",
            _ => null
        };
    }
}
