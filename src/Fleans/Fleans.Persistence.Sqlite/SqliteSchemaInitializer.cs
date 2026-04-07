using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Fleans.Persistence.Sqlite;

/// <summary>
/// SQLite-specific schema bootstrap helpers.
/// </summary>
public static class SqliteSchemaInitializer
{
    /// <summary>
    /// Calls <see cref="DatabaseFacade.EnsureCreated"/> and swallows
    /// <see cref="SqliteException"/>s thrown when another process (e.g. a sibling silo
    /// sharing the same .db file) has already created the tables.
    /// </summary>
    /// <remarks>
    /// This exists so host projects do not need a direct reference to
    /// <c>Microsoft.Data.Sqlite</c> just for the race-catch block.
    /// </remarks>
    public static void EnsureCreatedIgnoreRaces(DatabaseFacade database)
    {
        try
        {
            database.EnsureCreated();
        }
        catch (SqliteException)
        {
            // Tables already created by a concurrent process sharing the same SQLite file.
        }
    }
}
