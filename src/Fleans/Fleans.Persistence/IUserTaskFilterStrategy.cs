using Fleans.Domain.States;

namespace Fleans.Persistence;

/// <summary>
/// Provider-specific strategy for applying assignee/candidateGroup filters to a user-task
/// query. SQLite cannot push the JSON-array filter to SQL (CandidateUsers/CandidateGroups
/// are stored as JSON-serialized strings), so the default implementation is a no-op and
/// callers fall back to in-memory filtering. PostgreSQL implementations push the filter
/// to SQL via JSON-encoded LIKE so the silo materializes only matching rows.
///
/// Selecting between strategies is a DI-time concern: each Add&lt;Provider&gt;Persistence
/// extension registers the appropriate implementation. See #415.
/// </summary>
public interface IUserTaskFilterStrategy
{
    /// <summary>
    /// True if <see cref="Apply"/> pushes the filter to SQL — the caller can rely on
    /// the returned query to skip in-memory <c>ApplyUserTaskFilters</c> AND push
    /// pagination + count to SQL. False means the strategy is a no-op pass; the caller
    /// must materialize, then filter in memory.
    /// </summary>
    bool PushesToSql { get; }

    /// <summary>
    /// Returns a query with the assignee/candidateGroup filter applied (or unchanged
    /// when <see cref="PushesToSql"/> is false). Null filters are no-ops regardless of
    /// provider.
    /// </summary>
    IQueryable<UserTaskState> Apply(IQueryable<UserTaskState> query, string? assignee, string? candidateGroup);
}

/// <summary>
/// Default no-op strategy. Returns the query unchanged; the caller's existing
/// in-memory <c>ApplyUserTaskFilters</c> path continues to apply the filter.
/// Used by SQLite (no SQL pushdown for the JSON columns) and by host configurations
/// that have not registered a provider-specific strategy.
/// </summary>
public sealed class InMemoryUserTaskFilterStrategy : IUserTaskFilterStrategy
{
    public bool PushesToSql => false;

    public IQueryable<UserTaskState> Apply(IQueryable<UserTaskState> query, string? assignee, string? candidateGroup) => query;
}
