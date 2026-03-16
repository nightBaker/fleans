using Fleans.Domain;
using Fleans.Domain.States;
using Microsoft.EntityFrameworkCore;

namespace Fleans.Persistence;

public class FleanQueryDbContext : DbContext
{
    public DbSet<WorkflowInstanceState> WorkflowInstances => Set<WorkflowInstanceState>();
    public DbSet<ProcessDefinition> ProcessDefinitions => Set<ProcessDefinition>();
    public DbSet<UserTaskState> UserTasks => Set<UserTaskState>();

    public FleanQueryDbContext(DbContextOptions<FleanQueryDbContext> options) : base(options)
    {
        ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        FleanModelConfiguration.Configure(modelBuilder);
    }
}
