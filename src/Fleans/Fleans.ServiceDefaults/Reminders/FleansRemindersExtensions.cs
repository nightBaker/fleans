using Microsoft.Extensions.Configuration;
using Orleans.Hosting;
using StackExchange.Redis;

namespace Fleans.ServiceDefaults.Reminders;

public static class FleansRemindersExtensions
{
    /// <summary>
    /// Configures the silo to use the Redis-backed Orleans reminder service.
    /// Reminders share the <c>orleans-redis</c> connection used for clustering and
    /// streaming — see <c>docs/conventions/reminders.md</c>.
    ///
    /// Fails fast (throws <see cref="InvalidOperationException"/>) if the
    /// <c>orleans-redis</c> connection string is unavailable, per the
    /// "Registration-vs-cleanup error asymmetry" load-bearing invariant in
    /// CLAUDE.md. BPMN timer durability is a correctness invariant — silent
    /// fallback to in-memory reminders would re-create the exact bug this
    /// configuration prevents (#650).
    /// </summary>
    /// <remarks>
    /// The 1-minute reminder period used by <c>TimerCallbackGrain</c> is
    /// Orleans's required minimum granularity, not a bug — that grain
    /// unregisters in <c>ReceiveReminder</c> after the first fire
    /// (see <c>TimerCallbackGrain.cs:44, :66</c>).
    /// </remarks>
    public static ISiloBuilder AddFleansReminders(this ISiloBuilder siloBuilder, IConfiguration configuration)
    {
        var connString = configuration.GetConnectionString(FleansReminderOptions.ConnectionName)
            ?? throw new InvalidOperationException(
                $"'{FleansReminderOptions.ConnectionName}' connection string is required for Fleans reminders. " +
                "BPMN timer durability is a correctness invariant — silent fallback to in-memory reminders is " +
                "explicitly disallowed (see docs/conventions/reminders.md).");

        siloBuilder.UseRedisReminderService(options =>
            options.ConfigurationOptions = ConfigurationOptions.Parse(connString));

        return siloBuilder;
    }
}
