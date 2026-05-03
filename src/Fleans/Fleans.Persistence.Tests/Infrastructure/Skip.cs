namespace Fleans.Persistence.Tests.Infrastructure;

/// <summary>
/// Helper for skipping <see cref="PersistenceProvider.Postgres"/> rows when
/// <c>FLEANS_PG_TESTS</c> is not set to <c>1</c>. Calls
/// <see cref="Assert.Inconclusive(string)"/> — non-failing in MSTest, so the default
/// developer loop stays Docker-free.
/// </summary>
public static class Skip
{
    public static void IfPostgresUnavailable(PersistenceProvider provider)
    {
        if (provider == PersistenceProvider.Postgres && !PostgresContainerFixture.IsEnabled)
        {
            Assert.Inconclusive("FLEANS_PG_TESTS != 1 — skipping Postgres row.");
        }
    }
}
