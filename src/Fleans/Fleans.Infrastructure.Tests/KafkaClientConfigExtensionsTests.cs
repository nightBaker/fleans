using Confluent.Kafka;
using Fleans.Streaming.Kafka;

namespace Fleans.Infrastructure.Tests;

/// <summary>
/// Verifies <c>KafkaClientConfigExtensions.ApplySecurity</c> across the 11-case design matrix
/// plus empty-string variants for credential validation (review minor finding).
/// Tests target the internal helper directly — no host required.
/// </summary>
[TestClass]
public class KafkaClientConfigExtensionsTests
{
    // --- Case 1: Plaintext ---

    [TestMethod]
    public void Plaintext_sets_protocol_and_no_sasl_props()
    {
        var config = new ProducerConfig();
        var opts = new KafkaStreamingOptions { SecurityProtocol = KafkaSecurityProtocol.Plaintext };

        KafkaClientConfigExtensions.ApplySecurity(config, opts);

        Assert.AreEqual(SecurityProtocol.Plaintext, config.SecurityProtocol);
        Assert.IsNull(config.SaslMechanism);
        Assert.IsNull(config.SaslUsername);
        Assert.IsNull(config.SaslPassword);
    }

    // --- Case 2: SSL ---

    [TestMethod]
    public void Ssl_sets_protocol_and_no_sasl_props()
    {
        var config = new ProducerConfig();
        var opts = new KafkaStreamingOptions { SecurityProtocol = KafkaSecurityProtocol.Ssl };

        KafkaClientConfigExtensions.ApplySecurity(config, opts);

        Assert.AreEqual(SecurityProtocol.Ssl, config.SecurityProtocol);
        Assert.IsNull(config.SaslMechanism);
        Assert.IsNull(config.SaslUsername);
        Assert.IsNull(config.SaslPassword);
    }

    // --- Case 3: SaslPlaintext + Plain + valid creds ---

    [TestMethod]
    public void SaslPlaintext_Plain_sets_protocol_mechanism_and_credentials()
    {
        var config = new ProducerConfig();
        var opts = new KafkaStreamingOptions
        {
            SecurityProtocol = KafkaSecurityProtocol.SaslPlaintext,
            SaslMechanism    = KafkaSaslMechanism.Plain,
            SaslUsername     = "user",
            SaslPassword     = "pass",
        };

        KafkaClientConfigExtensions.ApplySecurity(config, opts);

        Assert.AreEqual(SecurityProtocol.SaslPlaintext, config.SecurityProtocol);
        Assert.AreEqual(SaslMechanism.Plain, config.SaslMechanism);
        Assert.AreEqual("user", config.SaslUsername);
        Assert.AreEqual("pass", config.SaslPassword);
    }

    // --- Case 4: SaslPlaintext + ScramSha256 + valid creds ---

    [TestMethod]
    public void SaslPlaintext_ScramSha256_sets_protocol_mechanism_and_credentials()
    {
        var config = new ProducerConfig();
        var opts = new KafkaStreamingOptions
        {
            SecurityProtocol = KafkaSecurityProtocol.SaslPlaintext,
            SaslMechanism    = KafkaSaslMechanism.ScramSha256,
            SaslUsername     = "u",
            SaslPassword     = "p",
        };

        KafkaClientConfigExtensions.ApplySecurity(config, opts);

        Assert.AreEqual(SecurityProtocol.SaslPlaintext, config.SecurityProtocol);
        Assert.AreEqual(SaslMechanism.ScramSha256, config.SaslMechanism);
        Assert.AreEqual("u", config.SaslUsername);
        Assert.AreEqual("p", config.SaslPassword);
    }

    // --- Case 5: SaslSsl + ScramSha512 + valid creds ---

    [TestMethod]
    public void SaslSsl_ScramSha512_sets_protocol_mechanism_and_credentials()
    {
        var config = new ProducerConfig();
        var opts = new KafkaStreamingOptions
        {
            SecurityProtocol = KafkaSecurityProtocol.SaslSsl,
            SaslMechanism    = KafkaSaslMechanism.ScramSha512,
            SaslUsername     = "u",
            SaslPassword     = "p",
        };

        KafkaClientConfigExtensions.ApplySecurity(config, opts);

        Assert.AreEqual(SecurityProtocol.SaslSsl, config.SecurityProtocol);
        Assert.AreEqual(SaslMechanism.ScramSha512, config.SaslMechanism);
        Assert.AreEqual("u", config.SaslUsername);
        Assert.AreEqual("p", config.SaslPassword);
    }

    // --- Case 6: SaslSsl + OAuthBearer + non-null provider ---

    [TestMethod]
    public void SaslSsl_OAuthBearer_sets_protocol_and_mechanism_without_credentials()
    {
        var config = new ProducerConfig();
        var opts = new KafkaStreamingOptions
        {
            SecurityProtocol         = KafkaSecurityProtocol.SaslSsl,
            SaslMechanism            = KafkaSaslMechanism.OAuthBearer,
            OAuthBearerTokenProvider = (_, _) => { },
        };

        KafkaClientConfigExtensions.ApplySecurity(config, opts);

        Assert.AreEqual(SecurityProtocol.SaslSsl, config.SecurityProtocol);
        Assert.AreEqual(SaslMechanism.OAuthBearer, config.SaslMechanism);
        Assert.IsNull(config.SaslUsername);
        Assert.IsNull(config.SaslPassword);
    }

    // --- Case 6b: SaslPlaintext + OAuthBearer (structural completeness) ---

    [TestMethod]
    public void SaslPlaintext_OAuthBearer_sets_protocol_and_mechanism_without_credentials()
    {
        var config = new ProducerConfig();
        var opts = new KafkaStreamingOptions
        {
            SecurityProtocol         = KafkaSecurityProtocol.SaslPlaintext,
            SaslMechanism            = KafkaSaslMechanism.OAuthBearer,
            OAuthBearerTokenProvider = (_, _) => { },
        };

        KafkaClientConfigExtensions.ApplySecurity(config, opts);

        Assert.AreEqual(SecurityProtocol.SaslPlaintext, config.SecurityProtocol);
        Assert.AreEqual(SaslMechanism.OAuthBearer, config.SaslMechanism);
        Assert.IsNull(config.SaslUsername);
        Assert.IsNull(config.SaslPassword);
    }

    // --- Case 7: SaslPlaintext + null SaslMechanism → throws ---

    [TestMethod]
    public void SaslPlaintext_null_mechanism_throws()
    {
        var config = new ProducerConfig();
        var opts = new KafkaStreamingOptions
        {
            SecurityProtocol = KafkaSecurityProtocol.SaslPlaintext,
            SaslMechanism    = null,
        };

        Assert.ThrowsExactly<InvalidOperationException>(
            () => KafkaClientConfigExtensions.ApplySecurity(config, opts));
    }

    // --- Case 8: Unknown enum value → throws ---

    [TestMethod]
    public void Unknown_SecurityProtocol_throws()
    {
        var config = new ProducerConfig();
        var opts = new KafkaStreamingOptions
        {
            SecurityProtocol = (KafkaSecurityProtocol)99,
        };

        Assert.ThrowsExactly<InvalidOperationException>(
            () => KafkaClientConfigExtensions.ApplySecurity(config, opts));
    }

    // --- Case 9: null SaslUsername → throws ---

    [TestMethod]
    public void SaslPlaintext_Plain_null_username_throws()
    {
        var config = new ProducerConfig();
        var opts = new KafkaStreamingOptions
        {
            SecurityProtocol = KafkaSecurityProtocol.SaslPlaintext,
            SaslMechanism    = KafkaSaslMechanism.Plain,
            SaslUsername     = null,
            SaslPassword     = "pass",
        };

        Assert.ThrowsExactly<InvalidOperationException>(
            () => KafkaClientConfigExtensions.ApplySecurity(config, opts));
    }

    // --- Case 9b: empty SaslUsername → throws ---

    [TestMethod]
    public void SaslPlaintext_Plain_empty_username_throws()
    {
        var config = new ProducerConfig();
        var opts = new KafkaStreamingOptions
        {
            SecurityProtocol = KafkaSecurityProtocol.SaslPlaintext,
            SaslMechanism    = KafkaSaslMechanism.Plain,
            SaslUsername     = "",
            SaslPassword     = "pass",
        };

        Assert.ThrowsExactly<InvalidOperationException>(
            () => KafkaClientConfigExtensions.ApplySecurity(config, opts));
    }

    // --- Case 10: null SaslPassword → throws ---

    [TestMethod]
    public void SaslPlaintext_Plain_null_password_throws()
    {
        var config = new ProducerConfig();
        var opts = new KafkaStreamingOptions
        {
            SecurityProtocol = KafkaSecurityProtocol.SaslPlaintext,
            SaslMechanism    = KafkaSaslMechanism.Plain,
            SaslUsername     = "user",
            SaslPassword     = null,
        };

        Assert.ThrowsExactly<InvalidOperationException>(
            () => KafkaClientConfigExtensions.ApplySecurity(config, opts));
    }

    // --- Case 10b: empty SaslPassword → throws ---

    [TestMethod]
    public void SaslPlaintext_Plain_empty_password_throws()
    {
        var config = new ProducerConfig();
        var opts = new KafkaStreamingOptions
        {
            SecurityProtocol = KafkaSecurityProtocol.SaslPlaintext,
            SaslMechanism    = KafkaSaslMechanism.Plain,
            SaslUsername     = "user",
            SaslPassword     = "",
        };

        Assert.ThrowsExactly<InvalidOperationException>(
            () => KafkaClientConfigExtensions.ApplySecurity(config, opts));
    }

    // --- Case 11: SaslSsl + OAuthBearer + null provider → throws ---

    [TestMethod]
    public void SaslSsl_OAuthBearer_null_provider_throws()
    {
        var config = new ProducerConfig();
        var opts = new KafkaStreamingOptions
        {
            SecurityProtocol         = KafkaSecurityProtocol.SaslSsl,
            SaslMechanism            = KafkaSaslMechanism.OAuthBearer,
            OAuthBearerTokenProvider = null,
        };

        Assert.ThrowsExactly<InvalidOperationException>(
            () => KafkaClientConfigExtensions.ApplySecurity(config, opts));
    }

    // --- SSL / mTLS cases (S1–S9) ---

    [TestMethod]
    public void S1_Ssl_no_ssl_paths_sets_protocol_only()
    {
        var config = new ProducerConfig();
        var opts = new KafkaStreamingOptions { SecurityProtocol = KafkaSecurityProtocol.Ssl };

        KafkaClientConfigExtensions.ApplySecurity(config, opts);

        Assert.AreEqual(SecurityProtocol.Ssl, config.SecurityProtocol);
        Assert.IsNull(config.SslCaLocation);
        Assert.IsNull(config.SslCertificateLocation);
        Assert.IsNull(config.SslKeyLocation);
        Assert.IsNull(config.SslKeyPassword);
    }

    [TestMethod]
    public void S1b_SaslSsl_with_full_mtls_triple_sets_both_sasl_and_ssl_props()
    {
        var config = new ProducerConfig();
        var opts = new KafkaStreamingOptions
        {
            SecurityProtocol      = KafkaSecurityProtocol.SaslSsl,
            SaslMechanism         = KafkaSaslMechanism.Plain,
            SaslUsername          = "user",
            SaslPassword          = "pass",
            SslCaLocation         = "/etc/kafka/ca.pem",
            SslCertificateLocation = "/etc/kafka/client.pem",
            SslKeyLocation        = "/etc/kafka/client.key",
        };

        KafkaClientConfigExtensions.ApplySecurity(config, opts);

        Assert.AreEqual(SecurityProtocol.SaslSsl, config.SecurityProtocol);
        Assert.AreEqual(SaslMechanism.Plain, config.SaslMechanism);
        Assert.AreEqual("/etc/kafka/ca.pem", config.SslCaLocation);
        Assert.AreEqual("/etc/kafka/client.pem", config.SslCertificateLocation);
        Assert.AreEqual("/etc/kafka/client.key", config.SslKeyLocation);
    }

    [TestMethod]
    public void S2_Ssl_ca_location_only_sets_ca_only()
    {
        var config = new ProducerConfig();
        var opts = new KafkaStreamingOptions
        {
            SecurityProtocol = KafkaSecurityProtocol.Ssl,
            SslCaLocation    = "/etc/kafka/ca.pem",
        };

        KafkaClientConfigExtensions.ApplySecurity(config, opts);

        Assert.AreEqual("/etc/kafka/ca.pem", config.SslCaLocation);
        Assert.IsNull(config.SslCertificateLocation);
        Assert.IsNull(config.SslKeyLocation);
    }

    [TestMethod]
    public void S3_Ssl_full_mtls_triple_sets_all_three_paths()
    {
        var config = new ProducerConfig();
        var opts = new KafkaStreamingOptions
        {
            SecurityProtocol       = KafkaSecurityProtocol.Ssl,
            SslCaLocation          = "/etc/kafka/ca.pem",
            SslCertificateLocation = "/etc/kafka/client.pem",
            SslKeyLocation         = "/etc/kafka/client.key",
        };

        KafkaClientConfigExtensions.ApplySecurity(config, opts);

        Assert.AreEqual("/etc/kafka/ca.pem", config.SslCaLocation);
        Assert.AreEqual("/etc/kafka/client.pem", config.SslCertificateLocation);
        Assert.AreEqual("/etc/kafka/client.key", config.SslKeyLocation);
        Assert.IsNull(config.SslKeyPassword);
    }

    [TestMethod]
    public void S4_Ssl_full_mtls_with_password_sets_all_four_props()
    {
        var config = new ProducerConfig();
        var opts = new KafkaStreamingOptions
        {
            SecurityProtocol       = KafkaSecurityProtocol.Ssl,
            SslCaLocation          = "/etc/kafka/ca.pem",
            SslCertificateLocation = "/etc/kafka/client.pem",
            SslKeyLocation         = "/etc/kafka/client.key",
            SslKeyPassword         = "s3cret",
        };

        KafkaClientConfigExtensions.ApplySecurity(config, opts);

        Assert.AreEqual("/etc/kafka/ca.pem", config.SslCaLocation);
        Assert.AreEqual("/etc/kafka/client.pem", config.SslCertificateLocation);
        Assert.AreEqual("/etc/kafka/client.key", config.SslKeyLocation);
        Assert.AreEqual("s3cret", config.SslKeyPassword);
    }

    [TestMethod]
    public void S5_Ssl_cert_without_key_throws()
    {
        var config = new ProducerConfig();
        var opts = new KafkaStreamingOptions
        {
            SecurityProtocol       = KafkaSecurityProtocol.Ssl,
            SslCertificateLocation = "/etc/kafka/client.pem",
        };

        Assert.ThrowsExactly<InvalidOperationException>(
            () => KafkaClientConfigExtensions.ApplySecurity(config, opts));
    }

    [TestMethod]
    public void S6_Ssl_key_without_cert_throws()
    {
        var config = new ProducerConfig();
        var opts = new KafkaStreamingOptions
        {
            SecurityProtocol = KafkaSecurityProtocol.Ssl,
            SslKeyLocation   = "/etc/kafka/client.key",
        };

        Assert.ThrowsExactly<InvalidOperationException>(
            () => KafkaClientConfigExtensions.ApplySecurity(config, opts));
    }

    [TestMethod]
    public void S7_Ssl_password_without_key_throws()
    {
        var config = new ProducerConfig();
        var opts = new KafkaStreamingOptions
        {
            SecurityProtocol = KafkaSecurityProtocol.Ssl,
            SslKeyPassword   = "s3cret",
        };

        Assert.ThrowsExactly<InvalidOperationException>(
            () => KafkaClientConfigExtensions.ApplySecurity(config, opts));
    }

    [TestMethod]
    public void S8_Plaintext_with_ssl_paths_throws()
    {
        var config = new ProducerConfig();
        var opts = new KafkaStreamingOptions
        {
            SecurityProtocol = KafkaSecurityProtocol.Plaintext,
            SslCaLocation    = "/etc/kafka/ca.pem",
        };

        Assert.ThrowsExactly<InvalidOperationException>(
            () => KafkaClientConfigExtensions.ApplySecurity(config, opts));
    }

    [TestMethod]
    public void S9_SaslPlaintext_with_ssl_paths_throws()
    {
        var config = new ProducerConfig();
        var opts = new KafkaStreamingOptions
        {
            SecurityProtocol = KafkaSecurityProtocol.SaslPlaintext,
            SaslMechanism    = KafkaSaslMechanism.Plain,
            SaslUsername     = "u",
            SaslPassword     = "p",
            SslCaLocation    = "/etc/kafka/ca.pem",
        };

        Assert.ThrowsExactly<InvalidOperationException>(
            () => KafkaClientConfigExtensions.ApplySecurity(config, opts));
    }

    // --- Regression guard: existing options defaults must still work ---

    [TestMethod]
    public void Default_options_plaintext_no_sasl_props()
    {
        var config = new ProducerConfig();
        var opts = new KafkaStreamingOptions(); // SecurityProtocol defaults to Plaintext

        KafkaClientConfigExtensions.ApplySecurity(config, opts);

        Assert.AreEqual(SecurityProtocol.Plaintext, config.SecurityProtocol);
        Assert.IsNull(config.SaslMechanism);
    }

    // --- Works on ConsumerConfig and AdminClientConfig (ClientConfig subclasses) ---

    [TestMethod]
    public void ApplySecurity_works_on_ConsumerConfig()
    {
        var config = new ConsumerConfig();
        var opts = new KafkaStreamingOptions
        {
            SecurityProtocol = KafkaSecurityProtocol.SaslPlaintext,
            SaslMechanism    = KafkaSaslMechanism.Plain,
            SaslUsername     = "u",
            SaslPassword     = "p",
        };

        KafkaClientConfigExtensions.ApplySecurity(config, opts);

        Assert.AreEqual(SecurityProtocol.SaslPlaintext, config.SecurityProtocol);
        Assert.AreEqual(SaslMechanism.Plain, config.SaslMechanism);
    }

    [TestMethod]
    public void ApplySecurity_works_on_AdminClientConfig()
    {
        var config = new AdminClientConfig();
        var opts = new KafkaStreamingOptions
        {
            SecurityProtocol = KafkaSecurityProtocol.Ssl,
        };

        KafkaClientConfigExtensions.ApplySecurity(config, opts);

        Assert.AreEqual(SecurityProtocol.Ssl, config.SecurityProtocol);
    }
}
