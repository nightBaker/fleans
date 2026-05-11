using Fleans.Domain.States;
using Fleans.Persistence;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;

namespace Fleans.Persistence.PostgreSql;

/// <summary>
/// PostgreSQL implementation of <see cref="IUserTaskFilterStrategy"/>. Pushes the
/// assignee/candidateGroup filter to SQL via <c>FromSqlInterpolated</c>.
///
/// EF Core's value converter on <c>CandidateUsers</c>/<c>CandidateGroups</c> (stored as
/// JSON-serialized text) makes it impossible to use <c>EF.Functions.Like</c> against those
/// columns via LINQ — EF applies the converter to the LIKE pattern constant and throws
/// <c>InvalidCastException</c>. Raw SQL bypasses the converter and correctly matches the
/// underlying <c>text</c> column. (See §6.1 in #415 design plan.)
/// </summary>
public sealed class PostgresUserTaskFilterStrategy : IUserTaskFilterStrategy
{
    public bool PushesToSql => true;

    // Path C: use FromSqlInterpolated to push the JSON-text LIKE filter to PostgreSQL.
    // Sieve sort + additional LINQ WHERE clauses compose correctly on top of FromSqlInterpolated.
    public IQueryable<UserTaskState> GetFilteredBase(
        FleanQueryDbContext db, string? assignee, string? candidateGroup)
    {
        if (assignee is not null && candidateGroup is not null)
        {
            var ap = BuildJsonValueLikePattern(assignee);
            var gp = BuildJsonValueLikePattern(candidateGroup);
            return db.UserTasks.FromSqlInterpolated(
                $"""SELECT * FROM "UserTasks" WHERE ("Assignee" = {assignee} OR "CandidateUsers" LIKE {ap} ESCAPE '\') AND "CandidateGroups" LIKE {gp} ESCAPE '\'""");
        }
        if (assignee is not null)
        {
            var ap = BuildJsonValueLikePattern(assignee);
            return db.UserTasks.FromSqlInterpolated(
                $"""SELECT * FROM "UserTasks" WHERE "Assignee" = {assignee} OR "CandidateUsers" LIKE {ap} ESCAPE '\'""");
        }
        if (candidateGroup is not null)
        {
            var gp = BuildJsonValueLikePattern(candidateGroup);
            return db.UserTasks.FromSqlInterpolated(
                $"""SELECT * FROM "UserTasks" WHERE "CandidateGroups" LIKE {gp} ESCAPE '\'""");
        }
        return db.UserTasks.AsQueryable();
    }

    /// <summary>
    /// Builds a LIKE pattern that matches a single JSON-array element exactly.
    /// <see cref="JsonConvert.ToString(string)"/> produces the JSON-quoted form (e.g.
    /// <c>"alice"</c>); LIKE-escape then prefixes backslash to <c>\</c>, <c>%</c>, <c>_</c>.
    /// The escape character is <c>\</c> (passed as the ESCAPE clause in raw SQL).
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
