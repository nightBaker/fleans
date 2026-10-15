using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Fleans.Application.Tests;

/// <summary>
/// Records (logger category, EventId) of every log entry written by the in-process test silos, so a
/// test can wait for a positive signal (e.g. "duplicate skipped", EventId 4038) instead of sleeping
/// and asserting that something did not happen. Static because all silos of a
/// <see cref="Orleans.TestingHost.TestCluster"/> run in the test process; counts are keyed by the
/// logger category so a test only sees entries from the types it owns (e.g. its own test stubs).
/// For EventIds passed to <see cref="Capture"/> it also keeps the message and the active scope
/// fields, so a test can assert what a log line says and which correlation ids it carries. Capture
/// stops when the returned handle is disposed; callers must be <c>[DoNotParallelize]</c>.
/// </summary>
public static class CapturedLogEvents
{
    private static readonly ConcurrentDictionary<(string Category, int EventId), int> Counts = new();
    private static readonly ConcurrentDictionary<int, ConcurrentQueue<Entry>> Captured = new();
    private static readonly AsyncLocal<Scope?> CurrentScope = new();

    public sealed record Entry(string Category, string Message, IReadOnlyDictionary<string, object?> ScopeFields);

    public static void Clear()
    {
        Counts.Clear();
        Captured.Clear();
    }

    /// <summary>
    /// Keeps full entries for <paramref name="eventIds"/> until the returned handle is disposed.
    /// </summary>
    public static IDisposable Capture(params int[] eventIds)
    {
        Captured.Clear();
        foreach (var id in eventIds)
            Captured[id] = new ConcurrentQueue<Entry>();
        return new CaptureHandle();
    }

    public static IReadOnlyList<Entry> Entries(int eventId)
        => Captured.TryGetValue(eventId, out var q) ? q.ToList() : [];

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
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
            => CurrentScope.Value = new Scope(state, CurrentScope.Value);

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (eventId.Id != 0)
                Counts.AddOrUpdate((category, eventId.Id), 1, (_, n) => n + 1);
            if (Captured.TryGetValue(eventId.Id, out var queue))
                queue.Enqueue(new Entry(category, formatter(state, exception), CollectScopeFields()));
        }

        // Inner scopes win over outer ones, as in structured-log sinks.
        private static Dictionary<string, object?> CollectScopeFields()
        {
            var fields = new Dictionary<string, object?>();
            for (var scope = CurrentScope.Value; scope is not null; scope = scope.Parent)
                if (scope.State is IEnumerable<KeyValuePair<string, object?>> pairs)
                    foreach (var (key, value) in pairs)
                        fields.TryAdd(key, value);
            return fields;
        }
    }

    private sealed class CaptureHandle : IDisposable
    {
        public void Dispose() => Captured.Clear();
    }

    private sealed class Scope(object state, Scope? parent) : IDisposable
    {
        public object State { get; } = state;
        public Scope? Parent { get; } = parent;

        public void Dispose() => CurrentScope.Value = Parent;
    }
}
