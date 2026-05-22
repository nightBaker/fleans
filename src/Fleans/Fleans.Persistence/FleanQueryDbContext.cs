using Fleans.Domain;
using Fleans.Domain.States;
using Fleans.Persistence.Events;
using Microsoft.EntityFrameworkCore;

namespace Fleans.Persistence;

public class FleanQueryDbContext : DbContext, IFleanQueryContext
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

    // Explicit IFleanQueryContext implementation — exposes IQueryable<T> only so the
    // CQRS-restricted view is invisible from the concrete class but reachable via the
    // interface contract. DbContext already implements IAsyncDisposable.
    IQueryable<WorkflowInstanceState> IFleanQueryContext.WorkflowInstances => WorkflowInstances;
    IQueryable<ProcessDefinition> IFleanQueryContext.ProcessDefinitions => ProcessDefinitions;
    IQueryable<UserTaskState> IFleanQueryContext.UserTasks => UserTasks;
    IQueryable<WorkflowEventEntity> IFleanQueryContext.WorkflowEvents => WorkflowEvents;
    IQueryable<WorkflowSnapshotEntity> IFleanQueryContext.WorkflowSnapshots => WorkflowSnapshots;
    IQueryable<MessageStartEventRegistration> IFleanQueryContext.MessageStartEventRegistrations => MessageStartEventRegistrations;
    IQueryable<SignalStartEventRegistration> IFleanQueryContext.SignalStartEventRegistrations => SignalStartEventRegistrations;
    IQueryable<ConditionalStartEventListenerState> IFleanQueryContext.ConditionalStartEventListeners => ConditionalStartEventListeners;
    IQueryable<MessageSubscription> IFleanQueryContext.MessageSubscriptions => MessageSubscriptions;
    IQueryable<SignalSubscription> IFleanQueryContext.SignalSubscriptions => SignalSubscriptions;
}
