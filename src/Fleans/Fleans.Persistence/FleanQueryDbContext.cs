using Fleans.Domain;
using Fleans.Domain.States;
using Microsoft.EntityFrameworkCore;

namespace Fleans.Persistence;

public class FleanQueryDbContext : DbContext
{
    public DbSet<ActivityInstanceState> ActivityInstances => Set<ActivityInstanceState>();
    public DbSet<WorkflowInstanceState> WorkflowInstances => Set<WorkflowInstanceState>();
    public DbSet<ProcessDefinition> ProcessDefinitions => Set<ProcessDefinition>();

    public FleanQueryDbContext(DbContextOptions<FleanQueryDbContext> options) : base(options)
    {
        ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        FleanModelConfiguration.Configure(modelBuilder);
    }
}
