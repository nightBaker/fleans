using Microsoft.Extensions.Configuration;
using Orleans.Hosting;

namespace Microsoft.Extensions.Hosting;

public static class FleanStreamingExtensions
{
    public const string StreamProviderName = "StreamProvider";

    public static ISiloBuilder AddFleanStreaming(this ISiloBuilder builder, IConfiguration configuration)
    {
        var provider = configuration.GetValue<string>("Fleans:Streaming:Provider") ?? "memory";

        return provider switch
        {
            "memory" => builder.AddMemoryStreams(StreamProviderName),
            _ => throw new ArgumentException(
                $"Unknown streaming provider '{provider}'. Supported: memory. " +
                $"To add a provider, install its NuGet package and add a case to {nameof(FleanStreamingExtensions)}.{nameof(AddFleanStreaming)}.")
        };
    }
}
