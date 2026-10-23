using Fleans.Persistence;
using Fleans.ServiceDefaults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Fleans.Persistence.Tests;

[TestClass]
public class FleansPersistenceExtensionsTests
{
    private static HostApplicationBuilder NewBuilder(IDictionary<string, string?> configValues)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(configValues);
        return builder;
    }

    [TestMethod]
    public void Sqlite_provider_succeeds()
    {
        var builder = NewBuilder(new Dictionary<string, string?>
        {
            ["Persistence:Provider"] = "Sqlite",
        });

        builder.AddFleansPersistence();

        using var host = builder.Build();
        using var scope = host.Services.CreateScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<FleanCommandDbContext>>();
        using var db = dbFactory.CreateDbContext();
        Assert.AreEqual("Microsoft.EntityFrameworkCore.Sqlite", db.Database.ProviderName);
    }

    [TestMethod]
    public void SQLITE_provider_succeeds_case_insensitive()
    {
        var builder = NewBuilder(new Dictionary<string, string?>
        {
            ["Persistence:Provider"] = "SQLITE",
        });

        builder.AddFleansPersistence();

        using var host = builder.Build();
        using var scope = host.Services.CreateScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<FleanCommandDbContext>>();
        using var db = dbFactory.CreateDbContext();
        Assert.AreEqual("Microsoft.EntityFrameworkCore.Sqlite", db.Database.ProviderName);
    }

    [TestMethod]
    public void Postgres_provider_with_connection_string_succeeds()
    {
        var builder = NewBuilder(new Dictionary<string, string?>
        {
            ["Persistence:Provider"] = "Postgres",
            ["ConnectionStrings:fleans"] = "Host=test;Database=fleans;Username=fleans;Password=stub;",
        });

        builder.AddFleansPersistence();

        using var host = builder.Build();
        using var scope = host.Services.CreateScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<FleanCommandDbContext>>();
        using var db = dbFactory.CreateDbContext();
        Assert.AreEqual("Npgsql.EntityFrameworkCore.PostgreSQL", db.Database.ProviderName);
    }

    [TestMethod]
    public void Postgres_without_connection_string_throws_invalidOperation()
    {
        var builder = NewBuilder(new Dictionary<string, string?>
        {
            ["Persistence:Provider"] = "Postgres",
        });

        var ex = Assert.ThrowsExactly<InvalidOperationException>(() => builder.AddFleansPersistence());
        StringAssert.Contains(ex.Message, "Connection string 'fleans' is required");
    }

    [TestMethod]
    public void Unknown_provider_throws_argumentException()
    {
        var builder = NewBuilder(new Dictionary<string, string?>
        {
            ["Persistence:Provider"] = "PostgreSQL",
        });

        var ex = Assert.ThrowsExactly<ArgumentException>(() => builder.AddFleansPersistence());
        StringAssert.Contains(ex.Message, "'PostgreSQL'");
        StringAssert.Contains(ex.Message, "Sqlite, Postgres");
    }

    [TestMethod]
    public void Whitespace_provider_throws_argumentException()
    {
        var builder = NewBuilder(new Dictionary<string, string?>
        {
            ["Persistence:Provider"] = " ",
        });

        var ex = Assert.ThrowsExactly<ArgumentException>(() => builder.AddFleansPersistence());
        StringAssert.Contains(ex.Message, "Sqlite, Postgres");
    }

    [TestMethod]
    public void Empty_string_provider_throws_argumentException()
    {
        var builder = NewBuilder(new Dictionary<string, string?>
        {
            ["Persistence:Provider"] = "",
        });

        var ex = Assert.ThrowsExactly<ArgumentException>(() => builder.AddFleansPersistence());
        StringAssert.Contains(ex.Message, "''");
        StringAssert.Contains(ex.Message, "Sqlite, Postgres");
    }

    // ── Journal store (#879) ─────────────────────────────────────────────────

    private const string MainPg = "Host=main;Database=fleans;Username=fleans;Password=stub";
    private const string JournalPg = "Host=journal;Database=fleans_journal;Username=fleans;Password=stub";

    private static (string? Provider, string Connection, string Journal) Resolve(IDictionary<string, string?> config)
    {
        var builder = NewBuilder(config);
        builder.AddFleansPersistence();
        using var host = builder.Build();
        using var scope = host.Services.CreateScope();
        using var main = scope.ServiceProvider.GetRequiredService<IDbContextFactory<FleanCommandDbContext>>().CreateDbContext();
        using var journal = scope.ServiceProvider.GetRequiredService<IDbContextFactory<FleanJournalDbContext>>().CreateDbContext();
        var options = scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<FleansPersistenceOptions>>().Value;
        return (options.Journal.Provider, Host(main), Host(journal) + "|" + journal.Database.ProviderName);

        static string Host(DbContext db) => db.Database.GetDbConnection().DataSource;
    }

    [TestMethod]
    public void Journal_unconfigured_shares_the_main_postgres_database()
    {
        var (provider, main, journal) = Resolve(new Dictionary<string, string?>
        {
            ["Persistence:Provider"] = "Postgres",
            ["ConnectionStrings:fleans"] = MainPg,
        });

        Assert.AreEqual("Postgres", provider);
        Assert.AreEqual($"{main}|Npgsql.EntityFrameworkCore.PostgreSQL", journal);
    }

    [TestMethod]
    public void Journal_unconfigured_shares_the_main_sqlite_file()
    {
        var (provider, main, journal) = Resolve(new Dictionary<string, string?>
        {
            ["Persistence:Provider"] = "Sqlite",
            ["FLEANS_SQLITE_CONNECTION"] = "DataSource=main.db",
        });

        Assert.AreEqual("Sqlite", provider);
        Assert.AreEqual($"{main}|Microsoft.EntityFrameworkCore.Sqlite", journal);
    }

    [TestMethod]
    public void Journal_connection_string_points_the_journal_at_its_own_database()
    {
        var (_, main, journal) = Resolve(new Dictionary<string, string?>
        {
            ["Persistence:Provider"] = "Postgres",
            ["ConnectionStrings:fleans"] = MainPg,
            ["ConnectionStrings:fleans-journal"] = JournalPg,
        });

        Assert.AreEqual("tcp://main:5432", main);
        Assert.AreEqual("tcp://journal:5432|Npgsql.EntityFrameworkCore.PostgreSQL", journal);
    }

    [TestMethod]
    public void Citus_journal_without_its_own_connection_uses_the_main_postgres_database()
    {
        var (provider, main, journal) = Resolve(new Dictionary<string, string?>
        {
            ["Persistence:Provider"] = "Postgres",
            ["Persistence:Journal:Provider"] = "Citus",
            ["ConnectionStrings:fleans"] = MainPg,
        });

        Assert.AreEqual("Citus", provider);
        Assert.AreEqual($"{main}|Npgsql.EntityFrameworkCore.PostgreSQL", journal);
    }

    [TestMethod]
    public void Postgres_journal_for_a_sqlite_main_database_needs_its_connection_string()
    {
        var builder = NewBuilder(new Dictionary<string, string?>
        {
            ["Persistence:Provider"] = "Sqlite",
            ["Persistence:Journal:Provider"] = "Postgres",
        });

        var ex = Assert.ThrowsExactly<InvalidOperationException>(() => builder.AddFleansPersistence());
        StringAssert.Contains(ex.Message, "'fleans-journal' is required");
    }

    [TestMethod]
    public void Postgres_journal_for_a_sqlite_main_database_uses_its_connection_string()
    {
        var (_, _, journal) = Resolve(new Dictionary<string, string?>
        {
            ["Persistence:Provider"] = "Sqlite",
            ["Persistence:Journal:Provider"] = "Postgres",
            ["ConnectionStrings:fleans-journal"] = JournalPg,
        });

        Assert.AreEqual("tcp://journal:5432|Npgsql.EntityFrameworkCore.PostgreSQL", journal);
    }

    [TestMethod]
    public void Empty_journal_provider_with_a_journal_connection_means_postgres_even_next_to_sqlite()
    {
        var (provider, _, journal) = Resolve(new Dictionary<string, string?>
        {
            ["Persistence:Provider"] = "Sqlite",
            ["FLEANS_SQLITE_CONNECTION"] = "DataSource=main.db",
            ["ConnectionStrings:fleans-journal"] = JournalPg,
        });

        Assert.AreEqual("Postgres", provider);
        Assert.AreEqual("tcp://journal:5432|Npgsql.EntityFrameworkCore.PostgreSQL", journal);
    }

    [TestMethod]
    public void Citus_shard_count_is_bound_from_configuration()
    {
        var builder = NewBuilder(new Dictionary<string, string?>
        {
            ["Persistence:Provider"] = "Postgres",
            ["Persistence:Journal:Provider"] = "Citus",
            ["Persistence:Journal:Citus:ShardCount"] = "256",
            ["ConnectionStrings:fleans"] = MainPg,
        });
        builder.AddFleansPersistence();
        using var host = builder.Build();

        var options = host.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<FleansPersistenceOptions>>().Value;

        Assert.AreEqual(256, options.Journal.Citus.ShardCount);
    }

    [TestMethod]
    public void Unknown_journal_provider_throws_argumentException()
    {
        var builder = NewBuilder(new Dictionary<string, string?>
        {
            ["Persistence:Provider"] = "Postgres",
            ["Persistence:Journal:Provider"] = "Cassandra",
            ["ConnectionStrings:fleans"] = MainPg,
        });

        var ex = Assert.ThrowsExactly<ArgumentException>(() => builder.AddFleansPersistence());
        StringAssert.Contains(ex.Message, "'Cassandra'");
        StringAssert.Contains(ex.Message, "Sqlite, Postgres, Citus");
    }
}
