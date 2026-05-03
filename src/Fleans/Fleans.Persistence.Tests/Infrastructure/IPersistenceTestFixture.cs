using Microsoft.EntityFrameworkCore;

namespace Fleans.Persistence.Tests.Infrastructure;

/// <summary>
/// Per-row disposable handle returned by <see cref="TestFixtureFactory"/>. Exposes the
/// command and query <see cref="IDbContextFactory{TContext}"/> the test class needs to
/// construct EF-backed grain storage and repository components.
/// </summary>
public interface IPersistenceTestFixture : IAsyncDisposable
{
    PersistenceProvider Provider { get; }
    IDbContextFactory<FleanCommandDbContext> CommandFactory { get; }
    IDbContextFactory<FleanQueryDbContext> QueryFactory { get; }
}
