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
}
