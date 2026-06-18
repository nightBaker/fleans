using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Runtime;

namespace Fleans.ServiceDefaults.Streaming;

internal sealed partial class StreamQueueCountProbe : ILifecycleParticipant<ISiloLifecycle>
{
    private readonly string _providerName;
    private readonly int _localQueueCount;
    private readonly IGrainFactory _grainFactory;
    private readonly ILocalSiloDetails _siloDetails;
    private readonly ILogger<StreamQueueCountProbe> _logger;

    internal StreamQueueCountProbe(
        string providerName,
        int localQueueCount,
        IGrainFactory grainFactory,
        ILocalSiloDetails siloDetails,
        ILogger<StreamQueueCountProbe> logger)
    {
        _providerName = providerName;
        _localQueueCount = localQueueCount;
        _grainFactory = grainFactory;
        _siloDetails = siloDetails;
        _logger = logger;
    }

    public void Participate(ISiloLifecycle observer) =>
        observer.Subscribe(
            nameof(StreamQueueCountProbe),
            ServiceLifecycleStage.Active,
            RunProbeAsync);

    internal async Task RunProbeAsync(CancellationToken ct)
    {
        try
        {
            var registry = _grainFactory.GetGrain<IStreamQueueCountRegistryGrain>(0);
            var localAddress = _siloDetails.SiloAddress.ToString();

            await registry.RegisterAsync(new StreamQueueCountEntry(
                SiloAddress: localAddress,
                ProviderName: _providerName,
                QueueCount: _localQueueCount));

            var entries = await registry.GetEntriesAsync(_providerName);

            var management = _grainFactory.GetGrain<IManagementGrain>(0);
            var activeHosts = await management.GetDetailedHosts(onlyActive: true);
            var activeAddresses = new HashSet<string>(
                activeHosts.Select(h => h.SiloAddress.ToString()),
                StringComparer.Ordinal);

            foreach (var entry in entries)
            {
                if (entry.SiloAddress == localAddress) continue;
                if (!activeAddresses.Contains(entry.SiloAddress)) continue;
                if (entry.QueueCount != _localQueueCount)
                    LogQueueCountMismatch(_providerName, localAddress, _localQueueCount,
                        entry.SiloAddress, entry.QueueCount);
            }
        }
        catch (Exception ex)
        {
            LogProbeFailed(_providerName, ex);
        }
    }

    [LoggerMessage(EventId = 11300, Level = LogLevel.Warning,
        Message = "Stream queue count mismatch for provider '{ProviderName}': " +
                  "local silo '{LocalSilo}' has {LocalCount} queue(s) but peer '{PeerSilo}' has {PeerCount}. " +
                  "All silos must share the same queue count to avoid stream misrouting.")]
    private partial void LogQueueCountMismatch(
        string providerName, string localSilo, int localCount, string peerSilo, int peerCount);

    [LoggerMessage(EventId = 11301, Level = LogLevel.Error,
        Message = "Stream queue count homogeneity probe failed for provider '{ProviderName}'. " +
                  "The check is best-effort; silo startup continues normally.")]
    private partial void LogProbeFailed(string providerName, Exception ex);
}
