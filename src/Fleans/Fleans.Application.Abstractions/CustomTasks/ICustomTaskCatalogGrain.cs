using Orleans.Concurrency;

namespace Fleans.Application.CustomTasks;

/// <summary>
/// Singleton grain on Core silos that records which custom-task plugins are deployed
/// on which Worker silos. Workers register themselves at silo startup; the catalog
/// reconciles entries against cluster membership periodically and drops entries
/// whose silo has left the cluster. Read by the management UI via
/// <c>GET /custom-tasks</c>.
/// </summary>
public interface ICustomTaskCatalogGrain : IGrainWithIntegerKey
{
    /// <summary>Idempotent upsert keyed by (TaskType, SiloName).</summary>
    Task Register(CustomTaskRegistration entry);

    /// <summary>Returns one row per task type, aggregating silos.</summary>
    [ReadOnly]
    Task<IReadOnlyList<CustomTaskCatalogEntry>> GetAll();

    /// <summary>Returns the entry for one task type, or <c>null</c> if no plugin claims it.</summary>
    [ReadOnly]
    Task<CustomTaskCatalogEntry?> Get(string taskType);
}
