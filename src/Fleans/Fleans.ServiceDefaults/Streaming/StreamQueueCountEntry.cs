using Orleans;

namespace Fleans.ServiceDefaults.Streaming;

[GenerateSerializer]
internal sealed record StreamQueueCountEntry(string SiloAddress, string ProviderName, int QueueCount);
