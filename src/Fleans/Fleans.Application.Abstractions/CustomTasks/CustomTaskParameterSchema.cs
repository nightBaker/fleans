namespace Fleans.Application.CustomTasks;

/// <summary>
/// What kind of value a custom-task parameter accepts. Drives both the input-mapping
/// validation (in v2) and the management-UI editor (sub-issue C).
/// </summary>
public enum CustomTaskParameterType
{
    /// <summary>Single-line text. Editor renders &lt;FluentTextField&gt;.</summary>
    String,
    /// <summary>Whole number. Editor renders &lt;FluentTextField type="number"&gt;.</summary>
    Integer,
    /// <summary>true / false. Editor renders &lt;FluentCheckbox&gt;.</summary>
    Boolean,
    /// <summary>
    /// An <c>=</c>-prefixed mapping expression evaluated against the workflow scope at
    /// dispatch time. Editor renders a multi-line input with the existing
    /// <c>=</c>-prefix affordance from the sequence-flow condition editor.
    /// </summary>
    Expression,
    /// <summary>Multi-line text (e.g. JSON request body). Editor renders &lt;FluentTextArea&gt;.</summary>
    MultilineString,
    /// <summary>
    /// Multiple values of the same primitive type. Editor renders a single-column
    /// list with "+ Add" / "remove" buttons; each row is rendered per
    /// <see cref="CustomTaskParameterSpec.ItemType"/>. Use for things like
    /// "list of allowed HTTP status codes".
    /// </summary>
    List,
    /// <summary>
    /// Multiple <c>(string key, value)</c> entries. Editor renders a two-column
    /// table with "+ Add" / "remove" buttons; the value column is rendered per
    /// <see cref="CustomTaskParameterSpec.ItemType"/>. Use for things like
    /// "HTTP request headers" or "form fields".
    /// </summary>
    Map,
}

/// <summary>
/// One parameter the plugin's <c>ExecuteAsync</c> reads from <c>resolvedInputs</c>.
/// The <see cref="Name"/> is the same string a workflow author writes as the
/// <c>&lt;zeebe:input target="…"/&gt;</c> on the BPMN service task.
///
/// When <see cref="Type"/> is <see cref="CustomTaskParameterType.List"/> or
/// <see cref="CustomTaskParameterType.Map"/>, <see cref="ItemType"/> must be set
/// to a primitive type (<c>String</c>, <c>Integer</c>, <c>Boolean</c>,
/// <c>Expression</c>, or <c>MultilineString</c>) describing how each entry is
/// rendered. Nested <c>List</c>/<c>Map</c> are not supported in v1.
/// </summary>
[GenerateSerializer]
public sealed record CustomTaskParameterSpec(
    [property: Id(0)] string Name,
    [property: Id(1)] string? DisplayName,
    [property: Id(2)] CustomTaskParameterType Type,
    [property: Id(3)] bool Required,
    [property: Id(4)] string? Description,
    [property: Id(5)] string? DefaultValue,
    [property: Id(6)] CustomTaskParameterType? ItemType = null);

/// <summary>
/// The set of parameters a plugin exposes. Surfaced in the catalog so the management UI
/// can render a per-parameter editor when a workflow author drops a <c>&lt;serviceTask
/// type="…"/&gt;</c> referencing this plugin.
/// </summary>
[GenerateSerializer]
public sealed record CustomTaskParameterSchema(
    [property: Id(0)] IReadOnlyList<CustomTaskParameterSpec> Parameters)
{
    /// <summary>Convenience constant for plugins that take no parameters.</summary>
    public static readonly CustomTaskParameterSchema Empty = new(Array.Empty<CustomTaskParameterSpec>());
}
