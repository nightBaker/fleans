using Fleans.Streaming.AzureQueue;
using Fleans.Streaming.Kafka;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Configuration;
using Orleans.Hosting;
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
            .Configure(options => options.TotalQueueCount = 8);

        builder.Services.AddOptions<SimpleQueueCacheOptions>(StreamProviderName);

        builder.Services.AddOptions<RedisStreamReceiverOptions>(StreamProviderName)
            .Configure(options =>
            {
                var section = configuration.GetSection("Fleans:Streaming:Redis");
                options.MaxStreamLength = section.GetValue<int?>("MaxStreamLength") ?? 1000;
                options.TrimTimeMinutes = section.GetValue<int?>("TrimTimeMinutes") ?? 5;
            });

        return builder.AddPersistentStreams(StreamProviderName, RedisStreamFactory.Create, null);
    }
}
