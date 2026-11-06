using Fleans.Domain.States;
using Microsoft.EntityFrameworkCore;

namespace Fleans.Persistence;

/// <summary>
/// How the retention purge removes an instance's details — the activity entries, variable scopes
/// and condition results the projector writes once, on completion (#980). Each of those rows
/// carries its instance's <see cref="InstanceCompletedAt"/>, so a provider can keep them in
/// completion-month partitions and drop a month whole instead of deleting it row by row.
///
/// Selecting between strategies is a DI-time concern: the default deletes the rows,
/// <c>AddPostgresPersistence</c> registers the partitioned one.
/// </summary>
public interface IReadModelRetention
{
    /// <summary>
    /// Shadow column on <see cref="ActivityInstanceEntry"/>, <see cref="WorkflowVariablesState"/> and
    /// <see cref="ConditionSequenceState"/>: the <c>CompletedAt</c> of the instance the row belongs
    /// to, set by the projector when it writes the row. PostgreSQL partitions the three tables on it.
    /// </summary>
    const string InstanceCompletedAt = "InstanceCompletedAt";

    /// <summary>
    /// Deletes what needs deleting of the details of the purged instances <paramref name="ids"/>,
    /// in the batch transaction that deletes their instance rows.
    /// </summary>
    Task DeleteDetailsAsync(FleanCommandDbContext db, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);

    /// <summary>
    /// Reclaims, after a purge's batches, the details that <see cref="DeleteDetailsAsync"/> left in
    /// place: those of instances that finished before <paramref name="completedBefore"/> and are all
    /// purged. Returns the number of months dropped.
    /// </summary>
    Task<int> ReclaimAsync(FleanCommandDbContext db, DateTimeOffset completedBefore, CancellationToken cancellationToken);
}

/// <summary>The default: details are deleted with their instance rows; nothing is left to reclaim.</summary>
public sealed class RowByRowReadModelRetention : IReadModelRetention
{
    public async Task DeleteDetailsAsync(FleanCommandDbContext db, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        await db.WorkflowActivityInstanceEntries.Where(e => ids.Contains(e.WorkflowInstanceId)).ExecuteDeleteAsync(cancellationToken);
        await db.WorkflowVariableStates.Where(v => ids.Contains(v.WorkflowInstanceId)).ExecuteDeleteAsync(cancellationToken);
        await db.WorkflowConditionSequenceStates.Where(c => ids.Contains(c.WorkflowInstanceId)).ExecuteDeleteAsync(cancellationToken);
    }

    public Task<int> ReclaimAsync(FleanCommandDbContext db, DateTimeOffset completedBefore, CancellationToken cancellationToken) =>
        Task.FromResult(0);
}
