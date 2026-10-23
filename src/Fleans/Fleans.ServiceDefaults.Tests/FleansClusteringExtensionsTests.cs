using Fleans.ServiceDefaults.Clustering;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.Hosting;
using Orleans.Messaging;
using Orleans.Runtime;

namespace Fleans.ServiceDefaults.Tests;

/// <summary>
/// Covers the membership provider switch, its fail-fast surface, Kubernetes hosting opt-in and the
/// silo-name rule (#885).
/// </summary>
[TestClass]
public class FleansClusteringExtensionsTests
{
    private const string PgApp = "Host=app-db;Database=fleans;Username=u;Password=p";
    private const string PgCluster = "Host=cluster-db;Database=cluster;Username=u;Password=p";

    [TestMethod]
    public void Default_provider_is_Redis_on_the_orleans_redis_connection()
    {
        var resolved = FleansClusteringExtensions.ResolveClusteringConfiguration(Config(new()
        {
            ["ConnectionStrings:orleans-redis"] = "localhost:6379",
        }));

        Assert.AreEqual(ClusteringProvider.Redis, resolved.Provider);
        Assert.AreEqual("localhost:6379", resolved.ConnectionString);
        Assert.IsFalse(resolved.ConfiguredByOrleans);
    }

    [TestMethod]
    public void Redis_provider_throws_when_orleans_redis_connection_missing()
    {
        var ex = Assert.ThrowsExactly<InvalidOperationException>(
            () => FleansClusteringExtensions.ResolveClusteringConfiguration(Config(new())));

        StringAssert.Contains(ex.Message, "orleans-redis");
        StringAssert.Contains(ex.Message, "Fleans:Clustering:Provider=Redis");
    }

    [TestMethod]
    public void Aspire_Orleans_clustering_section_is_left_to_Orleans()
    {
        // The Aspire AppHost injects Orleans:Clustering (WithClustering(redis)); Fleans must not
        // register a second membership table on top of it.
        var resolved = FleansClusteringExtensions.ResolveClusteringConfiguration(Config(new()
        {
            ["Orleans:Clustering:ProviderType"] = "Redis",
            ["Orleans:Clustering:ServiceKey"] = "orleans-redis",
        }));

        Assert.AreEqual(ClusteringProvider.Redis, resolved.Provider);
        Assert.IsTrue(resolved.ConfiguredByOrleans);
    }

    [TestMethod]
    public void Postgres_provider_conflicts_with_Aspire_Orleans_clustering_section()
    {
        var ex = Assert.ThrowsExactly<InvalidOperationException>(
            () => FleansClusteringExtensions.ResolveClusteringConfiguration(Config(new()
            {
                ["Fleans:Clustering:Provider"] = "Postgres",
                ["Persistence:Provider"] = "Postgres",
                ["ConnectionStrings:fleans"] = PgApp,
                ["Orleans:Clustering:ProviderType"] = "Redis",
            })));

        StringAssert.Contains(ex.Message, "Orleans:Clustering");
    }

    [TestMethod]
    public void Postgres_provider_throws_when_persistence_is_Sqlite()
    {
        var ex = Assert.ThrowsExactly<InvalidOperationException>(
            () => FleansClusteringExtensions.ResolveClusteringConfiguration(Config(new()
            {
                ["Fleans:Clustering:Provider"] = "Postgres",
                ["Persistence:Provider"] = "Sqlite",
                ["ConnectionStrings:fleans"] = PgApp,
            })));

        StringAssert.Contains(ex.Message, "Persistence:Provider=Postgres");
    }

    [TestMethod]
    public void Postgres_provider_throws_when_no_connection_string()
    {
        var ex = Assert.ThrowsExactly<InvalidOperationException>(
            () => FleansClusteringExtensions.ResolveClusteringConfiguration(Config(new()
            {
                ["Fleans:Clustering:Provider"] = "Postgres",
                ["Persistence:Provider"] = "Postgres",
            })));

        StringAssert.Contains(ex.Message, "'fleans-cluster' or 'fleans'");
    }

    [TestMethod]
    public void Postgres_provider_shares_the_fleans_database_by_default()
    {
        var resolved = FleansClusteringExtensions.ResolveClusteringConfiguration(Config(new()
        {
            ["Fleans:Clustering:Provider"] = "postgres",
            ["Persistence:Provider"] = "Postgres",
            ["ConnectionStrings:fleans"] = PgApp,
        }));

        Assert.AreEqual(ClusteringProvider.Postgres, resolved.Provider);
        Assert.AreEqual(PgApp, resolved.ConnectionString);
        Assert.IsFalse(resolved.DedicatedDatabase);
    }

    [TestMethod]
    public void Postgres_provider_prefers_dedicated_fleans_cluster_connection()
    {
        var resolved = FleansClusteringExtensions.ResolveClusteringConfiguration(Config(new()
        {
            ["Fleans:Clustering:Provider"] = "Postgres",
            ["Persistence:Provider"] = "Postgres",
            ["ConnectionStrings:fleans"] = PgApp,
            ["ConnectionStrings:fleans-cluster"] = PgCluster,
        }));

        Assert.AreEqual(PgCluster, resolved.ConnectionString);
        Assert.IsTrue(resolved.DedicatedDatabase);
    }

    [TestMethod]
    public void Unknown_provider_throws_ArgumentException()
    {
        var ex = Assert.ThrowsExactly<ArgumentException>(
            () => FleansClusteringExtensions.ResolveClusteringConfiguration(Config(new()
            {
                ["Fleans:Clustering:Provider"] = "Consul",
            })));

        StringAssert.Contains(ex.Message, "Consul");
        StringAssert.Contains(ex.Message, "Supported: Redis (default), Postgres");
    }

    [TestMethod]
    public void Silo_with_Postgres_provider_registers_the_AdoNet_membership_table()
    {
        var services = SiloServices(new()
        {
            ["Fleans:Clustering:Provider"] = "Postgres",
            ["Persistence:Provider"] = "Postgres",
            ["ConnectionStrings:fleans"] = PgApp,
        });

        CollectionAssert.AreEqual(new[] { "AdoNetClusteringTable" }, MembershipTables(services));
        Assert.IsFalse(HasKubernetesAgent(services));
    }

    [TestMethod]
    public void Silo_with_Redis_provider_registers_the_Redis_membership_table()
    {
        var services = SiloServices(new()
        {
            ["ConnectionStrings:orleans-redis"] = "localhost:6379",
        });

        var tables = MembershipTables(services);
        Assert.HasCount(1, tables);
        StringAssert.Contains(tables[0], "Redis");
    }

    [TestMethod]
    public void Silo_under_Aspire_keeps_the_single_membership_table_Orleans_configured()
    {
        var services = SiloServices(new()
        {
            ["Orleans:Clustering:ProviderType"] = "Redis",
            ["Orleans:Clustering:ServiceKey"] = "orleans-redis",
            ["ConnectionStrings:orleans-redis"] = "localhost:6379",
        });

        Assert.HasCount(1, MembershipTables(services));
    }

    [TestMethod]
    public void Kubernetes_hosting_is_registered_only_when_enabled()
    {
        var services = SiloServices(new()
        {
            ["ConnectionStrings:orleans-redis"] = "localhost:6379",
            ["Fleans:Kubernetes:Enabled"] = "true",
        });

        Assert.IsTrue(HasKubernetesAgent(services));
    }

    [TestMethod]
    public void Client_with_Postgres_provider_reads_gateways_from_the_AdoNet_table()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Fleans:Clustering:Provider"] = "Postgres",
            ["Persistence:Provider"] = "Postgres",
            ["ConnectionStrings:fleans"] = PgApp,
        });
        builder.UseOrleansClient(client => client.AddFleansClustering(builder.Configuration));

        var providers = builder.Services
            .Where(d => d.ServiceType == typeof(IGatewayListProvider))
            .Select(d => d.ImplementationType?.Name)
            .ToArray();
        CollectionAssert.AreEqual(new[] { "AdoNetGatewayListProvider" }, providers);
    }

    [TestMethod]
    public void Silo_name_carries_the_role_without_Kubernetes_hosting()
    {
        var name = FleansClusteringExtensions.ResolveSiloName(Config(new()), "Core");

        Assert.IsTrue(name.StartsWith("core-", StringComparison.Ordinal), name);
    }

    [TestMethod]
    public void Silo_name_is_the_pod_name_with_Kubernetes_hosting()
    {
        var name = FleansClusteringExtensions.ResolveSiloName(Config(new()
        {
            ["Fleans:Kubernetes:Enabled"] = "true",
            ["POD_NAME"] = "worker-fleans-7d9f8c6b5-x2x7k",
        }), "worker");

        Assert.AreEqual("worker-fleans-7d9f8c6b5-x2x7k", name);
    }

    [TestMethod]
    public void Kubernetes_hosting_rejects_a_pod_name_without_the_role_prefix()
    {
        // Kubernetes hosting matches silos to pods by name, and placement reads the role from the
        // silo-name prefix — a pod called fleans-core-… would be placed as "no role".
        var ex = Assert.ThrowsExactly<InvalidOperationException>(
            () => FleansClusteringExtensions.ResolveSiloName(Config(new()
            {
                ["Fleans:Kubernetes:Enabled"] = "true",
                ["POD_NAME"] = "fleans-core-7d9f8c6b5-x2x7k",
            }), "Core"));

        StringAssert.Contains(ex.Message, "'core-'");
    }

    private static IConfiguration Config(Dictionary<string, string?> values)
        => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static IServiceCollection SiloServices(Dictionary<string, string?> values)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(values);
        builder.UseOrleans(silo => silo.AddFleansClustering(builder.Configuration));
        return builder.Services;
    }

    private static string?[] MembershipTables(IServiceCollection services)
        => services
            .Where(d => d.ServiceType == typeof(IMembershipTable))
            // The Redis provider registers through a factory; name it by the factory's declaring type.
            .Select(d => d.ImplementationType?.Name ?? d.ImplementationFactory?.Method.DeclaringType?.FullName)
            .ToArray();

    private static bool HasKubernetesAgent(IServiceCollection services)
        => services.Any(d => d.ServiceType == typeof(ILifecycleParticipant<ISiloLifecycle>)
                             && d.ImplementationType?.Name == "KubernetesClusterAgent");
}
