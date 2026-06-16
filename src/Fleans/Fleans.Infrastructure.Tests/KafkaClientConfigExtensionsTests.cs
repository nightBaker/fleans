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
