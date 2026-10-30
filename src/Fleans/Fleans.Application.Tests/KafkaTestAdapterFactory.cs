using Fleans.Streaming.Kafka;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Serialization;

namespace Fleans.Application.Tests;

/// <summary>Builds a broker-backed <see cref="KafkaQueueAdapterFactory"/> for the env-gated Kafka tests.</summary>
internal static class KafkaTestAdapterFactory
{
    public static KafkaQueueAdapterFactory Create(KafkaStreamingOptions options)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSerializer();
        services.Configure<SimpleQueueCacheOptions>("StreamProvider", _ => { });
        var sp = services.BuildServiceProvider();

        return new KafkaQueueAdapterFactory(
            "StreamProvider",
            options,
            sp.GetRequiredService<IOptionsMonitor<SimpleQueueCacheOptions>>().Get("StreamProvider"),
            sp.GetRequiredService<Serializer<KafkaBatchContainer>>(),
            sp.GetRequiredService<ILoggerFactory>());
    }
}
