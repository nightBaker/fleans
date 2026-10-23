using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting; // UseRedisClustering lives here
using Orleans.Hosting;

namespace Fleans.ServiceDefaults.Clustering;

/// <summary>
/// Orleans membership wiring shared by every Fleans silo and client (#885). The provider is
/// selected by <c>Fleans:Clustering:Provider</c> (<c>Redis</c> default, <c>Postgres</c>); silos and
/// clients of one cluster must use the same provider, because the membership table is also the
/// clients' gateway list. See <c>docs/conventions/clustering.md</c>.
/// </summary>
public static class FleansClusteringExtensions
{
    /// <summary>
    /// Registers the membership table on a silo, plus Orleans Kubernetes hosting when
    /// <c>Fleans:Kubernetes:Enabled=true</c>. When the host already configures clustering through
    /// Orleans' own <c>Orleans:Clustering</c> section (the Aspire AppHost does), the membership table
    /// is left to Orleans. Fails fast on an unknown provider or a missing connection string.
    /// </summary>
    public static ISiloBuilder AddFleansClustering(this ISiloBuilder siloBuilder, IConfiguration configuration)
    {
        var resolved = ResolveClusteringConfiguration(configuration);

        if (!resolved.ConfiguredByOrleans)
        {
            switch (resolved.Provider)
            {
                case ClusteringProvider.Redis:
                    siloBuilder.UseRedisClustering(resolved.ConnectionString);
                    break;
                case ClusteringProvider.Postgres:
                    siloBuilder.UseAdoNetClustering(options =>
                    {
                        options.Invariant = "Npgsql";
                        options.ConnectionString = resolved.ConnectionString;
                    });
                    break;
            }
        }

        if (IsKubernetesHostingEnabled(configuration))
            siloBuilder.UseKubernetesHosting();

        return siloBuilder;
    }

    /// <summary>
    /// Registers the gateway list provider on an Orleans client (Fleans.Web, Fleans.Mcp). Must match
    /// the silos' <c>Fleans:Clustering:Provider</c>.
    /// </summary>
    public static IClientBuilder AddFleansClustering(this IClientBuilder clientBuilder, IConfiguration configuration)
    {
        var resolved = ResolveClusteringConfiguration(configuration);
        if (resolved.ConfiguredByOrleans)
            return clientBuilder;

        switch (resolved.Provider)
        {
            case ClusteringProvider.Redis:
                clientBuilder.UseRedisClustering(resolved.ConnectionString);
                break;
            case ClusteringProvider.Postgres:
                clientBuilder.UseAdoNetClustering(options =>
                {
                    options.Invariant = "Npgsql";
                    options.ConnectionString = resolved.ConnectionString;
                });
                break;
        }
        return clientBuilder;
    }

    /// <summary>
    /// The silo name for an engine silo of the given role. Placement reads the role from the silo
    /// name prefix (<c>core-</c>, <c>worker-</c>, <c>combined-</c>), so without Kubernetes hosting it is
    /// <c>{role}-{machine}-{guid}</c>. With Kubernetes hosting Orleans matches silos to pods by name,
    /// so the silo name is the pod name — and the pod name must then start with <c>{role}-</c>
    /// (the Helm chart names its silo Deployments that way). Throws when it does not.
    /// </summary>
    public static string ResolveSiloName(IConfiguration configuration, string role)
    {
        var prefix = role.ToLowerInvariant() + "-";
        if (!IsKubernetesHostingEnabled(configuration))
            return $"{prefix}{Environment.MachineName}-{Guid.NewGuid():N}".ToLowerInvariant();

        var podName = configuration[FleansClusteringOptions.PodNameKey];
        if (string.IsNullOrWhiteSpace(podName))
            podName = Environment.MachineName;

        if (!podName.StartsWith(prefix, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"{FleansClusteringOptions.KubernetesEnabledKey}=true makes the pod name the Orleans silo name, " +
                $"and Fleans placement reads the silo role from its prefix — pod '{podName}' must start with " +
                $"'{prefix}' for Fleans:Role={role}. Name the Deployment/StatefulSet '{prefix}…' " +
                "(see docs/conventions/clustering.md).");

        return podName;
    }

    public static bool IsKubernetesHostingEnabled(IConfiguration configuration)
        => configuration.GetValue<bool>(FleansClusteringOptions.KubernetesEnabledKey);

    /// <summary>
    /// Validates the clustering config and resolves the provider + connection string, or throws.
    /// Exposed for unit tests and for <c>EnsureDatabaseSchemaAsync</c>.
    /// </summary>
    public static ResolvedClusteringConfiguration ResolveClusteringConfiguration(IConfiguration configuration)
    {
        var providerRaw = configuration[FleansClusteringOptions.ProviderKey] ?? "Redis";
        var configuredByOrleans = configuration.GetSection(FleansClusteringOptions.OrleansClusteringSection).Exists();

        switch (providerRaw.ToLowerInvariant())
        {
            case "redis":
                {
                    if (configuredByOrleans)
                        return new ResolvedClusteringConfiguration(ClusteringProvider.Redis, "", ConfiguredByOrleans: true);

                    var connString = configuration.GetConnectionString(FleansClusteringOptions.RedisConnectionName);
                    if (string.IsNullOrWhiteSpace(connString))
                        throw new InvalidOperationException(
                            $"Connection string '{FleansClusteringOptions.RedisConnectionName}' is required for " +
                            $"{FleansClusteringOptions.ProviderKey}=Redis (the default). Set it, or switch the " +
                            "membership table to Postgres (see docs/conventions/clustering.md).");
                    return new ResolvedClusteringConfiguration(ClusteringProvider.Redis, connString);
                }
            case "postgres":
                {
                    if (configuredByOrleans)
                        throw new InvalidOperationException(
                            $"{FleansClusteringOptions.ProviderKey}=Postgres conflicts with the Orleans:Clustering " +
                            "section (set by the Aspire AppHost's WithClustering). Configure the membership table in " +
                            "one place only — the Aspire AppHost stays on Redis.");

                    // Same rule as Postgres reminders: the membership schema is created next to the
                    // application's migrations, so a SQLite install has nowhere to create it.
                    var persistenceProvider = configuration["Persistence:Provider"] ?? "Sqlite";
                    if (!persistenceProvider.Equals("Postgres", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException(
                            $"{FleansClusteringOptions.ProviderKey}=Postgres requires Persistence:Provider=Postgres " +
                            $"(got '{persistenceProvider}').");

                    var dedicated = configuration.GetConnectionString(FleansClusteringOptions.PostgresConnectionName);
                    if (!string.IsNullOrWhiteSpace(dedicated))
                        return new ResolvedClusteringConfiguration(ClusteringProvider.Postgres, dedicated, DedicatedDatabase: true);

                    var connString = configuration.GetConnectionString("fleans");
                    if (string.IsNullOrWhiteSpace(connString))
                        throw new InvalidOperationException(
                            $"Connection string '{FleansClusteringOptions.PostgresConnectionName}' or 'fleans' is required " +
                            $"when {FleansClusteringOptions.ProviderKey}=Postgres (see docs/conventions/clustering.md).");
                    return new ResolvedClusteringConfiguration(ClusteringProvider.Postgres, connString);
                }
            default:
                throw new ArgumentException(
                    $"Unknown clustering provider '{providerRaw}'. Supported: Redis (default), Postgres. " +
                    $"Set {FleansClusteringOptions.ProviderKey} in configuration.");
        }
    }
}

public enum ClusteringProvider
{
    Redis,
    Postgres,
}

/// <param name="DedicatedDatabase">
/// True when <c>Postgres</c> membership uses <c>ConnectionStrings:fleans-cluster</c> (its own database)
/// instead of the application's <c>fleans</c> connection.
/// </param>
/// <param name="ConfiguredByOrleans">
/// True when Orleans' <c>Orleans:Clustering</c> section (Aspire) already wires the membership table;
/// <see cref="ConnectionString"/> is then empty.
/// </param>
public readonly record struct ResolvedClusteringConfiguration(
    ClusteringProvider Provider,
    string ConnectionString,
    bool DedicatedDatabase = false,
    bool ConfiguredByOrleans = false);
