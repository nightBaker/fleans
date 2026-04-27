using Fleans.Streaming.Kafka;
using Microsoft.Extensions.Configuration;
using Orleans.Hosting;

namespace Microsoft.Extensions.Hosting;

public static class FleanStreamingExtensions
{
    public const string StreamProviderName = "StreamProvider";

    /// <summary>
    /// Configures the Orleans stream provider. Reads <c>Fleans:Streaming:Provider</c> from config
    /// (default: <c>memory</c>; supported: <c>memory</c>, <c>kafka</c>; matched case-insensitively).
    /// Requires <c>PubSubStore</c> grain storage to be configured by the Aspire AppHost.
    /// </summary>
    public static ISiloBuilder AddFleanStreaming(this ISiloBuilder builder, IConfiguration configuration)
    {
        var provider = configuration.GetValue<string>("Fleans:Streaming:Provider") ?? "memory";

        return provider.ToLowerInvariant() switch
        {
            "memory" => builder.AddMemoryStreams(StreamProviderName),
            "kafka" => builder.AddKafkaStreams(
                StreamProviderName,
                configuration.GetSection("Fleans:Streaming:Kafka")),
            _ => throw new ArgumentException(
                $"Unknown streaming provider '{provider}'. Supported: memory, kafka. " +
                $"To add a provider, install its NuGet package and add a case to {nameof(FleanStreamingExtensions)}.{nameof(AddFleanStreaming)}.")
        };
    }
}
