using Testcontainers.PostgreSql;

namespace Fleans.Persistence.Tests.Infrastructure;

/// <summary>
/// Owns a single <see cref="PostgreSqlContainer"/> for the assembly. The container starts
/// lazily on first PG fixture request — Docker-less developers running only SQLite rows
/// pay zero startup cost. Disposed via <see cref="AssemblyInit"/>'s
/// <c>[AssemblyCleanup]</c>.
/// </summary>
internal static class PostgresContainerFixture
{
    /// <summary>
    /// Image pinned for reproducible tests. Bump when the production deployment target
    /// (Aspire <c>Aspire.Hosting.PostgreSQL</c> default) moves; see
    /// <c>docs/conventions/persistence.md § Test parity</c>.
    /// </summary>
    public const string PostgresImage = "postgres:16-alpine";

    private const string BootstrapDatabaseName = "fleans_bootstrap";

    private static readonly SemaphoreSlim s_lock = new(1, 1);
    private static PostgreSqlContainer? s_container;

    /// <summary>
    /// True when <c>FLEANS_PG_TESTS=1</c> — gates container startup and PG row execution.
    /// </summary>
    public static bool IsEnabled =>
        string.Equals(
            Environment.GetEnvironmentVariable("FLEANS_PG_TESTS"),
            "1",
            StringComparison.Ordinal);

    /// <summary>
    /// Connection string pointed at the container's bootstrap database. Use this only for
    /// administrative operations (CREATE/DROP DATABASE); per-test work uses
    /// <see cref="BuildConnectionString"/>.
    /// </summary>
    public static string ContainerConnectionString => EnsureStarted().GetConnectionString();

    /// <summary>
    /// Builds a connection string targeting the named per-class database on the running
    /// container.
    /// </summary>
    public static string BuildConnectionString(string databaseName)
    {
        var container = EnsureStarted();
        var template = container.GetConnectionString();
        var builder = new Npgsql.NpgsqlConnectionStringBuilder(template)
        {
            Database = databaseName,
        };
        return builder.ToString();
    }

    /// <summary>
    /// Stops the container if it was started. Called by <see cref="AssemblyInit"/>'s
    /// <c>[AssemblyCleanup]</c>.
    /// </summary>
    public static async Task DisposeAsync()
    {
        PostgreSqlContainer? container;
        await s_lock.WaitAsync();
        try
        {
            container = s_container;
            s_container = null;
        }
        finally
        {
            s_lock.Release();
        }

        if (container is not null)
        {
            await container.DisposeAsync();
        }
    }

    private static PostgreSqlContainer EnsureStarted()
    {
        if (!IsEnabled)
        {
            throw new InvalidOperationException(
                "PostgresContainerFixture accessed without FLEANS_PG_TESTS=1.");
        }

        if (s_container is { } existing)
        {
            return existing;
        }

        s_lock.Wait();
        try
        {
            if (s_container is null)
            {
                var container = new PostgreSqlBuilder()
                    .WithImage(PostgresImage)
                    .WithDatabase(BootstrapDatabaseName)
                    .Build();
                container.StartAsync().GetAwaiter().GetResult();
                s_container = container;
            }
            return s_container;
        }
        finally
        {
            s_lock.Release();
        }
    }
}
