namespace Fleans.Application.ExternalTasks;

/// <summary>
/// Routes service tasks to external workers (<c>Fleans:ExternalTasks</c>, #979). A task whose type
/// is listed in <see cref="TaskTypes"/> goes to the external-task stream instead of the custom-task
/// plugin stream; <see cref="ExternalTaskDispatcherGrain"/> hands it to a worker connected over gRPC
/// (<c>external_task_worker.proto</c>). Off by default: with no task types every service task keeps
/// going to plugins.
/// </summary>
public sealed class ExternalTaskOptions
{
    public const string SectionName = "Fleans:ExternalTasks";

    /// <summary>
    /// Service-task types handed to external workers (<c>Fleans:ExternalTasks:TaskTypes</c>), matched
    /// case-insensitively. Every other type goes to the plugin stream as before.
    /// </summary>
    public List<string> TaskTypes { get; set; } = [];

    /// <summary>
    /// How often a connected worker's subscription is renewed with its worker pools and the
    /// catalog, and how often the worker's stream gets a heartbeat. Default 15 s.
    /// </summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// A worker not renewed for this long is dropped from its pools and the catalog — the bound
    /// on how long a worker behind a crashed API process keeps being picked. Must exceed
    /// <see cref="HeartbeatInterval"/>. Default 45 s.
    /// </summary>
    public TimeSpan WorkerTimeout { get; set; } = TimeSpan.FromSeconds(45);

    /// <summary>
    /// How long the engine waits for a worker to report a task's outcome before it gives up on
    /// that delivery (tells the worker to cancel it) and lets the stream redeliver the task. The
    /// wait runs inside the stream delivery, so keep it below the stream's delivery window
    /// (<c>WorkflowEventStreams.DefaultMaxEventDeliveryTime</c>, 6 min) or the redelivery is cut
    /// short. Default 5 min.
    /// </summary>
    public TimeSpan ExecutionTimeout { get; set; } = TimeSpan.FromMinutes(5);

    public bool IsExternal(string taskType) =>
        TaskTypes.Exists(t => string.Equals(t, taskType, StringComparison.OrdinalIgnoreCase));
}
