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

        // Enable WAL once. Sticky across connections via the database header — subsequent
        // opens on any thread or process see the file in WAL mode without re-running the
        // pragma. Idempotent: re-running on an already-WAL file is a no-op.
        //
        // PRAGMA returns the resulting mode as a 1-row result set, so we use ExecuteScalar
        // (not ExecuteNonQuery — some provider versions treat that as undefined behaviour
        // for PRAGMA).
        //
        // busy_timeout intentionally NOT set here: Microsoft.Data.Sqlite already defaults
        // SqliteCommand.CommandTimeout to 30s and internally calls sqlite3_busy_timeout
        // before each command. The PRAGMA-level setting would be per-connection and not
        // worth the maintenance cost given the provider default suffices.
        var conn = (SqliteConnection)database.GetDbConnection();
        var wasClosed = conn.State != System.Data.ConnectionState.Open;
        try
        {
            if (wasClosed) conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA journal_mode=WAL;";
            _ = cmd.ExecuteScalar(); // returns "wal" for file-backed, "memory" for in-memory — we don't care which
        }
        catch (SqliteException ex)
        {
            // Likely an in-memory or read-only DB. WAL doesn't apply to those; safe to
            // proceed. Surface the message so a misconfigured writable DB (e.g.,
            // directory permission lost between deploys) isn't silently downgraded to
            // DELETE-mode journaling.
            Console.Error.WriteLine($"[Fleans] WARNING: Failed to set journal_mode=WAL: {ex.Message}");
        }
        finally
        {
            if (wasClosed && conn.State == System.Data.ConnectionState.Open) conn.Close();
        }
    }
}
