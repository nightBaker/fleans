namespace Fleans.ServiceDefaults.Reminders;

/// <summary>
/// Reminders ride the *clustering* Redis (connection name <c>orleans-redis</c>),
/// shared with Orleans clustering + Pub-Sub streaming. NOT the application
/// persistence DB — these are distinct subsystems with different failure modes.
/// </summary>
public static class FleansReminderOptions
{
    public const string ConnectionName = "orleans-redis";
}
