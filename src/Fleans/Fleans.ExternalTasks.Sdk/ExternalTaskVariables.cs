namespace Fleans.ExternalTasks.Sdk;

/// <summary>
/// Which workflow variables a handler receives in the <c>variables</c> parameter of
/// <see cref="ExternalTaskHandler.ExecuteAsync"/>, declared when it is registered. The engine does
/// not collect or send the rest, so large variables do not cross the wire for a handler that never
/// reads them. The task's input mappings arrive in <c>resolvedInputs</c> whatever this says.
/// </summary>
public sealed class ExternalTaskVariables
{
    private ExternalTaskVariables(bool includeAll, IReadOnlyList<string> variableNames)
    {
        IncludeAll = includeAll;
        VariableNames = variableNames;
    }

    /// <summary>No variables: <c>variables</c> is empty. The default.</summary>
    public static ExternalTaskVariables None { get; } = new(false, []);

    /// <summary>Every variable visible to the task.</summary>
    public static ExternalTaskVariables All { get; } = new(true, []);

    /// <summary>
    /// The top-level variables with these exact names; a name no visible variable has is left out.
    /// </summary>
    public static ExternalTaskVariables Names(params string[] names)
    {
        ArgumentNullException.ThrowIfNull(names);
        if (names.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("A variable name is blank.", nameof(names));
        return new ExternalTaskVariables(false, [.. names]);
    }

    internal bool IncludeAll { get; }

    internal IReadOnlyList<string> VariableNames { get; }
}
