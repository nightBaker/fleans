using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Hosting;

namespace Fleans.Streaming.Kafka;

/// <summary>
/// Pure math for the production preset — extracted so tests can verify it without a host.
/// </summary>
internal static class KafkaProductionPreset
{
    /// <summary>Max of 1 (safe floor for single-node clusters) and the core count.</summary>
    internal static int NumPartitions(int cores) => Math.Max(1, cores);
}

/// <summary>
/// Chained extension that applies a CPU-sized partition count on top of
/// <see cref="KafkaSiloBuilderExtensions.AddKafkaStreams"/>.
/// </summary>
public static partial class KafkaProductionPresetExtensions
{
    /// <summary>
    /// Overrides <see cref="KafkaStreamingOptions.NumPartitions"/> to
    /// <c>max(1, Environment.ProcessorCount)</c> for the named Kafka stream provider.
    ///
    /// <para>Usage (chain after <c>AddKafkaStreams</c>):</para>
    /// <code>
    /// builder.AddKafkaStreams("kafka", configuration)
    ///        .WithProductionDefaults("kafka");
    /// </code>
    ///
    /// <para><strong>Name must match:</strong> <paramref name="name"/> must be identical to the
    /// name passed to <c>AddKafkaStreams</c>. A mismatch silently binds a different named-options
    /// instance and applies no overrides — the startup INFO log (EventId 11000) will be absent
    /// for that provider name.</para>
    ///
    /// <para><strong>Why not <see cref="KafkaStreamingOptions.QueueCount"/>:</strong> it is a
    /// cluster-wide hash-ring parameter (mapped to Orleans
    /// <c>HashRingStreamQueueMapperOptions.TotalQueueCount</c>) that every silo must agree on, and
    /// Orleans does not check that they do. Deriving it from the host's core count would let a
    /// mixed-core fleet silently misroute streams (#699), so it stays a plain config value.</para>
    /// </summary>
    public static ISiloBuilder WithProductionDefaults(this ISiloBuilder builder, string name)
    {
        var numPartitions = KafkaProductionPreset.NumPartitions(Environment.ProcessorCount);

        return builder.ConfigureServices(s =>
            s.AddOptions<KafkaStreamingOptions>(name)
             .Configure<ILoggerFactory>((o, loggerFactory) =>
             {
                 o.NumPartitions = numPartitions;
                 LogProductionPresetApplied(
                     loggerFactory.CreateLogger("Fleans.Streaming.Kafka"),
                     name, numPartitions, Environment.ProcessorCount);
             }));
    }

    [LoggerMessage(EventId = 11000, Level = LogLevel.Information,
        Message = "Kafka stream provider '{ProviderName}' production preset applied: " +
                  "NumPartitions={NumPartitions} (ProcessorCount={ProcessorCount}).")]
    private static partial void LogProductionPresetApplied(
        ILogger logger, string ProviderName, int NumPartitions, int ProcessorCount);
}
