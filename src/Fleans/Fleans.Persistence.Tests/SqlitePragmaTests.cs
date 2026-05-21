using Fleans.Persistence.Events;
using Fleans.Persistence.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Fleans.Persistence.Tests;

/// <summary>
/// #656 — verifies <c>SqliteSchemaInitializer.EnsureCreatedIgnoreRaces</c> sets
/// <c>PRAGMA journal_mode=WAL</c> on file-backed databases and silently no-ops
/// (without throwing) on in-memory databases where WAL is inapplicable.
/// </summary>
[TestClass]
public class SqlitePragmaTests
{
    [TestMethod]
    public void EnsureCreatedIgnoreRaces_SetsWalJournalMode_OnFileBackedDb()
    {
        // Arrange — real temp file. In-memory would no-op the pragma ("memory" mode)
        // and not exercise WAL persistence in the file header.
        var dbPath = Path.GetTempFileName();
        try
        {
            using (var ctx = new FleanCommandDbContext(
                new DbContextOptionsBuilder<FleanCommandDbContext>()
                    .UseFleansSqlite($"DataSource={dbPath}")
                    .Options))
            {
                SqliteSchemaInitializer.EnsureCreatedIgnoreRaces(ctx.Database);
            }

            // Open a *fresh* connection (not reusing the ctx that ran the pragma)
            // to prove WAL persisted in the file header, not just in the connection
            // that set it.
            using var verifyConn = new SqliteConnection($"DataSource={dbPath}");
            verifyConn.Open();
            using var cmd = verifyConn.CreateCommand();
            cmd.CommandText = "PRAGMA journal_mode;";
            var mode = (string?)cmd.ExecuteScalar();
            Assert.AreEqual("wal", mode, ignoreCase: true);
        }
        finally
        {
            // Microsoft.Data.Sqlite pools connections per connection-string. On Windows
            // the file handle stays held even after Dispose, so File.Delete fails with
            // "process cannot access the file". Clearing the pool releases the handles.
            SqliteConnection.ClearAllPools();

            // WAL creates -wal and -shm sidecar files. Clean all three.
            foreach (var suffix in new[] { "", "-wal", "-shm" })
                if (File.Exists(dbPath + suffix)) File.Delete(dbPath + suffix);
        }
    }

    [DataTestMethod]
    [DataRow("DataSource=:memory:")]
    [DataRow("DataSource=file::memory:?cache=shared")]
    public void EnsureCreatedIgnoreRaces_DoesNotThrow_OnInMemoryDb(string connStr)
    {
        // In-memory DBs return "memory" (not "wal") from PRAGMA journal_mode=WAL.
        // The helper must not throw on that case. Both common in-memory forms are
        // covered: bare `:memory:` and the shared-cache form used by WorkflowTestBase.
        using var ctx = new FleanCommandDbContext(
            new DbContextOptionsBuilder<FleanCommandDbContext>()
                .UseFleansSqlite(connStr)
                .Options);

        SqliteSchemaInitializer.EnsureCreatedIgnoreRaces(ctx.Database);
        // No assertion needed beyond "did not throw" — the in-memory case has no
        // file header to verify, and the helper's contract here is just "don't crash".
    }
}
