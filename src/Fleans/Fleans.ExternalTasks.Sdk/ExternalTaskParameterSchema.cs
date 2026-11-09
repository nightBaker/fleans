namespace Fleans.ExternalTasks.Sdk;

/// <summary>What kind of value a task parameter accepts; picks its editor in the management UI.</summary>
public enum ExternalTaskParameterType
{
    /// <summary>Single-line text.</summary>
    String,
    /// <summary>Whole number.</summary>
    Integer,
    /// <summary>true / false.</summary>
    Boolean,
    /// <summary>An <c>=</c>-prefixed mapping expression evaluated against the workflow scope at dispatch time.</summary>
    Expression,
    /// <summary>Multi-line text (e.g. a JSON request body).</summary>
    MultilineString,
    /// <summary>Multiple values of the type <see cref="ExternalTaskParameter.ItemType"/> names.</summary>
    List,
    /// <summary>Multiple <c>(string key, value)</c> entries; values have the type <see cref="ExternalTaskParameter.ItemType"/> names.</summary>
    Map,
}

/// <summary>
/// One parameter the handler reads from <c>resolvedInputs</c>. <see cref="Name"/> is what a workflow
/// author writes as <c>&lt;fleans:input target="…"/&gt;</c>. <see cref="ItemType"/> is required for
/// <see cref="ExternalTaskParameterType.List"/> and <see cref="ExternalTaskParameterType.Map"/>, and
/// must not itself be one of those two.
/// </summary>
public sealed record ExternalTaskParameter(
    string Name,
    string? DisplayName,
    ExternalTaskParameterType Type,
    bool Required = false,
    string? Description = null,
    string? DefaultValue = null,
    ExternalTaskParameterType? ItemType = null);

/// <summary>The parameters a task type exposes, which the BPMN editor renders typed editors from.</summary>
public sealed record ExternalTaskParameterSchema(IReadOnlyList<ExternalTaskParameter> Parameters)
{
    /// <summary>For a task type that takes no parameters.</summary>
    public static readonly ExternalTaskParameterSchema Empty = new([]);
}

/// <summary>Who may use a task type.</summary>
public enum ExternalTaskVisibility
{
    /// <summary>Usable in any process.</summary>
    Public,
    /// <summary>Usable only in the processes on the task type's allow-list (by process definition key).</summary>
    Private,
}
