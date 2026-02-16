using Microsoft.EntityFrameworkCore;
using Orleans.Runtime;

namespace Fleans.Persistence.Tests;

internal class TestGrainState<T> : IGrainState<T>
{
    public T State { get; set; } = default!;
    public string? ETag { get; set; }
    public bool RecordExists { get; set; }
}

internal class TestDbContextFactory : IDbContextFactory<FleanCommandDbContext>
{
    private readonly DbContextOptions<FleanCommandDbContext> _options;

    public TestDbContextFactory(DbContextOptions<FleanCommandDbContext> options)
    {
        _options = options;
    }

    public FleanCommandDbContext CreateDbContext() => new(_options);

    public Task<FleanCommandDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(CreateDbContext());
}
