using Fleans.ServiceDefaults.Reminders;
using Microsoft.Extensions.Configuration;

namespace Fleans.ServiceDefaults.Tests;

/// <summary>
/// Covers <see cref="FleansRemindersExtensions.ResolveRemindersConfiguration"/> —
/// the provider-switch and fail-fast surface (issue #669).
/// </summary>
[TestClass]
public class FleansRemindersExtensionsTests
{
    [TestMethod]
    public void Default_provider_is_Redis_when_unset()
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:orleans-redis"] = "localhost:6379",
            }).Build();

        var resolved = FleansRemindersExtensions.ResolveRemindersConfiguration(cfg);

        Assert.AreEqual(RemindersProvider.Redis, resolved.Provider);
        Assert.AreEqual("localhost:6379", resolved.ConnectionString);
    }

    [TestMethod]
    public void Redis_provider_throws_when_orleans_redis_connection_missing()
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection().Build();

        var ex = Assert.ThrowsExactly<InvalidOperationException>(
            () => FleansRemindersExtensions.ResolveRemindersConfiguration(cfg));

        StringAssert.Contains(ex.Message, "orleans-redis");
        StringAssert.Contains(ex.Message, "silent fallback to in-memory reminders is");
    }

    [TestMethod]
    public void Postgres_provider_throws_when_persistence_is_Sqlite()
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Fleans:Reminders:Provider"] = "Postgres",
                ["Persistence:Provider"] = "Sqlite",
                ["ConnectionStrings:fleans"] = "Host=localhost;Database=fleans;Username=u;Password=p",
            }).Build();

        var ex = Assert.ThrowsExactly<InvalidOperationException>(
            () => FleansRemindersExtensions.ResolveRemindersConfiguration(cfg));

        StringAssert.Contains(ex.Message, "Persistence:Provider=Postgres");
        StringAssert.Contains(ex.Message, "Mixed-storage");
    }

    [TestMethod]
    public void Postgres_provider_throws_when_fleans_connection_missing()
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Fleans:Reminders:Provider"] = "Postgres",
                ["Persistence:Provider"] = "Postgres",
            }).Build();

        var ex = Assert.ThrowsExactly<InvalidOperationException>(
            () => FleansRemindersExtensions.ResolveRemindersConfiguration(cfg));

        StringAssert.Contains(ex.Message, "'fleans'");
        StringAssert.Contains(ex.Message, "Fleans:Reminders:Provider=Postgres");
    }

    [TestMethod]
    public void Postgres_provider_resolves_when_persistence_and_fleans_present()
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Fleans:Reminders:Provider"] = "Postgres",
                ["Persistence:Provider"] = "Postgres",
                ["ConnectionStrings:fleans"] = "Host=localhost;Database=fleans;Username=u;Password=p",
            }).Build();

        var resolved = FleansRemindersExtensions.ResolveRemindersConfiguration(cfg);

        Assert.AreEqual(RemindersProvider.Postgres, resolved.Provider);
        StringAssert.Contains(resolved.ConnectionString, "Host=localhost");
    }

    [TestMethod]
    public void Unknown_provider_throws_ArgumentException()
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Fleans:Reminders:Provider"] = "MySQL",
            }).Build();

        var ex = Assert.ThrowsExactly<ArgumentException>(
            () => FleansRemindersExtensions.ResolveRemindersConfiguration(cfg));

        StringAssert.Contains(ex.Message, "MySQL");
        StringAssert.Contains(ex.Message, "Supported: Redis (default), Postgres");
    }
}
