namespace Fleans.ServiceDefaults.Reminders;

/// <summary>
/// Connection names and the configuration section used by the reminder providers.
/// Reminders are their own subsystem with their own failure mode — see
/// <c>docs/conventions/reminders.md</c>.
/// </summary>
public static class FleansReminderOptions
{
    /// <summary>
    /// <c>Redis</c> provider: reminders ride the *clustering* Redis, shared with Orleans
    /// clustering + Pub-Sub streaming.
    /// </summary>
    public const string ConnectionName = "orleans-redis";

    /// <summary>
    /// <c>Postgres</c> provider: optional dedicated database for the reminders table (#886),
    /// so the 5-minute range re-reads do not compete with the read model. Falls back to the
    /// application's <c>fleans</c> connection when unset.
    /// </summary>
    public const string PostgresConnectionName = "fleans-reminders";

    /// <summary>
    /// Section holding <c>Provider</c>; it is also bound onto Orleans' <c>ReminderOptions</c>,
    /// so <c>Fleans:Reminders:ReminderLoadingWindow</c> etc. tune the reminder service.
    /// </summary>
    public const string SectionName = "Fleans:Reminders";
}
