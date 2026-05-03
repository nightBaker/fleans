using Fleans.Domain;
using Fleans.Domain.States;
using Fleans.Persistence.Events;
using Microsoft.EntityFrameworkCore;

namespace Fleans.Persistence;

public class FleanQueryDbContext : DbContext
{
    public DbSet<WorkflowInstanceState> WorkflowInstances => Set<WorkflowInstanceState>();
    public DbSet<ProcessDefinition> ProcessDefinitions => Set<ProcessDefinition>();
    public DbSet<UserTaskState> UserTasks => Set<UserTaskState>();
    public DbSet<WorkflowEventEntity> WorkflowEvents => Set<WorkflowEventEntity>();
    public DbSet<WorkflowSnapshotEntity> WorkflowSnapshots => Set<WorkflowSnapshotEntity>();

    // Read-side projections of event-registration / subscription tables.
    // The /events admin page reads from these via WorkflowQueryService —
    // CQRS query path must not reach into the command context.
    public DbSet<MessageStartEventRegistration> MessageStartEventRegistrations => Set<MessageStartEventRegistration>();
    public DbSet<SignalStartEventRegistration> SignalStartEventRegistrations => Set<SignalStartEventRegistration>();
    public DbSet<ConditionalStartEventListenerState> ConditionalStartEventListeners => Set<ConditionalStartEventListenerState>();
    public DbSet<MessageSubscription> MessageSubscriptions => Set<MessageSubscription>();
    public DbSet<SignalSubscription> SignalSubscriptions => Set<SignalSubscription>();

    public FleanQueryDbContext(DbContextOptions<FleanQueryDbContext> options) : base(options)
    {
        ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        FleanModelConfiguration.Configure(modelBuilder);
    }
}
