using Microsoft.EntityFrameworkCore;

namespace Fleans.Persistence;

/// <summary>
/// Singleton-safe factory for <see cref="IFleanQueryContext"/>. Wraps the EF Core
/// <c>IDbContextFactory&lt;FleanQueryDbContext&gt;</c> to expose the CQRS-restricted
/// read-side view. Use this from Singleton consumers (currently
/// <see cref="EfCoreProcessDefinitionRepository"/>).
/// </summary>
public interface IFleanQueryContextFactory
{
    Task<IFleanQueryContext> CreateAsync(CancellationToken cancellationToken = default);
}

public sealed class FleanQueryContextFactory : IFleanQueryContextFactory
{
    private readonly IDbContextFactory<FleanQueryDbContext> _inner;

    public FleanQueryContextFactory(IDbContextFactory<FleanQueryDbContext> inner)
        => _inner = inner;

    public async Task<IFleanQueryContext> CreateAsync(CancellationToken cancellationToken = default)
        => await _inner.CreateDbContextAsync(cancellationToken);
}
