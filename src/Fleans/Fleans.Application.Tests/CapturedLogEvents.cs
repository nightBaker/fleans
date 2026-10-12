using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Fleans.Application.Tests;

/// <summary>
/// Records the EventId of every log entry written by the in-process test silos, so a test can
/// wait for a positive signal (e.g. "duplicate skipped", EventId 4038) instead of sleeping and
/// asserting that something did not happen. Static because all silos of a
/// <see cref="Orleans.TestingHost.TestCluster"/> run in the test process.
/// </summary>
public static class CapturedLogEvents
{
    private static readonly ConcurrentDictionary<int, int> Counts = new();

    public static void Clear() => Counts.Clear();

    public static int Count(int eventId) => Counts.TryGetValue(eventId, out var n) ? n : 0;

    /// <summary>Polls until at least <paramref name="expected"/> entries with <paramref name="eventId"/> were logged.</summary>
    public static async Task WaitFor(int eventId, int expected, int timeoutMs = 10000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline && Count(eventId) < expected)
            await Task.Delay(50);
        Assert.IsTrue(Count(eventId) >= expected,
            $"Expected at least {expected} log entries with EventId {eventId} within {timeoutMs}ms, got {Count(eventId)}.");
    }

    public sealed class Provider : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new Logger();

        public void Dispose() { }
    }

    private sealed class Logger : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (eventId.Id != 0)
                Counts.AddOrUpdate(eventId.Id, 1, (_, n) => n + 1);
        }
    }
}
