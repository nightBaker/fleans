using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Streams;

namespace Fleans.Streaming.Kafka;

public static class KafkaSiloBuilderExtensions
{
    /// <summary>
    /// Registers the Kafka-backed Orleans stream provider under the given name and binds
    /// <see cref="KafkaStreamingOptions"/> from the provided configuration section. Defaults
    /// follow the v1 design (plaintext brokers, single queue, RF=1, partitions=1).
    /// </summary>
    public static ISiloBuilder AddKafkaStreams(
        this ISiloBuilder builder,
        string name,
        IConfiguration configuration)
    {
        return builder.AddPersistentStreams(
            name,
            KafkaQueueAdapterFactory.Create,
            b =>
            {
                b.Configure<KafkaStreamingOptions>(o => o.Configure(opt => configuration.Bind(opt)));
                b.Configure<SimpleQueueCacheOptions>(_ => { });
                b.UseConsistentRingQueueBalancer();
                b.ConfigureStreamPubSub(StreamPubSubType.ExplicitGrainBasedAndImplicit);
            });
    }
}
