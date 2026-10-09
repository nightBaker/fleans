namespace Fleans.Streaming.Kafka.SchemaRegistry;

public class KafkaSchemaRegistryOptions
{
    public string Url { get; set; } = string.Empty;

    // Basic auth
    public string? BasicAuthUsername { get; set; }
    public string? BasicAuthPassword { get; set; }

    // TLS — CA verification (PEM path, same field name as Confluent.SchemaRegistry SslCaLocation)
    public string? SslCaLocation { get; set; }

    // mTLS — client certificate as PKCS12 keystore.
    // SchemaRegistryConfig uses PKCS12 format, NOT the PEM cert+key pair used by Confluent.Kafka
    // ClientConfig. Operators must convert PEM to PKCS12 before using these fields:
    //   openssl pkcs12 -export -in cert.pem -inkey key.pem -out client.p12
    public string? SslKeystoreLocation { get; set; }
    public string? SslKeystorePassword { get; set; }
}
