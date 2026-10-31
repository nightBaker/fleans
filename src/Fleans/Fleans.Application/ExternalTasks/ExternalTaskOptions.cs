namespace Fleans.Application.ExternalTasks;

/// <summary>
/// Routes service tasks to external workers through a queue (<c>Fleans:ExternalTasks</c>, #979).
/// A task whose type is listed in <see cref="TaskTypes"/> is published to the queue named by
/// <see cref="Provider"/> instead of the custom-task plugin stream; the worker completes or fails it
/// over REST. Off by default: with no task types every service task keeps going to plugins.
/// </summary>
public sealed class ExternalTaskOptions
{
    public const string SectionName = "Fleans:ExternalTasks";

    /// <summary>
    /// Queue implementation (<c>Fleans:ExternalTasks:Provider</c>): <c>kafka</c>, or unset for none.
    /// Required when <see cref="TaskTypes"/> is not empty.
    /// </summary>
    public string? Provider { get; set; }

    /// <summary>
    /// Service-task types handed to external workers (<c>Fleans:ExternalTasks:TaskTypes</c>), matched
    /// case-insensitively. Every other type goes to the plugin stream as before.
    /// </summary>
    public List<string> TaskTypes { get; set; } = [];

    public bool IsExternal(string taskType) =>
        TaskTypes.Exists(t => string.Equals(t, taskType, StringComparison.OrdinalIgnoreCase));
}
