namespace Fleans.Persistence.Tests.Infrastructure;

/// <summary>
/// Builds a per-row <see cref="IPersistenceTestFixture"/> for the chosen provider.
/// Calls <see cref="Skip.IfPostgresUnavailable(PersistenceProvider)"/> implicitly so
/// that Postgres rows on a Docker-less developer loop surface as
/// <c>Inconclusive</c> rather than <c>Failed</c>.
/// </summary>
public static class TestFixtureFactory
{
    public static Task<IPersistenceTestFixture> CreateAsync(PersistenceProvider provider)
    {
        Skip.IfPostgresUnavailable(provider);
        return provider switch
        {
            PersistenceProvider.Sqlite => SqliteRowFixture.CreateAsync(),
            PersistenceProvider.Postgres => PostgresRowFixture.CreateAsync(),
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unknown provider"),
        };
    }
}
