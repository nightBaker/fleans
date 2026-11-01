using Microsoft.Extensions.Configuration;
using Orleans.Configuration;
using Orleans.Hosting;
using StackExchange.Redis;

namespace Fleans.Worker.Hosting;

/// <summary>
/// The engine's durable reminder service for an external plugin host (#788).
/// <para>
/// Every silo owns a slice of the Orleans grain-service hash ring, and Orleans offers no way to
/// leave it. Engine reminders (BPMN timers registered by <c>TimerCallbackGrain</c>) whose hash
/// lands on a plugin silo are served by that silo: without a reminder service the registration
/// fails with <c>TypeLoadException: Orleans.IReminderService</c> and the timer's activity fails;
/// with an in-memory one the timer is lost when the plugin host restarts. So a plugin host runs
/// the same reminder table as the engine, chosen by the same <c>Fleans:Reminders:Provider</c> key.
/// </para>
/// <para>
/// Mirrors <c>Fleans.ServiceDefaults</c>' <c>AddFleansReminders</c> without referencing it (plugin
/// hosts must not load engine assemblies). The Postgres + <c>Persistence:Provider</c> consistency
/// check stays with the engine silos, which also create the reminders table.
/// </para>
/// </summary>
public static class PluginReminderExtensions
{
    public const string RedisConnectionName = "orleans-redis";
    public const string PostgresConnectionName = "fleans-reminders";
    public const string PostgresFallbackConnectionName = "fleans";

    /// <summary>
    /// Wires the engine's reminder table: Redis on <c>orleans-redis</c> (default), or Postgres on
    /// <c>fleans-reminders</c>, else <c>fleans</c>. Binds <c>Fleans:Reminders</c> onto
    /// <see cref="ReminderOptions"/>. Throws when the provider is unknown or its connection
    /// string is missing — there is no in-memory fallback. Called by
    /// <see cref="PluginHostExtensions.AddFleansPluginHost"/>.
    /// </summary>
    public static ISiloBuilder AddFleansPluginReminders(this ISiloBuilder siloBuilder, IConfiguration configuration)
    {
        var (provider, connectionString) = ResolveReminders(configuration);
        siloBuilder.Configure<ReminderOptions>(configuration.GetSection("Fleans:Reminders"));

        if (provider == "postgres")
            siloBuilder.UseAdoNetReminderService(o =>
            {
                o.Invariant = "Npgsql";
                o.ConnectionString = connectionString;
            });
        else
            siloBuilder.UseRedisReminderService(o => o.ConfigurationOptions = ConfigurationOptions.Parse(connectionString));

        return siloBuilder;
    }

    internal static (string Provider, string ConnectionString) ResolveReminders(IConfiguration configuration)
    {
        var configured = configuration["Fleans:Reminders:Provider"] ?? "Redis";
        switch (configured.ToLowerInvariant())
        {
            case "redis":
                return ("redis", configuration.GetConnectionString(RedisConnectionName)
                    ?? throw new InvalidOperationException(
                        $"ConnectionStrings:{RedisConnectionName} is required: a plugin host serves part of the engine's " +
                        "BPMN-timer reminders and must use the engine's Redis reminder table (#788)."));
            case "postgres":
                var dedicated = configuration.GetConnectionString(PostgresConnectionName);
                return ("postgres", !string.IsNullOrWhiteSpace(dedicated)
                    ? dedicated
                    : configuration.GetConnectionString(PostgresFallbackConnectionName)
                      ?? throw new InvalidOperationException(
                          $"Fleans:Reminders:Provider=Postgres needs ConnectionStrings:{PostgresConnectionName} or " +
                          $"ConnectionStrings:{PostgresFallbackConnectionName} — the engine's reminders database (#788)."));
            default:
                throw new InvalidOperationException(
                    $"Unknown Fleans:Reminders:Provider '{configured}'. Supported: Redis (default), Postgres — " +
                    "use the engine's value.");
        }
    }
}
