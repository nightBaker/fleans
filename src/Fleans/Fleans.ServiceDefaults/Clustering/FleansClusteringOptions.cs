namespace Fleans.ServiceDefaults.Clustering;

/// <summary>
/// Configuration keys and connection names of the Orleans membership ("clustering") wiring
/// (#885). See <c>docs/conventions/clustering.md</c>.
/// </summary>
public static class FleansClusteringOptions
{
    /// <summary><c>Redis</c> (default) | <c>Postgres</c>, case-insensitive.</summary>
    public const string ProviderKey = "Fleans:Clustering:Provider";

    /// <summary>
    /// <c>true</c> = the silo calls Orleans' <c>UseKubernetesHosting()</c>: the pod name becomes the
    /// silo name and silos whose pod is gone are declared dead straight away. Default <c>false</c>.
    /// </summary>
    public const string KubernetesEnabledKey = "Fleans:Kubernetes:Enabled";

    /// <summary><c>Redis</c> provider: the clustering Redis (shared with Redis reminders and streams).</summary>
    public const string RedisConnectionName = "orleans-redis";

    /// <summary>
    /// <c>Postgres</c> provider: optional dedicated database for the membership table; falls back to
    /// the application's <c>fleans</c> connection when unset.
    /// </summary>
    public const string PostgresConnectionName = "fleans-cluster";

    /// <summary>
    /// The pod name Kubernetes hosting uses as the silo name. Set from the downward API
    /// (<c>metadata.name</c>); falls back to the host name, which Kubernetes sets to the pod name.
    /// </summary>
    public const string PodNameKey = "POD_NAME";

    /// <summary>
    /// Orleans' own configuration-driven clustering (<c>Orleans:Clustering:ProviderType</c>), which the
    /// Aspire AppHost injects via <c>WithClustering(redis)</c>. When present, Orleans has already wired
    /// the membership table and Fleans does not register a second one.
    /// </summary>
    public const string OrleansClusteringSection = "Orleans:Clustering";
}
