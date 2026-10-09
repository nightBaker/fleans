using Fleans.Persistence;
using Fleans.Persistence.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SQLitePCL;

namespace Fleans.Persistence.Tests;

/// <summary>
/// Regression coverage for #660 — verifies <see cref="SqliteSchemaInitializer.EnsureCreatedIgnoreRaces"/>
/// narrows its exception filter to the "table/index already exists" race only.
/// Any other <see cref="SqliteException"/> (permission denied, corruption, disk full,
/// can't-open) must surface as a startup crash, not silent breakage at first query.
/// </summary>
[TestClass]
public class SqliteSchemaInitializerTests
{
    [TestMethod]
    public void EnsureCreatedIgnoreRaces_RethrowsOnUnopenableDatabase()
    {
        // Arrange — Data Source pointing at a directory that does not exist.
        // SQLite cannot create the file there, so EnsureCreated() throws
        // SqliteException with SQLITE_CANTOPEN (14). Maps directly to the
        // issue body's "permission-denied / corrupt-file" scenario without
        // needing any platform-specific filesystem manipulation.
        var ctx = new FleanCommandDbContext(new DbContextOptionsBuilder<FleanCommandDbContext>()
            .UseFleansSqlite("Data Source=/nonexistent/__fleans_660_test_dir__/foo.db")
            .Options);

        // Act + Assert — must throw, must not be swallowed.
        var ex = Assert.ThrowsExactly<SqliteException>(() =>
            SqliteSchemaInitializer.EnsureCreatedIgnoreRaces(ctx.Database));

        // The error code must be SQLITE_CANTOPEN (14), NOT the swallowed
        // SQLITE_ERROR (1). If the filter regresses to a bare
        // catch (SqliteException), Assert.ThrowsException above fails first;
        // this check additionally pins the *kind* of error to prevent future
        // false positives.
        Assert.AreEqual(raw.SQLITE_CANTOPEN, ex.SqliteErrorCode,
            "expected SQLITE_CANTOPEN to be rethrown, not the swallowed SQLITE_ERROR race");
    }

    [TestMethod]
    public void EnsureCreatedIgnoreRaces_AddsMissingAdditiveColumns_OnExistingDb()
    {
        // Arrange — #762: an existing dev DB created before WorkflowInstances.IsFailed existed.
        // EnsureCreated() is a no-op on a non-empty file, so the initializer must backfill.
        var dbPath = Path.GetTempFileName();
        try
        {
            var options = new DbContextOptionsBuilder<FleanCommandDbContext>()
                .UseFleansSqlite($"DataSource={dbPath}")
                .Options;
            using (var ctx = new FleanCommandDbContext(options))
                SqliteSchemaInitializer.EnsureCreatedIgnoreRaces(ctx.Database);

            using (var conn = new SqliteConnection($"DataSource={dbPath}"))
            {
                conn.Open();
                using var drop = conn.CreateCommand();
                drop.CommandText = "ALTER TABLE \"WorkflowInstances\" DROP COLUMN \"IsFailed\";";
                drop.ExecuteNonQuery();
            }

            // Act — run the initializer twice: the second run must be a no-op.
            using (var ctx = new FleanCommandDbContext(options))
                SqliteSchemaInitializer.EnsureCreatedIgnoreRaces(ctx.Database);
            using (var ctx = new FleanCommandDbContext(options))
                SqliteSchemaInitializer.EnsureCreatedIgnoreRaces(ctx.Database);

            // Assert — the column exists and the EF model can query it.
            using (var verify = new SqliteConnection($"DataSource={dbPath}"))
            {
                verify.Open();
                using var cmd = verify.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('WorkflowInstances') WHERE name = 'IsFailed';";
                Assert.AreEqual(1L, (long)cmd.ExecuteScalar()!);
            }
            using (var ctx = new FleanCommandDbContext(options))
                Assert.AreEqual(0, ctx.WorkflowInstances.Count(w => w.IsFailed));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" })
                if (File.Exists(dbPath + suffix)) File.Delete(dbPath + suffix);
        }
    }
}
