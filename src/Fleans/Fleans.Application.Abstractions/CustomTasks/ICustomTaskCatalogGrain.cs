using Orleans.Concurrency;

namespace Fleans.Application.CustomTasks;

/// <summary>
/// Singleton grain on Core silos that records which custom-task plugins are deployed
/// on which Worker silos, and which external-task types the connected gRPC workers implement.
/// Plugin silos register themselves at silo startup and are reconciled against cluster
/// membership; gRPC workers hold a lease (<see cref="RegisterWorker"/>) that the engine renews
/// every heartbeat. Read by the management UI via <c>GET /custom-tasks</c>.
/// </summary>
public interface ICustomTaskCatalogGrain : IGrainWithIntegerKey
{
    /// <summary>Idempotent upsert keyed by (TaskType, SiloName).</summary>
    Task Register(CustomTaskRegistration entry);

    /// <summary>
    /// Registers or renews an external gRPC worker (its name in <see cref="CustomTaskRegistration.SiloName"/>).
    /// The row stays while renewals keep arriving within <c>Fleans:ExternalTasks:WorkerTimeout</c>.
    /// </summary>
    Task RegisterWorker(CustomTaskRegistration entry);

    /// <summary>Drops an external gRPC worker's row (it disconnected); no-op when absent.</summary>
    Task UnregisterWorker(string taskType, string workerName);

    /// <summary>Returns one row per task type, aggregating silos.</summary>
    [ReadOnly]
    Task<IReadOnlyList<CustomTaskCatalogEntry>> GetAll();

    /// <summary>Returns the entry for one task type, or <c>null</c> if no plugin claims it.</summary>
    [ReadOnly]
    Task<CustomTaskCatalogEntry?> Get(string taskType);
}
