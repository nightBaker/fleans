using Orleans.Concurrency;

namespace Fleans.Application.Grains;

/// <summary>
/// Lightweight singleton that tracks known process definition keys.
/// Used for grain-layer key listing; the admin UI queries the DB directly.
/// </summary>
public interface IProcessDefinitionRegistryGrain : IGrainWithIntegerKey
{
    Task RegisterKey(string processDefinitionKey);

    [ReadOnly]
    Task<List<string>> GetAllKeys();
}
