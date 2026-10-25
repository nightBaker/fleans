using Testcontainers.PostgreSql;

namespace Fleans.Persistence.Tests.Infrastructure;

/// <summary>
/// Owns the assembly's <see cref="PostgreSqlContainer"/>s: plain PostgreSQL, and a single-node
/// Citus for the distributed-journal contract run (#879). Each starts lazily on first request —
/// Docker-less developers running only SQLite rows pay zero startup cost. Disposed via
/// <see cref="AssemblyInit"/>'s <c>[AssemblyCleanup]</c>.
/// </summary>
internal static class PostgresContainerFixture
{
    /// <summary>
    /// Image pinned for reproducible tests. Bump when the production deployment target
    /// (Aspire <c>Aspire.Hosting.PostgreSQL</c> default) moves; see
    /// <c>docs/conventions/persistence.md § Test parity</c>.
    /// </summary>
    public const string PostgresImage = "postgres:16-alpine";

    /// <summary>Citus on the same PostgreSQL major as <see cref="PostgresImage"/>.</summary>
    public const string CitusImage = "citusdata/citus:12.1";

    private const string BootstrapDatabaseName = "fleans_bootstrap";

    private static readonly SemaphoreSlim s_lock = new(1, 1);
    private static readonly Dictionary<string, PostgreSqlContainer> s_containers = new();

    /// <summary>
    /// True when <c>FLEANS_PG_TESTS=1</c> — gates container startup and PG row execution.
    /// </summary>
    public static bool IsEnabled =>
        string.Equals(
            Environment.GetEnvironmentVariable("FLEANS_PG_TESTS"),
            "1",
            StringComparison.Ordinal);

    /// <summary>
    /// Connection string pointed at the PostgreSQL container's bootstrap database. Use this only
    /// for administrative operations (CREATE/DROP DATABASE); per-test work uses
    /// <see cref="BuildConnectionString(string)"/>.
    /// </summary>
    public static string ContainerConnectionString => EnsureStarted(PostgresImage).GetConnectionString();

    /// <summary>
    /// Builds a connection string targeting the named per-class database on the running
    /// PostgreSQL container.
    /// </summary>
    public static string BuildConnectionString(string databaseName) =>
        BuildConnectionString(PostgresImage, databaseName);

    /// <summary>
    /// The bootstrap connection string of <paramref name="image"/>'s container (started on demand).
    /// </summary>
    public static string AdminConnectionString(string image) => EnsureStarted(image).GetConnectionString();

    /// <summary>
    /// The container's address on the Docker bridge network, for another container (e.g. a
    /// PgBouncer in front of it) to connect to.
    /// </summary>
    public static string BridgeAddress(string image) => EnsureStarted(image).IpAddress;

    /// <summary>
    /// Builds a connection string targeting the named database on <paramref name="image"/>'s container.
    /// </summary>
    public static string BuildConnectionString(string image, string databaseName)
    {
        var builder = new Npgsql.NpgsqlConnectionStringBuilder(EnsureStarted(image).GetConnectionString())
        {
            Database = databaseName,
        };
        return builder.ToString();
    }

    /// <summary>
    /// Stops the containers that were started. Called by <see cref="AssemblyInit"/>'s
    /// <c>[AssemblyCleanup]</c>.
    /// </summary>
    public static async Task DisposeAsync()
    {
        List<PostgreSqlContainer> containers;
        await s_lock.WaitAsync();
        try
        {
            containers = s_containers.Values.ToList();
            s_containers.Clear();
        }
        finally
        {
            s_lock.Release();
        }

        foreach (var container in containers)
        {
            await container.DisposeAsync();
        }
    }

    private static PostgreSqlContainer EnsureStarted(string image)
    {
        if (!IsEnabled)
        {
            throw new InvalidOperationException(
                "PostgresContainerFixture accessed without FLEANS_PG_TESTS=1.");
        }

        s_lock.Wait();
        try
        {
            if (!s_containers.TryGetValue(image, out var container))
            {
                container = new PostgreSqlBuilder(image)
                    .WithDatabase(BootstrapDatabaseName)
                    .Build();
                container.StartAsync().GetAwaiter().GetResult();
                s_containers[image] = container;
            }
            return container;
        }
        finally
        {
            s_lock.Release();
        }
    }
}
