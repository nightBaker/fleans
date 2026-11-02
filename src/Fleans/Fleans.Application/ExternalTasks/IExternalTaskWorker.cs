namespace Fleans.Application.ExternalTasks;

/// <summary>
/// One connected gRPC worker, as seen from the engine: an Orleans observer that the worker gRPC
/// service creates for each <c>Subscribe</c> stream and joins to the
/// <see cref="IExternalTaskWorkerPoolGrain"/> of every task type the worker implements. The
/// <see cref="ExternalTaskDispatcherGrain"/> hands it tasks; the service writes them to the stream.
/// </summary>
public interface IExternalTaskWorker : IGrainObserver
{
    /// <summary>
    /// Queues <paramref name="assignment"/> on the worker's stream. <c>false</c> when the stream
    /// has already closed — the caller tries another worker.
    /// </summary>
    Task<bool> Assign(ExternalTaskAssignment assignment);

    /// <summary>Tells the worker the engine no longer waits for the task with <paramref name="taskToken"/>.</summary>
    Task Cancel(string taskToken);
}

/// <summary>
/// One delivery attempt of a external task to a worker. Inputs and variables are already the JSON
/// the worker receives: the values can be anything a stream provider deserialized (e.g.
/// <c>System.Text.Json.JsonElement</c>), which Orleans cannot copy to a local observer.
/// </summary>
[GenerateSerializer]
public sealed record ExternalTaskAssignment(
    [property: Id(0)] string TaskToken,
    [property: Id(1)] string TaskType,
    [property: Id(2)] Guid WorkflowInstanceId,
    [property: Id(3)] string WorkflowId,
    [property: Id(4)] string? ProcessDefinitionId,
    [property: Id(5)] Guid ActivityInstanceId,
    [property: Id(6)] string ActivityId,
    [property: Id(7)] string? BusinessId,
    [property: Id(8)] string InputsJson,
    [property: Id(9)] string VariablesJson);
