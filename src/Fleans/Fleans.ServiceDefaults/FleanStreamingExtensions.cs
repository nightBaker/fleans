using System.Globalization;
using Fleans.Application.Abstractions.Events;
using Fleans.Streaming.AzureQueue;
using Fleans.Streaming.Kafka;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Streams;
using StackExchange.Redis;
using Universley.OrleansContrib.StreamsProvider.Redis;

namespace Microsoft.Extensions.Hosting;

public static class FleanStreamingExtensions
{
    public const string StreamProviderName = "StreamProvider";

    /// <summary>
    /// Default for <c>Fleans:Streaming:Redis:MaxStreamLength</c>. The Redis provider trims by length
    /// (<c>XTRIM MAXLEN ~</c>) whether or not the consumer group has read an entry, so the cap must
    /// stay far above any backlog a queue can build up (#850).
    /// </summary>
    public const int DefaultRedisMaxStreamLength = 100_000;

    /// <summary>
    /// Configures the Orleans stream provider. Reads <c>Fleans:Streaming:Provider</c> from config
    /// (default: <c>redis</c>; supported: <c>memory</c>, <c>kafka</c>, <c>azurequeue</c>, <c>redis</c>; matched case-insensitively).
    /// Every provider uses <see cref="StreamPubSubType.ImplicitOnly"/>: all Fleans subscribers are
    /// <c>[ImplicitStreamSubscription]</c> grains, so no rendezvous grain and no <c>PubSubStore</c>
    /// grain storage is needed (#911). The <c>redis</c> provider requires a keyed
    /// <c>IConnectionMultiplexer</c> named <c>orleans-redis</c> in DI (Aspire's
    /// <c>AddKeyedRedisClient("orleans-redis")</c>).
    /// </summary>
    public static ISiloBuilder AddFleanStreaming(this ISiloBuilder builder, IConfiguration configuration)
    {
        var provider = configuration.GetValue<string>("Fleans:Streaming:Provider") ?? "redis";

        var pollPeriod = ReadPollPeriod(configuration);
        var maxEventDeliveryTime = ReadMaxEventDeliveryTime(configuration);
        // Registered before the provider, so Kafka's own DeliveryRetryWindow (applied by
        // AddKafkaStreams) still wins when it is set.
        builder.Services.Configure<StreamPullingAgentOptions>(StreamProviderName, o =>
        {
            if (pollPeriod is { } period)
                o.GetQueueMsgsTimerPeriod = period;
            o.MaxEventDeliveryTime = maxEventDeliveryTime;
        });

        return provider.ToLowerInvariant() switch
        {
            "memory" => builder.AddMemoryStreams(StreamProviderName,
                b => b.ConfigureStreamPubSub(StreamPubSubType.ImplicitOnly)),
            "kafka" => builder.AddKafkaStreams(
                StreamProviderName,
                configuration.GetSection("Fleans:Streaming:Kafka")),
            "azurequeue" => builder.AddAzureQueueStreaming(
                StreamProviderName,
                configuration.GetSection("Fleans:Streaming:AzureQueue")),
            "redis" => AddRedisStreams(builder, configuration),
            _ => throw new ArgumentException(
                $"Unknown streaming provider '{provider}'. Supported: memory, kafka, azurequeue, redis. " +
                $"To add a provider, install its NuGet package and add a case to {nameof(FleanStreamingExtensions)}.{nameof(AddFleanStreaming)}.")
        };
    }

    private static ISiloBuilder AddRedisStreams(ISiloBuilder builder, IConfiguration configuration)
    {
        // The third-party package resolves a non-keyed IConnectionMultiplexer from DI. Aspire's
        // AddKeyedRedisClient("orleans-redis") registers a *keyed* one — alias it through.
        // TryAddSingleton lets a caller register an explicit non-keyed multiplexer first if needed.
        builder.Services.TryAddSingleton<IConnectionMultiplexer>(sp =>
            sp.GetRequiredKeyedService<IConnectionMultiplexer>("orleans-redis"));

        builder.Services.AddOptions<HashRingStreamQueueMapperOptions>(StreamProviderName)
            .Configure<IConfiguration>((options, cfg) => options.TotalQueueCount = ReadRedisTotalQueueCount(cfg));

        builder.Services.AddOptions<SimpleQueueCacheOptions>(StreamProviderName);

        builder.Services.AddOptions<RedisStreamReceiverOptions>(StreamProviderName)
            .Configure(options =>
            {
                var section = configuration.GetSection("Fleans:Streaming:Redis");
                options.MaxStreamLength = section.GetValue<int?>("MaxStreamLength") ?? DefaultRedisMaxStreamLength;
                options.TrimTimeMinutes = section.GetValue<int?>("TrimTimeMinutes") ?? 5;
            });

        return builder.AddPersistentStreams(StreamProviderName, RedisStreamFactory.Create,
            b => b.ConfigureStreamPubSub(StreamPubSubType.ImplicitOnly));
    }

    /// <summary>
    /// Reads <c>Fleans:Streaming:PollPeriod</c> — how often each pulling agent reads its queue
    /// (Orleans' <c>StreamPullingAgentOptions.GetQueueMsgsTimerPeriod</c>), for every provider.
    /// <c>null</c> when absent: Orleans' default of 100 ms applies. A stream event waits up to one
    /// period before delivery; a shorter period lowers that wait and polls the backend more often.
    /// Throws <see cref="ArgumentException"/> when the value is not a positive <c>TimeSpan</c>.
    /// </summary>
    public static TimeSpan? ReadPollPeriod(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var raw = configuration["Fleans:Streaming:PollPeriod"];
        if (raw is null) return null;
        if (!TimeSpan.TryParse(raw, CultureInfo.InvariantCulture, out var period) || period <= TimeSpan.Zero)
        {
            throw new ArgumentException(
                $"Fleans:Streaming:PollPeriod must be a positive TimeSpan such as 00:00:00.020 (got '{raw}'). " +
                "Set Fleans__Streaming__PollPeriod or remove it for the 100 ms default.");
        }
        return period;
    }

    /// <summary>
    /// Reads <c>Fleans:Streaming:MaxEventDeliveryTime</c> — how long a pulling agent keeps retrying
    /// one stream event before skipping it for good (Orleans'
    /// <c>StreamPullingAgentOptions.MaxEventDeliveryTime</c>), for every provider. Returns
    /// <see cref="WorkflowEventStreams.DefaultMaxEventDeliveryTime"/> (6 minutes) when absent. Throws
    /// <see cref="ArgumentException"/> when the value is not a positive <c>TimeSpan</c>.
    /// </summary>
    public static TimeSpan ReadMaxEventDeliveryTime(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var raw = configuration["Fleans:Streaming:MaxEventDeliveryTime"];
        if (raw is null) return WorkflowEventStreams.DefaultMaxEventDeliveryTime;
        if (!TimeSpan.TryParse(raw, CultureInfo.InvariantCulture, out var window) || window <= TimeSpan.Zero)
        {
            throw new ArgumentException(
                $"Fleans:Streaming:MaxEventDeliveryTime must be a positive TimeSpan such as 00:06:00 (got '{raw}'). " +
                "Set Fleans__Streaming__MaxEventDeliveryTime or remove it for the 6-minute default.");
        }
        return window;
    }

    /// <summary>
    /// Resolves the Redis Orleans-parallelism knob from <c>Fleans:Streaming:Redis:TotalQueueCount</c>:
    /// returns <c>8</c> when absent; throws <see cref="ArgumentException"/> when the value is not a
    /// parseable integer or is less than <c>1</c>. Bumping the count rehashes Stream IDs across queues
    /// — expect in-flight stalls across the bump window (no formal drain procedure pre-v1; see
    /// <c>reference/streaming.md</c> "Tuning throughput").
    /// </summary>
    public static int ReadRedisTotalQueueCount(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var raw = configuration.GetSection("Fleans:Streaming:Redis")["TotalQueueCount"];
        if (raw is null) return 8;
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count))
        {
            throw new ArgumentException(
                $"Fleans:Streaming:Redis:TotalQueueCount must be an integer (got '{raw}'). " +
                "Set Fleans__Streaming__Redis__TotalQueueCount to a positive integer.");
        }
        if (count < 1)
        {
            throw new ArgumentException(
                $"Fleans:Streaming:Redis:TotalQueueCount must be >= 1 (got {count}). " +
                "Set Fleans__Streaming__Redis__TotalQueueCount to a positive integer.");
        }
        return count;
    }
}
