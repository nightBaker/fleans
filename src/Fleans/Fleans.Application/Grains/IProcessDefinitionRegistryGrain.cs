using Orleans.Concurrency;

namespace Fleans.Application.Grains;

/// <summary>
/// Lightweight singleton that tracks all known process definition keys.
/// Used only for grain-layer operations (e.g. start event listener fan-out listing).
/// The admin UI queries the DB directly via IWorkflowQueryService.
/// </summary>
public interface IProcessDefinitionRegistryGrain : IGrainWithIntegerKey
{
    Task RegisterKey(string processDefinitionKey);

    [ReadOnly]
    Task<List<string>> GetAllKeys();
}
