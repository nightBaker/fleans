using Fleans.Domain.States;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;

namespace Fleans.Persistence.PostgreSql;

/// <summary>
/// PostgreSQL implementation of <see cref="IUserTaskFilterStrategy"/>. Pushes the
/// assignee/candidateGroup filter to SQL via JSON-encoded LIKE patterns matched
/// against the underlying text column. The CandidateUsers/CandidateGroups properties
/// are persisted as JSON-serialized strings (see <c>FleanModelConfiguration</c>) — so
/// a stored value of <c>["alice","bob"]</c> contains the literal substring <c>"alice"</c>
/// (with surrounding double-quotes), and a value of <c>["alice2"]</c> does not.
/// The JSON-quote bracketing produced by <see cref="JsonConvert.ToString(string)"/>
/// gives us the exact-element match without resorting to <c>jsonb</c>.
/// </summary>
public sealed class PostgresUserTaskFilterStrategy : IUserTaskFilterStrategy
{
    public bool PushesToSql => true;

    public IQueryable<UserTaskState> Apply(IQueryable<UserTaskState> query, string? assignee, string? candidateGroup)
    {
        if (assignee is not null)
        {
            var pattern = BuildJsonValueLikePattern(assignee);
            // OR with the scalar Assignee column to preserve existing in-memory semantics.
            query = query.Where(t =>
                t.Assignee == assignee ||
                EF.Functions.Like(EF.Property<string>(t, "CandidateUsers"), pattern, "\\"));
        }

        if (candidateGroup is not null)
        {
            var pattern = BuildJsonValueLikePattern(candidateGroup);
            query = query.Where(t =>
                EF.Functions.Like(EF.Property<string>(t, "CandidateGroups"), pattern, "\\"));
        }

        return query;
    }

    /// <summary>
    /// Builds a LIKE pattern that matches a single JSON-array element exactly.
    /// JsonConvert.ToString produces the JSON-quoted form (e.g. <c>"alice"</c>);
    /// LIKE-escape then prefixes a backslash to <c>\</c>, <c>%</c>, and <c>_</c>.
    /// The escape character is <c>\</c> (passed in <see cref="EF.Functions.Like"/>'s
    /// third argument).
    /// </summary>
    internal static string BuildJsonValueLikePattern(string value)
    {
        var jsonEncoded = JsonConvert.ToString(value);
        var escaped = jsonEncoded
            .Replace("\\", "\\\\")
            .Replace("%", "\\%")
            .Replace("_", "\\_");
        return "%" + escaped + "%";
    }
}
