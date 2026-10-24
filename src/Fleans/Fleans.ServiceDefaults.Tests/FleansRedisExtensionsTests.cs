using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Fleans.ServiceDefaults.Tests;

/// <summary>
/// Covers <see cref="FleansRedisExtensions"/>: a host registers the <c>orleans-redis</c> client — and
/// its health check, which gates <c>/health</c> — only when a Redis-backed provider is configured (#911).
/// </summary>
[TestClass]
public class FleansRedisExtensionsTests
{
    private static readonly Dictionary<string, string?> NoRedis = new()
    {
        ["Fleans:Clustering:Provider"] = "Postgres",
        ["Fleans:Reminders:Provider"] = "Postgres",
        ["Fleans:Streaming:Provider"] = "Kafka",
    };

    [TestMethod]
    [DataRow(FleansHostKind.Silo)]
    [DataRow(FleansHostKind.Client)]
    public void Defaults_need_Redis(FleansHostKind host)
    {
        // Clustering, reminders and streams all default to Redis.
        Assert.IsTrue(FleansRedisExtensions.IsRedisRequired(Config(new()), host));
    }

    [TestMethod]
    [DataRow(FleansHostKind.Silo)]
    [DataRow(FleansHostKind.Client)]
    public void No_Redis_backed_provider_needs_no_Redis(FleansHostKind host)
    {
        Assert.IsFalse(FleansRedisExtensions.IsRedisRequired(Config(NoRedis), host));
    }

    [TestMethod]
    [DataRow("Fleans:Clustering:Provider", true)]
    [DataRow("Fleans:Reminders:Provider", false)]
    [DataRow("Fleans:Streaming:Provider", false)]
    public void One_Redis_provider_needs_Redis_on_a_silo_and_only_clustering_counts_on_a_client(
        string key, bool clientNeedsRedis)
    {
        var values = new Dictionary<string, string?>(NoRedis) { [key] = "redis" };

        Assert.IsTrue(FleansRedisExtensions.IsRedisRequired(Config(values), FleansHostKind.Silo));
        Assert.AreEqual(clientNeedsRedis, FleansRedisExtensions.IsRedisRequired(Config(values), FleansHostKind.Client));
    }

    [TestMethod]
    [DataRow("Fleans:Reminders:Provider")]
    [DataRow("Fleans:Streaming:Provider")]
    public void An_unset_silo_provider_defaults_to_Redis(string key)
    {
        var values = new Dictionary<string, string?>(NoRedis);
        values.Remove(key);

        Assert.IsTrue(FleansRedisExtensions.IsRedisRequired(Config(values), FleansHostKind.Silo));
    }

    [TestMethod]
    [DataRow("Memory")]
    [DataRow("AzureQueue")]
    public void Other_stream_providers_need_no_Redis(string streaming)
    {
        var values = new Dictionary<string, string?>(NoRedis) { ["Fleans:Streaming:Provider"] = streaming };

        Assert.IsFalse(FleansRedisExtensions.IsRedisRequired(Config(values), FleansHostKind.Silo));
    }

    [TestMethod]
    public void Aspire_Orleans_clustering_section_decides_the_clustering_side()
    {
        // The Aspire AppHost injects Orleans:Clustering (WithClustering(redis)) and Orleans resolves
        // the keyed orleans-redis multiplexer from its ServiceKey — Fleans:Clustering:Provider is unused.
        var aspire = new Dictionary<string, string?>(NoRedis)
        {
            ["Orleans:Clustering:ProviderType"] = "Redis",
            ["Orleans:Clustering:ServiceKey"] = "orleans-redis",
        };
        var other = new Dictionary<string, string?>
        {
            ["Fleans:Clustering:Provider"] = "Redis",
            ["Orleans:Clustering:ProviderType"] = "AzureTableStorage",
        };

        Assert.IsTrue(FleansRedisExtensions.IsRedisRequired(Config(aspire), FleansHostKind.Client));
        Assert.IsFalse(FleansRedisExtensions.IsRedisRequired(Config(other), FleansHostKind.Client));
    }

    [TestMethod]
    public void Unknown_provider_does_not_throw_here()
    {
        // The provider's own resolver fails startup with the specific message.
        var values = new Dictionary<string, string?>(NoRedis) { ["Fleans:Streaming:Provider"] = "Pulsar" };

        Assert.IsFalse(FleansRedisExtensions.IsRedisRequired(Config(values), FleansHostKind.Silo));
    }

    [TestMethod]
    public void AddFleansRedisClient_registers_the_keyed_client_and_its_health_check_when_needed()
    {
        var builder = Builder(new() { ["ConnectionStrings:orleans-redis"] = "localhost:6379" });

        var registered = builder.AddFleansRedisClient(FleansHostKind.Silo);

        Assert.IsTrue(registered);
        Assert.IsTrue(HasKeyedMultiplexer(builder.Services));
        CollectionAssert.Contains(HealthCheckNames(builder), "StackExchange.Redis_orleans-redis");
    }

    [TestMethod]
    public void AddFleansRedisClient_registers_nothing_without_a_Redis_provider()
    {
        var builder = Builder(NoRedis);

        var registered = builder.AddFleansRedisClient(FleansHostKind.Silo);

        Assert.IsFalse(registered);
        Assert.IsFalse(HasKeyedMultiplexer(builder.Services));
        Assert.IsFalse(HealthCheckNames(builder).Any(n => n.Contains("Redis", StringComparison.OrdinalIgnoreCase)));
    }

    private static IConfiguration Config(Dictionary<string, string?> values)
        => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static HostApplicationBuilder Builder(Dictionary<string, string?> values)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(values);
        builder.Services.AddHealthChecks();
        return builder;
    }

    private static bool HasKeyedMultiplexer(IServiceCollection services)
        => services.Any(d => d.ServiceType == typeof(IConnectionMultiplexer)
                             && Equals(d.ServiceKey, FleansRedisExtensions.ConnectionName));

    private static List<string> HealthCheckNames(HostApplicationBuilder builder)
    {
        using var provider = builder.Services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations
            .Select(r => r.Name)
            .ToList();
    }
}
