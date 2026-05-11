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
    /// Returns the base <see cref="IQueryable{UserTaskState}"/> for the paged overload.
    /// PostgreSQL implementations use <c>FromSqlInterpolated</c> to inject a JSON-text
    /// LIKE filter directly into SQL. SQLite returns <c>db.UserTasks.AsQueryable()</c>
    /// unchanged and the caller falls back to in-memory filtering after materialisation.
    /// </summary>
    IQueryable<UserTaskState> GetFilteredBase(FleanQueryDbContext db, string? assignee, string? candidateGroup);
}

/// <summary>
/// Default no-op strategy for SQLite. Returns the plain <c>db.UserTasks</c> set;
/// the caller's in-memory <c>ApplyUserTaskFilters</c> path still applies the filter.
/// </summary>
public sealed class InMemoryUserTaskFilterStrategy : IUserTaskFilterStrategy
{
    public bool PushesToSql => false;

    public IQueryable<UserTaskState> GetFilteredBase(FleanQueryDbContext db, string? assignee, string? candidateGroup)
        => db.UserTasks.AsQueryable();
}
