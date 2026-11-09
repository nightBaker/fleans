namespace Fleans.Application.CustomTasks;

/// <summary>
/// What a Worker silo announces about a custom-task plugin it hosts. Sent to the
/// catalog at silo startup by the per-silo <c>CustomTaskPluginRegistrar</c>, and by the engine for
/// each task type a gRPC worker announces. <see cref="AllowedProcessKeys"/> applies only to a private
/// plugin, and only until its allow-list is first edited through the API or the UI.
/// </summary>
[GenerateSerializer]
public sealed record CustomTaskRegistration(
    [property: Id(0)] string TaskType,
    [property: Id(1)] string? DisplayName,
    [property: Id(2)] CustomTaskParameterSchema? ParameterSchema,
    [property: Id(3)] string SiloName,
    [property: Id(4)] string? Category = null,
    [property: Id(5)] CustomTaskVisibility Visibility = CustomTaskVisibility.Public,
    [property: Id(6)] List<string>? AllowedProcessKeys = null,
    [property: Id(7)] string? Version = null);
