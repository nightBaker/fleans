namespace Fleans.Domain;

/// <summary>
/// Script-task formats (#1105). C# (<c>csharp</c>, <c>c#</c> or no format) is built in and runs on
/// the Worker silo's script executor. Every other format runs on an external-task worker plugin
/// registered for the task type <c>fleans.script.&lt;format&gt;</c>, so a runaway or malicious
/// script cannot hurt a silo.
/// </summary>
public static class ScriptFormats
{
    /// <summary>The prefix of the external task type a non-built-in script format runs as.</summary>
    public const string TaskTypePrefix = "fleans.script.";

    /// <summary>The resolved input that carries the script text to the worker.</summary>
    public const string ScriptInput = "script";

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["js"] = "javascript",
        ["py"] = "python",
    };

    /// <summary>Whether the format is C#, which runs built in.</summary>
    public static bool IsBuiltIn(string? scriptFormat) =>
        string.IsNullOrEmpty(scriptFormat)
        || scriptFormat.Equals("csharp", StringComparison.OrdinalIgnoreCase)
        || scriptFormat.Equals("c#", StringComparison.OrdinalIgnoreCase);

    /// <summary>The format in lower case with its alias resolved: <c>JS</c> → <c>javascript</c>.</summary>
    public static string Normalize(string scriptFormat)
    {
        var format = scriptFormat.Trim().ToLowerInvariant();
        return Aliases.TryGetValue(format, out var canonical) ? canonical : format;
    }

    /// <summary>The external task type a non-built-in format runs as: <c>fleans.script.javascript</c>.</summary>
    public static string ToTaskType(string scriptFormat) => TaskTypePrefix + Normalize(scriptFormat);

    /// <summary>Whether a custom-task type is a script-format worker rather than a service-task plugin.</summary>
    public static bool IsScriptTaskType(string taskType) =>
        taskType.StartsWith(TaskTypePrefix, StringComparison.OrdinalIgnoreCase);
}
