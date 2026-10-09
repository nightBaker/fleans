using Fleans.Streaming.Kafka;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans.Hosting;
using System.Text;

namespace Fleans.Streaming.Kafka.AwsMsk.Tests;

/// <summary>
/// Verifies <see cref="MskIamSiloBuilderExtensions.AddKafkaStreamingWithMskIam"/> wiring,
/// region resolution, and early validation. Tests use <see cref="FakeSiloBuilder"/> to avoid
/// standing up a real Orleans cluster — we test option binding, not Orleans internals.
/// </summary>
[TestClass]
public class MskIamSiloBuilderExtensionsTests
{
    private static readonly IConfiguration EmptyConfig =
        new ConfigurationBuilder().Build();

    // ── Test 1: wiring ────────────────────────────────────────────────────────

    [TestMethod]
    public void Add_with_region_sets_SaslSsl_OAuthBearer_and_token_provider()
    {
        const string name = "test";
        var builder = new FakeSiloBuilder();
        builder.AddKafkaStreamingWithMskIam(name, EmptyConfig, "us-east-1");

        // Resolve named options to trigger the Configure<ILoggerFactory> action.
        var sp = builder.Services
            .AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance)
            .BuildServiceProvider();

        var opts = sp.GetRequiredService<IOptionsMonitor<KafkaStreamingOptions>>().Get(name);

        Assert.AreEqual(KafkaSecurityProtocol.SaslSsl, opts.SecurityProtocol);
        Assert.AreEqual(KafkaSaslMechanism.OAuthBearer, opts.SaslMechanism);
        Assert.IsNotNull(opts.OAuthBearerTokenProvider, "OAuthBearerTokenProvider must be set");
    }

    // ── Test 2: missing region throws ─────────────────────────────────────────

    [TestMethod]
    public void Add_without_region_or_env_throws()
    {
        var saved1 = Environment.GetEnvironmentVariable("AWS_REGION");
        var saved2 = Environment.GetEnvironmentVariable("AWS_DEFAULT_REGION");
        try
        {
            Environment.SetEnvironmentVariable("AWS_REGION", null);
            Environment.SetEnvironmentVariable("AWS_DEFAULT_REGION", null);

            // Region resolution throws before builder is accessed, so null! is safe.
            ISiloBuilder builder = null!;
            Assert.ThrowsExactly<InvalidOperationException>(
                () => builder.AddKafkaStreamingWithMskIam("test", EmptyConfig));
        }
        finally
        {
            Environment.SetEnvironmentVariable("AWS_REGION", saved1);
            Environment.SetEnvironmentVariable("AWS_DEFAULT_REGION", saved2);
        }
    }

    // ── Test 3: env var fallback ───────────────────────────────────────────────

    [TestMethod]
    public void Add_falls_back_to_AWS_REGION_env()
    {
        var saved = Environment.GetEnvironmentVariable("AWS_REGION");
        try
        {
            Environment.SetEnvironmentVariable("AWS_REGION", "eu-west-1");

            // Should not throw — region resolved from env var.
            var builder = new FakeSiloBuilder();
            builder.AddKafkaStreamingWithMskIam("test", EmptyConfig);

            var sp = builder.Services
                .AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance)
                .BuildServiceProvider();

            var opts = sp.GetRequiredService<IOptionsMonitor<KafkaStreamingOptions>>().Get("test");
            Assert.AreEqual(KafkaSecurityProtocol.SaslSsl, opts.SecurityProtocol);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AWS_REGION", saved);
        }
    }

    // ── Test 4: invalid region throws at registration time ─────────────────────

    [TestMethod]
    public void Add_with_invalid_region_throws_at_registration()
    {
        // Amazon.RegionEndpoint.GetBySystemName("") throws ArgumentException.
        // The throw must happen synchronously inside AddKafkaStreamingWithMskIam,
        // NOT deferred to the first broker connect.
        var builder = new FakeSiloBuilder();
        Assert.ThrowsExactly<ArgumentException>(
            () => builder.AddKafkaStreamingWithMskIam("test", EmptyConfig, ""));
    }
}

/// <summary>
/// Minimal <see cref="ISiloBuilder"/> stand-in that captures <c>ConfigureServices</c> calls
/// into a real <see cref="ServiceCollection"/>. Extension methods on <see cref="ISiloBuilder"/>
/// (AddPersistentStreams, AddKafkaStreams, etc.) call through to <see cref="ConfigureServices"/>,
/// so option registrations accumulate correctly without a real Orleans host.
/// </summary>
internal sealed class FakeSiloBuilder : ISiloBuilder
{
    public IServiceCollection Services { get; } = new ServiceCollection();

    public IConfiguration Configuration { get; } =
        new ConfigurationBuilder().Build();

    public ISiloBuilder ConfigureServices(Action<IServiceCollection> configureDelegate)
    {
        configureDelegate(Services);
        return this;
    }
}
