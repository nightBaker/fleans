using Microsoft.Extensions.Configuration;
using Orleans.Hosting;
using StackExchange.Redis;

namespace Fleans.ServiceDefaults.Reminders;

public static class FleansRemindersExtensions
{
    /// <summary>
    /// Configures the silo's Orleans reminder service. The provider is selected via
    /// <c>Fleans:Reminders:Provider</c> (default <c>Redis</c>, case-insensitive;
    /// supported: <c>Redis</c>, <c>Postgres</c>).
    ///
    /// <c>Redis</c> shares the <c>orleans-redis</c> connection used for clustering and
    /// streaming; <c>Postgres</c> shares the <c>fleans</c> connection used by
    /// <c>Persistence:Provider=Postgres</c> and is only allowed when persistence is
    /// also Postgres (mixed-storage is rejected at startup). See
    /// <c>docs/conventions/reminders.md</c>.
    ///
    /// Fails fast (throws <see cref="InvalidOperationException"/>) if the chosen
    /// provider's connection string is missing, per CLAUDE.md's "Registration-vs-
    /// cleanup error asymmetry" load-bearing invariant. BPMN timer durability is a
    /// correctness invariant — silent fallback to in-memory reminders is disallowed.
    /// </summary>
    public static ISiloBuilder AddFleansReminders(this ISiloBuilder siloBuilder, IConfiguration configuration)
    {
        var resolved = ResolveRemindersConfiguration(configuration);

        switch (resolved.Provider)
        {
            case RemindersProvider.Redis:
                siloBuilder.UseRedisReminderService(options =>
                    options.ConfigurationOptions = ConfigurationOptions.Parse(resolved.ConnectionString));
                break;
            case RemindersProvider.Postgres:
                siloBuilder.UseAdoNetReminderService(options =>
                {
                    options.Invariant = "Npgsql";
                    options.ConnectionString = resolved.ConnectionString;
                });
                break;
        }
        return siloBuilder;
    }

    /// <summary>
    /// Validates the reminders config and resolves the provider + connection string,
    /// or throws. Exposed for unit tests so the throw paths can be exercised without
    /// standing up an <see cref="ISiloBuilder"/> / TestCluster.
    /// </summary>
    public static ResolvedRemindersConfiguration ResolveRemindersConfiguration(IConfiguration configuration)
    {
        var providerRaw = configuration.GetValue<string>("Fleans:Reminders:Provider") ?? "Redis";

        switch (providerRaw.ToLowerInvariant())
        {
            case "redis":
                {
                    var connString = configuration.GetConnectionString(FleansReminderOptions.ConnectionName)
                        ?? throw new InvalidOperationException(
                            $"'{FleansReminderOptions.ConnectionName}' connection string is required for Fleans reminders. " +
                            "BPMN timer durability is a correctness invariant — silent fallback to in-memory reminders is " +
                            "explicitly disallowed (see docs/conventions/reminders.md).");
                    return new ResolvedRemindersConfiguration(RemindersProvider.Redis, connString);
                }
            case "postgres":
                {
                    // D4: SQLite-app + Postgres-reminders is operationally odd (re-introduces the
                    // external-DB dependency the SQLite single-binary story exists to avoid).
                    // Fail fast — operator can argue for it with a follow-up issue.
                    var persistenceProvider = configuration["Persistence:Provider"] ?? "Sqlite";
                    if (!persistenceProvider.Equals("Postgres", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException(
                            $"Fleans:Reminders:Provider=Postgres requires Persistence:Provider=Postgres (got '{persistenceProvider}'). " +
                            "Mixed-storage (SQLite app + Postgres reminders) is unsupported — see #669.");

                    var connString = configuration.GetConnectionString("fleans")
                        ?? throw new InvalidOperationException(
                            "Connection string 'fleans' is required when Fleans:Reminders:Provider=Postgres " +
                            "(reuses the Persistence:Provider=Postgres connection — see docs/conventions/reminders.md).");
                    return new ResolvedRemindersConfiguration(RemindersProvider.Postgres, connString);
                }
            default:
                throw new ArgumentException(
                    $"Unknown reminders provider '{providerRaw}'. Supported: Redis (default), Postgres. " +
                    $"Set Fleans:Reminders:Provider in configuration.");
        }
    }
}

public enum RemindersProvider
{
    Redis,
    Postgres,
}

public readonly record struct ResolvedRemindersConfiguration(RemindersProvider Provider, string ConnectionString);
