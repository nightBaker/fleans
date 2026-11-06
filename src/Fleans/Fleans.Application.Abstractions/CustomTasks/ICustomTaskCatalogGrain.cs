using Orleans.Concurrency;

namespace Fleans.Application.CustomTasks;

/// <summary>
/// Singleton grain on Core silos that records which custom-task types the connected gRPC workers
/// implement. A worker holds a lease (<see cref="RegisterWorker"/>) that the engine renews every
/// heartbeat. Read by the management UI via <c>GET /custom-tasks</c>.
/// </summary>
public interface ICustomTaskCatalogGrain : IGrainWithIntegerKey
{
    /// <summary>
    /// Registers or renews a gRPC worker (its name in <see cref="CustomTaskRegistration.SiloName"/>);
    /// idempotent upsert keyed by (TaskType, SiloName).
    /// The row stays while renewals keep arriving within <c>Fleans:ExternalTasks:WorkerTimeout</c>.
    /// </summary>
    Task RegisterWorker(CustomTaskRegistration entry);

    /// <summary>Drops a gRPC worker's row (it disconnected); no-op when absent.</summary>
    Task UnregisterWorker(string taskType, string workerName);

    /// <summary>Returns one row per task type, aggregating workers.</summary>
    [ReadOnly]
    Task<IReadOnlyList<CustomTaskCatalogEntry>> GetAll();

    /// <summary>Returns the entry for one task type, or <c>null</c> if no worker claims it.</summary>
    [ReadOnly]
    Task<CustomTaskCatalogEntry?> Get(string taskType);

    /// <summary>
    /// Replaces a private task type's allow-list. From then on the stored list wins over the one its
    /// workers declare. Deployments are checked against it; running instances are not affected.
    /// </summary>
    /// <exception cref="ArgumentException">A process key is blank.</exception>
    /// <exception cref="InvalidOperationException">The task type is not private.</exception>
    Task<CustomTaskCatalogEntry> SetAllowedProcesses(string taskType, List<string> processKeys);

    /// <summary>
    /// Of <paramref name="taskTypes"/>, the private ones <paramref name="processKey"/> is not allowed
    /// to use. A task type the catalog does not know is public.
    /// </summary>
    [ReadOnly]
    Task<List<string>> GetDisallowedTaskTypes(string processKey, List<string> taskTypes);
}
