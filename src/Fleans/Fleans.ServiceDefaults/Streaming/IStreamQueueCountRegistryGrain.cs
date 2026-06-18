using Orleans;

namespace Fleans.ServiceDefaults.Streaming;

internal interface IStreamQueueCountRegistryGrain : IGrainWithIntegerKey
{
    Task RegisterAsync(StreamQueueCountEntry entry);
    Task<IReadOnlyList<StreamQueueCountEntry>> GetEntriesAsync(string providerName);
}
