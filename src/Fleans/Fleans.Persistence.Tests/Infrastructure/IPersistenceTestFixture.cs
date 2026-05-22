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

    /// <summary>
    /// CQRS-restricted query-context factory wrapping <see cref="QueryFactory"/>.
    /// Pass this when constructing components that depend on
    /// <see cref="IFleanQueryContextFactory"/> (e.g.
    /// <see cref="EfCoreProcessDefinitionRepository"/>). Default-interface-member
    /// avoids 15 inline <c>new FleanQueryContextFactory(...)</c> sites in the
    /// repository test class. See #661.
    /// </summary>
    IFleanQueryContextFactory QueryContextFactory => new FleanQueryContextFactory(QueryFactory);
}
