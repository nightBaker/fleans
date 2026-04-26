using Testcontainers.PostgreSql;

namespace Fleans.Persistence.Tests;

internal static class PostgresContainerFixture
{
    private static PostgreSqlContainer? _container;

    public static bool IsEnabled =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FLEANS_PG_TESTS"));

    public static string ConnectionString => _container?.GetConnectionString()
        ?? throw new InvalidOperationException("PostgreSQL container is not running.");

    internal static async Task StartAsync()
    {
        if (!IsEnabled) return;
        _container = new PostgreSqlBuilder()
            .WithImage("postgres:16-alpine")
            .Build();
        await _container.StartAsync();
    }

    internal static async Task StopAsync()
    {
        if (_container is not null)
            await _container.DisposeAsync();
    }
}
