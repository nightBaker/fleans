namespace Fleans.Application.CustomTasks;

/// <summary>
/// One connected version of a custom task. <see cref="Version"/> is <c>null</c> for workers that
/// declare none; those alone can disagree on their parameters (<see cref="SchemaConflict"/>), in which
/// case there is no contract to check and <see cref="ParameterSchema"/> is just the first worker's.
/// </summary>
[GenerateSerializer]
public sealed record CustomTaskVersionEntry(
    [property: Id(0)] string? Version,
    [property: Id(1)] CustomTaskParameterSchema? ParameterSchema,
    [property: Id(2)] IReadOnlyList<string> SiloNames,
    [property: Id(3)] bool SchemaConflict = false);
