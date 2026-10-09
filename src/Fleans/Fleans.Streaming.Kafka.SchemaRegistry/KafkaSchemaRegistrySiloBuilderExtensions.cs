using Confluent.SchemaRegistry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Hosting;

namespace Fleans.Streaming.Kafka.SchemaRegistry;

public static class KafkaSchemaRegistrySiloBuilderExtensions
{
    /// <summary>
    /// Registers <see cref="ISchemaRegistryClient"/> for Schema Registry integration.
    /// Pair with <c>AddKafkaStreams</c> and an <c>IExternalEventEncoder</c> implementation
    /// (from a framing package such as #685B Avro or #685C Protobuf) to activate the
    /// two-topic fanout in <c>KafkaQueueAdapter</c>.
    /// </summary>
    public static ISiloBuilder AddKafkaSchemaRegistry(
        this ISiloBuilder builder,
        IConfiguration configuration)
    {
        builder.Services.Configure<KafkaSchemaRegistryOptions>(opt => configuration.Bind(opt));

        builder.Services.AddSingleton<ISchemaRegistryClient>(sp =>
        {
            var opts = sp.GetRequiredService<IOptionsMonitor<KafkaSchemaRegistryOptions>>()
                         .CurrentValue;
            var cfg = new SchemaRegistryConfig { Url = opts.Url };

            // Basic auth
            if (opts.BasicAuthUsername is not null)
                cfg.BasicAuthUserInfo = $"{opts.BasicAuthUsername}:{opts.BasicAuthPassword}";

            // CA verification (PEM path, same field name as Kafka ClientConfig)
            if (opts.SslCaLocation is not null)
                cfg.SslCaLocation = opts.SslCaLocation;

            // mTLS — PKCS12 keystore (different format from Kafka's PEM cert+key pair)
            if (opts.SslKeystoreLocation is not null)
            {
                cfg.SslKeystoreLocation = opts.SslKeystoreLocation;
                cfg.SslKeystorePassword = opts.SslKeystorePassword;
            }

            return new CachedSchemaRegistryClient(cfg);
        });

        return builder;
    }
}
