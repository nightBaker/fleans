using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Fleans.Application.Tests;

/// <summary>
/// Records (logger category, EventId) of every log entry written by the in-process test silos, so a
/// test can wait for a positive signal (e.g. "duplicate skipped", EventId 4038) instead of sleeping
/// and asserting that something did not happen. Static because all silos of a
/// <see cref="Orleans.TestingHost.TestCluster"/> run in the test process; counts are keyed by the
/// logger category so a test only sees entries from the types it owns (e.g. its own test stubs).
/// </summary>
public static class CapturedLogEvents
{
    private static readonly ConcurrentDictionary<(string Category, int EventId), int> Counts = new();

    public static void Clear() => Counts.Clear();

    public static int Count<TCategory>(int eventId)
        => Counts.TryGetValue((typeof(TCategory).FullName!, eventId), out var n) ? n : 0;

    /// <summary>
    /// Polls until at least <paramref name="expected"/> entries with <paramref name="eventId"/> were
    /// logged by an <c>ILogger&lt;TCategory&gt;</c>.
    /// </summary>
    public static async Task WaitFor<TCategory>(int eventId, int expected, int timeoutMs = 10000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline && Count<TCategory>(eventId) < expected)
            await Task.Delay(50);
        Assert.IsTrue(Count<TCategory>(eventId) >= expected,
            $"Expected at least {expected} log entries with EventId {eventId} from {typeof(TCategory).Name} " +
            $"within {timeoutMs}ms, got {Count<TCategory>(eventId)}.");
    }

    public sealed class Provider : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new Logger(categoryName);

        public void Dispose() { }
    }

    private sealed class Logger(string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (eventId.Id != 0)
                Counts.AddOrUpdate((category, eventId.Id), 1, (_, n) => n + 1);
        }
    }
}
