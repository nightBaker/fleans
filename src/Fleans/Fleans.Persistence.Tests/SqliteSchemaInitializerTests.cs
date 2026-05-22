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
}
