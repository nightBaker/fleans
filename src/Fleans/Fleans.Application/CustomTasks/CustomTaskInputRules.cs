using System.Globalization;
using Fleans.Domain.Activities;
using Newtonsoft.Json.Linq;

namespace Fleans.Application.CustomTasks;

/// <summary>
/// Whether a task's inputs fit the schema of the plugin version it uses (#1051). Deploy checks the
/// BPMN sources, where an <c>=expression</c> has no type yet; dispatch checks the evaluated values.
/// Both report undeclared and missing inputs by the same rules. A numeric or boolean string is valid
/// because a literal BPMN source always evaluates to a string.
/// </summary>
public static class CustomTaskInputRules
{
    /// <summary>The violations of <paramref name="inputs"/> as BPMN sources, for deploy.</summary>
    public static List<string> CheckSources(CustomTaskParameterSchema schema, IReadOnlyCollection<InputMapping> inputs)
    {
        var violations = new List<string>();
        var present = new HashSet<string>(StringComparer.Ordinal);
        foreach (var input in inputs)
        {
            var spec = FindDeclared(schema, input.Target, violations);
            if (spec is null)
                continue;
            if (input.Source.StartsWith('='))
            {
                present.Add(input.Target);
                continue;
            }
            if (!IsBlankNumberOrBoolean(spec.Type, input.Source))
                present.Add(input.Target);
            if (LiteralProblem(spec, input.Source) is { } problem)
                violations.Add($"input '{input.Target}' {problem}");
        }
        AddMissing(schema, present, violations);
        return violations;
    }

    /// <summary>The violations of evaluated <paramref name="inputs"/>, for dispatch.</summary>
    public static List<string> CheckValues(CustomTaskParameterSchema schema, JObject inputs)
    {
        var violations = new List<string>();
        var present = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (name, value) in inputs)
        {
            var spec = FindDeclared(schema, name, violations);
            if (spec is null || value is null || value.Type == JTokenType.Null)
                continue;
            if (!(value is JValue { Value: string text } && IsBlankNumberOrBoolean(spec.Type, text)))
                present.Add(name);
            if (ValueProblem(spec, value) is { } problem)
                violations.Add($"input '{name}' {problem}");
        }
        AddMissing(schema, present, violations);
        return violations;
    }

    private static CustomTaskParameterSpec? FindDeclared(CustomTaskParameterSchema schema, string name, List<string> violations)
    {
        var spec = schema.Parameters.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal));
        if (spec is null)
            violations.Add($"input '{name}' is not a parameter of this plugin");
        return spec;
    }

    private static void AddMissing(CustomTaskParameterSchema schema, HashSet<string> present, List<string> violations)
    {
        foreach (var spec in schema.Parameters.Where(p => p.Required && !present.Contains(p.Name)))
            violations.Add($"required parameter '{spec.Name}' is missing");
    }

    private static string? LiteralProblem(CustomTaskParameterSpec spec, string source) => spec.Type switch
    {
        CustomTaskParameterType.Integer when !IsWholeNumberText(source) && !string.IsNullOrWhiteSpace(source) =>
            $"must be a whole number; got '{source}'",
        CustomTaskParameterType.Boolean when !IsBooleanText(source) && !string.IsNullOrWhiteSpace(source) =>
            $"must be true or false; got '{source}'",
        CustomTaskParameterType.List or CustomTaskParameterType.Map =>
            $"is a {spec.Type.ToString().ToLowerInvariant()}: a literal is not supported, reference a variable with =",
        _ => null,
    };

    private static string? ValueProblem(CustomTaskParameterSpec spec, JToken value)
    {
        switch (spec.Type)
        {
            case CustomTaskParameterType.List:
                if (value is not JArray list)
                    return $"must be a list; got {Describe(value)}";
                return list.Where(item => item.Type != JTokenType.Null)
                    .Select(item => ScalarProblem(spec.ItemType, item))
                    .FirstOrDefault(problem => problem is not null) is { } itemProblem
                    ? $"has an entry that {itemProblem}"
                    : null;
            case CustomTaskParameterType.Map:
                if (value is not JObject map)
                    return $"must be a map; got {Describe(value)}";
                return map.Properties().Where(entry => entry.Value.Type != JTokenType.Null)
                    .Select(entry => ScalarProblem(spec.ItemType, entry.Value))
                    .FirstOrDefault(problem => problem is not null) is { } entryProblem
                    ? $"has an entry that {entryProblem}"
                    : null;
            default:
                return ScalarProblem(spec.Type, value);
        }
    }

    private static string? ScalarProblem(CustomTaskParameterType? type, JToken value) => type switch
    {
        CustomTaskParameterType.Integer when !IsWholeNumber(value) => $"must be a whole number; got {Describe(value)}",
        CustomTaskParameterType.Boolean when !IsBoolean(value) => $"must be true or false; got {Describe(value)}",
        CustomTaskParameterType.String or CustomTaskParameterType.MultilineString when value is JContainer =>
            $"must be text; got {Describe(value)}",
        _ => null,
    };

    private static bool IsWholeNumber(JToken value) => value switch
    {
        { Type: JTokenType.Integer } => true,
        JValue { Type: JTokenType.Float } number => decimal.TryParse(
            Convert.ToString(number.Value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            && d == decimal.Truncate(d),
        JValue { Value: string text } => string.IsNullOrWhiteSpace(text) || IsWholeNumberText(text),
        _ => false,
    };

    private static bool IsBoolean(JToken value) => value switch
    {
        { Type: JTokenType.Boolean } => true,
        JValue { Value: string text } => string.IsNullOrWhiteSpace(text) || IsBooleanText(text),
        _ => false,
    };

    // A blank editor field means absent for these: a plugin can't read a number or a flag out of it.
    private static bool IsBlankNumberOrBoolean(CustomTaskParameterType type, string text) =>
        type is CustomTaskParameterType.Integer or CustomTaskParameterType.Boolean && string.IsNullOrWhiteSpace(text);

    private static bool IsWholeNumberText(string text) =>
        long.TryParse(text.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _);

    private static bool IsBooleanText(string text) => bool.TryParse(text.Trim(), out _);

    // The type only: this ends up in the activity's error, and a value may be a secret.
    private static string Describe(JToken value) => value.Type switch
    {
        JTokenType.Object => "an object",
        JTokenType.Array => "a list",
        JTokenType.Integer or JTokenType.Float => "a number",
        JTokenType.Boolean => "a boolean",
        _ => "text",
    };
}
