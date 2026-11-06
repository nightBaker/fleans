using Orleans.Concurrency;

namespace Fleans.Application.ExternalTasks;

/// <summary>
/// The connected workers implementing one external-task type, keyed by
/// <see cref="ExternalTaskWorkerPoolGrain.KeyFor"/>(task type). In memory only: the worker gRPC
/// service renews every membership on each heartbeat, so a reactivated pool refills within one
/// <c>Fleans:ExternalTasks:HeartbeatInterval</c>.
/// </summary>
public interface IExternalTaskWorkerPoolGrain : IGrainWithStringKey
{
    /// <summary>Adds or renews the worker behind <paramref name="connectionId"/>.</summary>
    Task Join(string connectionId, IExternalTaskWorker worker);

    /// <summary>Removes the worker behind <paramref name="connectionId"/>; no-op when absent.</summary>
    Task Leave(string connectionId);

    /// <summary>The workers renewed within <c>Fleans:ExternalTasks:WorkerTimeout</c>.</summary>
    [ReadOnly]
    Task<List<IExternalTaskWorker>> GetWorkers();
}

/// <summary>
/// An external task's outcome, reported by a worker to the <see cref="ExternalTaskDispatcherGrain"/>
/// waiting for it. The calls interleave with that grain's stream delivery, which is what awaits them.
/// </summary>
public interface IExternalTaskDispatcherGrain : IGrainWithStringKey
{
    /// <summary><c>false</c> when the grain no longer waits for <paramref name="dispatchId"/>.</summary>
    [AlwaysInterleave]
    Task<bool> Complete(Guid dispatchId, System.Dynamic.ExpandoObject result);

    /// <summary><c>false</c> when the grain no longer waits for <paramref name="dispatchId"/>.</summary>
    [AlwaysInterleave]
    Task<bool> Fail(Guid dispatchId, string code, string message);

    /// <summary>
    /// The worker took the task; its outcome comes through the REST API. <c>false</c> when the grain
    /// no longer waits for <paramref name="dispatchId"/>.
    /// </summary>
    [AlwaysInterleave]
    Task<bool> Defer(Guid dispatchId);

    /// <summary>
    /// The worker that got <paramref name="dispatchId"/> disconnected before reporting: stop
    /// waiting and let the stream redeliver the task. No-op when it was already settled.
    /// </summary>
    [AlwaysInterleave]
    Task Abandon(Guid dispatchId);
}
