using System.Globalization;
using Fleans.ServiceDefaults.Streaming;
using Fleans.Streaming.AzureQueue;
using Fleans.Streaming.Kafka;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Runtime;
using StackExchange.Redis;
using Universley.OrleansContrib.StreamsProvider.Redis;

namespace Microsoft.Extensions.Hosting;

public static class FleanStreamingExtensions
{
    public const string StreamProviderName = "StreamProvider";

    /// <summary>
    /// Configures the Orleans stream provider. Reads <c>Fleans:Streaming:Provider</c> from config
    /// (default: <c>redis</c>; supported: <c>memory</c>, <c>kafka</c>, <c>azurequeue</c>, <c>redis</c>; matched case-insensitively).
    /// Requires <c>PubSubStore</c> grain storage to be configured by the Aspire AppHost. The
    /// <c>redis</c> provider also requires a keyed <c>IConnectionMultiplexer</c> named
    /// <c>orleans-redis</c> in DI (Aspire's <c>AddKeyedRedisClient("orleans-redis")</c>).
    /// </summary>
    public static ISiloBuilder AddFleanStreaming(this ISiloBuilder builder, IConfiguration configuration)
    {
        var provider = configuration.GetValue<string>("Fleans:Streaming:Provider") ?? "redis";

        return provider.ToLowerInvariant() switch
        {
            "memory" => builder.AddMemoryStreams(StreamProviderName),
            "kafka" => AddKafkaStreamingWithProbe(builder, configuration),
            "azurequeue" => AddAzureQueueStreamingWithProbe(builder, configuration),
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
                options.MaxStreamLength = section.GetValue<int?>("MaxStreamLength") ?? 1000;
                options.TrimTimeMinutes = section.GetValue<int?>("TrimTimeMinutes") ?? 5;
            });

        builder.Services.AddSingleton<ILifecycleParticipant<ISiloLifecycle>>(sp =>
            new StreamQueueCountProbe(
                providerName: StreamProviderName,
                localQueueCount: ReadRedisTotalQueueCount(configuration),
                grainFactory: sp.GetRequiredService<IGrainFactory>(),
                siloDetails: sp.GetRequiredService<ILocalSiloDetails>(),
                logger: sp.GetRequiredService<ILogger<StreamQueueCountProbe>>()));

        return builder.AddPersistentStreams(StreamProviderName, RedisStreamFactory.Create, null);
    }

    private static ISiloBuilder AddKafkaStreamingWithProbe(ISiloBuilder builder, IConfiguration configuration)
    {
        builder.Services.AddSingleton<ILifecycleParticipant<ISiloLifecycle>>(sp =>
            new StreamQueueCountProbe(
                providerName: StreamProviderName,
                localQueueCount: sp.GetRequiredService<IOptionsMonitor<KafkaStreamingOptions>>()
                                   .Get(StreamProviderName).QueueCount,
                grainFactory: sp.GetRequiredService<IGrainFactory>(),
                siloDetails: sp.GetRequiredService<ILocalSiloDetails>(),
                logger: sp.GetRequiredService<ILogger<StreamQueueCountProbe>>()));
        return builder.AddKafkaStreams(StreamProviderName, configuration.GetSection("Fleans:Streaming:Kafka"));
    }

    private static ISiloBuilder AddAzureQueueStreamingWithProbe(ISiloBuilder builder, IConfiguration configuration)
    {
        var aqOpts = configuration.GetSection("Fleans:Streaming:AzureQueue")
            .Get<AzureQueueStreamingOptions>() ?? new();
        builder.Services.AddSingleton<ILifecycleParticipant<ISiloLifecycle>>(sp =>
            new StreamQueueCountProbe(
                providerName: StreamProviderName,
                localQueueCount: aqOpts.QueueNames.Count,
                grainFactory: sp.GetRequiredService<IGrainFactory>(),
                siloDetails: sp.GetRequiredService<ILocalSiloDetails>(),
                logger: sp.GetRequiredService<ILogger<StreamQueueCountProbe>>()));
        return builder.AddAzureQueueStreaming(StreamProviderName,
            configuration.GetSection("Fleans:Streaming:AzureQueue"));
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
