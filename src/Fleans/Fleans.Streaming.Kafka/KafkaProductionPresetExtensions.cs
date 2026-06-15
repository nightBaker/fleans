using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Hosting;

namespace Fleans.Streaming.Kafka;

/// <summary>
/// Pure math for the production preset — extracted so tests can verify it without a host.
/// </summary>
internal static class KafkaProductionPreset
{
    /// <summary>Max of 8 (documented Orleans baseline) and the core count.</summary>
    internal static int QueueCount(int cores) => Math.Max(8, cores);

    /// <summary>Max of 1 (safe floor for single-node clusters) and the core count.</summary>
    internal static int NumPartitions(int cores) => Math.Max(1, cores);
}

/// <summary>
/// Chained extension that applies CPU-sized production defaults on top of
/// <see cref="KafkaSiloBuilderExtensions.AddKafkaStreams"/>.
/// </summary>
public static partial class KafkaProductionPresetExtensions
{
    /// <summary>
    /// Overrides <see cref="KafkaStreamingOptions.QueueCount"/> to
    /// <c>max(8, Environment.ProcessorCount)</c> and
    /// <see cref="KafkaStreamingOptions.NumPartitions"/> to
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
    /// <para><strong>Homogeneity requirement:</strong> <see cref="KafkaStreamingOptions.QueueCount"/>
    /// is a cluster-wide hash-ring parameter (mapped to Orleans
    /// <c>HashRingStreamQueueMapperOptions.TotalQueueCount</c>). All silos MUST have the same
    /// <see cref="Environment.ProcessorCount"/>; a mismatch silently misroutes streams under
    /// rebalance. A cross-silo sanity probe is tracked in #699.</para>
    /// </summary>
    public static ISiloBuilder WithProductionDefaults(this ISiloBuilder builder, string name)
    {
        var queueCount    = KafkaProductionPreset.QueueCount(Environment.ProcessorCount);
        var numPartitions = KafkaProductionPreset.NumPartitions(Environment.ProcessorCount);

        return builder.ConfigureServices(s =>
            s.AddOptions<KafkaStreamingOptions>(name)
             .Configure<ILoggerFactory>((o, loggerFactory) =>
             {
                 o.QueueCount    = queueCount;
                 o.NumPartitions = numPartitions;
                 LogProductionPresetApplied(
                     loggerFactory.CreateLogger("Fleans.Streaming.Kafka"),
                     name, queueCount, numPartitions, Environment.ProcessorCount);
             }));
    }

    [LoggerMessage(EventId = 11000, Level = LogLevel.Information,
        Message = "Kafka stream provider '{ProviderName}' production preset applied: " +
                  "QueueCount={QueueCount}, NumPartitions={NumPartitions} (ProcessorCount={ProcessorCount}). " +
                  "All silos must have identical ProcessorCount — mismatch silently misroutes streams (see #699).")]
    private static partial void LogProductionPresetApplied(
        ILogger logger, string ProviderName, int QueueCount, int NumPartitions, int ProcessorCount);
}
