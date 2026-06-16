using Confluent.Kafka;

namespace Fleans.Streaming.Kafka;

/// <summary>
/// Shared security-config helper applied to all three Kafka client builders
/// (producer, consumer, admin) from the same validated source of truth.
/// </summary>
internal static class KafkaClientConfigExtensions
{
    /// <summary>
    /// Applies the security-protocol and SASL settings from <paramref name="opts"/>
    /// onto <paramref name="config"/>. Throws <see cref="InvalidOperationException"/>
    /// for any misconfigured combination so the silo fails-fast at startup rather than
    /// at first broker connection.
    /// </summary>
    internal static void ApplySecurity(ClientConfig config, KafkaStreamingOptions opts)
    {
        config.SecurityProtocol = opts.SecurityProtocol switch
        {
            KafkaSecurityProtocol.Plaintext     => SecurityProtocol.Plaintext,
            KafkaSecurityProtocol.Ssl           => SecurityProtocol.Ssl,
            KafkaSecurityProtocol.SaslPlaintext => SecurityProtocol.SaslPlaintext,
            KafkaSecurityProtocol.SaslSsl       => SecurityProtocol.SaslSsl,
            _ => throw new InvalidOperationException(
                     $"Unsupported SecurityProtocol: {opts.SecurityProtocol}"),
        };

        if (opts.SecurityProtocol is not (KafkaSecurityProtocol.SaslPlaintext or KafkaSecurityProtocol.SaslSsl))
            return;

        if (opts.SaslMechanism is null)
            throw new InvalidOperationException(
                "SaslMechanism is required when SecurityProtocol is SASL_*");

        config.SaslMechanism = opts.SaslMechanism.Value switch
        {
            KafkaSaslMechanism.Plain       => SaslMechanism.Plain,
            KafkaSaslMechanism.ScramSha256 => SaslMechanism.ScramSha256,
            KafkaSaslMechanism.ScramSha512 => SaslMechanism.ScramSha512,
            KafkaSaslMechanism.OAuthBearer => SaslMechanism.OAuthBearer,
            _ => throw new InvalidOperationException(
                     $"Unsupported SaslMechanism: {opts.SaslMechanism}"),
        };

        if (opts.SaslMechanism.Value is KafkaSaslMechanism.Plain
                                     or KafkaSaslMechanism.ScramSha256
                                     or KafkaSaslMechanism.ScramSha512)
        {
            if (string.IsNullOrEmpty(opts.SaslUsername))
                throw new InvalidOperationException(
                    $"SaslUsername is required when SaslMechanism is {opts.SaslMechanism}");
            if (string.IsNullOrEmpty(opts.SaslPassword))
                throw new InvalidOperationException(
                    $"SaslPassword is required when SaslMechanism is {opts.SaslMechanism}");
            config.SaslUsername = opts.SaslUsername;
            config.SaslPassword = opts.SaslPassword;
        }

        if (opts.SaslMechanism.Value is KafkaSaslMechanism.OAuthBearer)
        {
            if (opts.OAuthBearerTokenProvider is null)
                throw new InvalidOperationException(
                    "OAuthBearerTokenProvider is required when SaslMechanism is OAuthBearer");
            // Handler is wired on the builder at each call site after ApplySecurity returns.
        }
    }
}
