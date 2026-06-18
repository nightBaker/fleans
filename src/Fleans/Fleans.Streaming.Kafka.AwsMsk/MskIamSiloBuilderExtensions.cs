using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Hosting;

namespace Fleans.Streaming.Kafka;

public static class MskIamSiloBuilderExtensions
{
    /// <summary>
    /// Registers the Kafka-backed Orleans stream provider with AWS MSK IAM authentication.
    /// IAM credentials come from the workload identity chain (instance profile, EKS Pod Identity,
    /// ECS task role, etc.) — no static secrets are needed in configuration.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <param name="name">Stream provider name. Must match the name used throughout the cluster.</param>
    /// <param name="configuration">Configuration section to bind <see cref="KafkaStreamingOptions"/> from.</param>
    /// <param name="region">
    /// AWS region. When null, falls back to the <c>AWS_REGION</c> then <c>AWS_DEFAULT_REGION</c>
    /// environment variables. Throws <see cref="InvalidOperationException"/> if no region is found.
    /// </param>
    public static ISiloBuilder AddKafkaStreamingWithMskIam(
        this ISiloBuilder builder,
        string name,
        IConfiguration configuration,
        string? region = null)
    {
        var effectiveRegion = region
            ?? Environment.GetEnvironmentVariable("AWS_REGION")
            ?? Environment.GetEnvironmentVariable("AWS_DEFAULT_REGION")
            ?? throw new InvalidOperationException(
                "AWS region must be supplied via the `region` parameter or the AWS_REGION env var.");

        // Validate at extension-call time so a bad region fails fast with a clear error
        // rather than surfacing as a generic SASL handshake failure on the first broker connect.
        if (string.IsNullOrEmpty(effectiveRegion))
            throw new ArgumentException(
                "AWS region must not be empty.", nameof(region));
        _ = Amazon.RegionEndpoint.GetBySystemName(effectiveRegion);

        return builder
            .AddKafkaStreams(name, configuration)
            .ConfigureServices(s =>
                s.AddOptions<KafkaStreamingOptions>(name)
                 .Configure<ILoggerFactory>((o, loggerFactory) =>
                 {
                     var logger = loggerFactory.CreateLogger("Fleans.Streaming.Kafka.AwsMsk");
                     o.SecurityProtocol        = KafkaSecurityProtocol.SaslSsl;
                     o.SaslMechanism           = KafkaSaslMechanism.OAuthBearer;
                     o.OAuthBearerTokenProvider = (c, _) =>
                         MskIamTokenRefresher.Refresh(c, effectiveRegion, logger);
                 }));
    }
}
