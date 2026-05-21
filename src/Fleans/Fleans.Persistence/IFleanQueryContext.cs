using Fleans.Domain;
using Fleans.Domain.States;
using Fleans.Persistence.Events;

namespace Fleans.Persistence;

/// <summary>
/// Read-side view of the persistence model for the CQRS query path.
/// Exposes <see cref="IQueryable{T}"/> only — no mutation surface — so the
/// type system catches accidental writes through the query side at compile time.
/// Implemented by <see cref="FleanQueryDbContext"/>.
///
/// See <c>docs/plans/2026-02-15-cqrs-query-service-design.md</c> for the CQRS
/// contract and #661 for the originating concern. Two consumers are deliberately
/// exempt and stay on the concrete <see cref="FleanQueryDbContext"/>:
/// <see cref="WorkflowQueryService"/> (the body delegates to <c>IUserTaskFilterStrategy</c>
/// which keeps <see cref="FleanQueryDbContext"/>) and <c>PostgresUserTaskFilterStrategy</c>
/// (uses <c>FromSqlInterpolated</c>, an EF Core extension on <c>DbSet&lt;TEntity&gt;</c>).
/// </summary>
public interface IFleanQueryContext : IAsyncDisposable
{
    IQueryable<WorkflowInstanceState> WorkflowInstances { get; }
    IQueryable<ProcessDefinition> ProcessDefinitions { get; }
    IQueryable<UserTaskState> UserTasks { get; }
    IQueryable<WorkflowEventEntity> WorkflowEvents { get; }
    IQueryable<WorkflowSnapshotEntity> WorkflowSnapshots { get; }
    IQueryable<MessageStartEventRegistration> MessageStartEventRegistrations { get; }
    IQueryable<SignalStartEventRegistration> SignalStartEventRegistrations { get; }
    IQueryable<ConditionalStartEventListenerState> ConditionalStartEventListeners { get; }
    IQueryable<MessageSubscription> MessageSubscriptions { get; }
    IQueryable<SignalSubscription> SignalSubscriptions { get; }
}
