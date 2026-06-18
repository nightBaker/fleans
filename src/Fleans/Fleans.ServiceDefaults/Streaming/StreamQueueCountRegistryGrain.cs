using Orleans;

namespace Fleans.ServiceDefaults.Streaming;

internal sealed class StreamQueueCountRegistryGrain : Grain, IStreamQueueCountRegistryGrain
{
    private readonly List<StreamQueueCountEntry> _entries = [];

    public Task RegisterAsync(StreamQueueCountEntry entry)
    {
        _entries.RemoveAll(e => e.SiloAddress == entry.SiloAddress && e.ProviderName == entry.ProviderName);
        _entries.Add(entry);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<StreamQueueCountEntry>> GetEntriesAsync(string providerName)
    {
        IReadOnlyList<StreamQueueCountEntry> result = _entries
            .Where(e => e.ProviderName == providerName)
            .ToList();
        return Task.FromResult(result);
    }
}
