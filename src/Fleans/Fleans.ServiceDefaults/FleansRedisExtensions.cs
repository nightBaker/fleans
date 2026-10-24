using Fleans.ServiceDefaults.Clustering;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Fleans.ServiceDefaults;

/// <summary>What a Fleans host runs as: an Orleans silo, or an Orleans client with no grains.</summary>
public enum FleansHostKind
{
    /// <summary>An Orleans silo (Fleans.Api as Core/Worker/Combined, Fleans.WorkerHost).</summary>
    Silo,

    /// <summary>An Orleans client (Fleans.Web, Fleans.Mcp, Fleans.Api as Api).</summary>
    Client,
}

/// <summary>
/// The <c>orleans-redis</c> client registration shared by every Fleans host. Redis is optional:
/// the keyed multiplexer — and the Aspire health check that gates <c>/health</c> — is registered
/// only when a Redis-backed provider is configured. See <c>docs/conventions/clustering.md</c>.
/// </summary>
public static class FleansRedisExtensions
{
    /// <summary>The Redis connection name (shared by clustering, reminders and streams).</summary>
    public const string ConnectionName = "orleans-redis";

    /// <summary>
    /// Whether the host needs the <c>orleans-redis</c> connection. A client only reads the membership
    /// table, so only clustering counts; a silo also counts reminders and streams:
    /// <list type="bullet">
    /// <item>clustering — Orleans' <c>Orleans:Clustering:ProviderType</c> when that section exists
    /// (the Aspire AppHost injects it), else <c>Fleans:Clustering:Provider</c> (default <c>Redis</c>);</item>
    /// <item>reminders — <c>Fleans:Reminders:Provider</c> (default <c>Redis</c>);</item>
    /// <item>streams — <c>Fleans:Streaming:Provider</c> (default <c>Redis</c>).</item>
    /// </list>
    /// Never throws: an unknown provider value counts as "not Redis" and fails in its own resolver.
    /// </summary>
    public static bool IsRedisRequired(IConfiguration configuration, FleansHostKind host)
    {
        if (UsesRedisClustering(configuration))
            return true;
        if (host == FleansHostKind.Client)
            return false;

        return IsRedis(configuration["Fleans:Reminders:Provider"])
            || IsRedis(configuration["Fleans:Streaming:Provider"]);
    }

    /// <summary>
    /// Registers the keyed <c>orleans-redis</c> client (Aspire's <c>AddKeyedRedisClient</c>, with its
    /// health check) when <see cref="IsRedisRequired"/> says the host needs it. Returns whether it did.
    /// </summary>
    public static bool AddFleansRedisClient(this IHostApplicationBuilder builder, FleansHostKind host)
    {
        if (!IsRedisRequired(builder.Configuration, host))
            return false;

        builder.AddKeyedRedisClient(ConnectionName);
        return true;
    }

    private static bool UsesRedisClustering(IConfiguration configuration)
    {
        var orleansClustering = configuration.GetSection(FleansClusteringOptions.OrleansClusteringSection);
        return orleansClustering.Exists()
            ? string.Equals(orleansClustering["ProviderType"], "Redis", StringComparison.OrdinalIgnoreCase)
            : IsRedis(configuration[FleansClusteringOptions.ProviderKey]);
    }

    /// <summary>Unset means the provider's default, which is Redis for all three.</summary>
    private static bool IsRedis(string? provider)
        => provider is null || provider.Equals("Redis", StringComparison.OrdinalIgnoreCase);
}
