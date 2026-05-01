namespace Fleans.Worker.CustomTasks;

/// <summary>
/// Per-plugin descriptor registered into DI by <c>AddCustomTaskPlugin&lt;THandler&gt;()</c>
/// and consumed by <c>CustomTaskPluginRegistrar</c> at silo startup to push the registration
/// to the catalog.
/// </summary>
public sealed record CustomTaskPluginDescriptor(
    string TaskType,
    string? DisplayName,
    string? ParameterSchemaJson);
