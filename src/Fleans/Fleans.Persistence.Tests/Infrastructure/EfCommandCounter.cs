using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Fleans.Persistence.Tests.Infrastructure;

/// <summary>
/// Counts the SQL commands EF Core sends from the code running inside <see cref="Start"/>'s scope
/// (the current async flow), through EF's <see cref="DiagnosticListener"/> events — so a test can
/// pin how many round trips a write costs. Other tests running in parallel are not counted.
/// </summary>
internal sealed class EfCommandCounter : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
{
    private static readonly AsyncLocal<EfCommandCounter?> Current = new();

    private readonly List<IDisposable> _subscriptions = new();
    private readonly IDisposable _allListeners;
    private readonly List<string> _commands = new();

    private EfCommandCounter()
    {
        _allListeners = DiagnosticListener.AllListeners.Subscribe(this);
    }

    /// <summary>Starts counting the commands sent from the current async flow.</summary>
    public static EfCommandCounter Start()
    {
        var counter = new EfCommandCounter();
        Current.Value = counter;
        return counter;
    }

    /// <summary>The SQL of every command sent so far, in order.</summary>
    public IReadOnlyList<string> Commands
    {
        get { lock (_commands) return _commands.ToList(); }
    }

    public void OnNext(DiagnosticListener listener)
    {
        if (listener.Name == DbLoggerCategory.Name)
            lock (_subscriptions)
                _subscriptions.Add(listener.Subscribe(this));
    }

    public void OnNext(KeyValuePair<string, object?> value)
    {
        if (value.Key == RelationalEventId.CommandExecuting.Name
            && value.Value is CommandEventData data
            && ReferenceEquals(Current.Value, this))
        {
            lock (_commands)
                _commands.Add(data.Command.CommandText);
        }
    }

    public void OnCompleted() { }

    public void OnError(Exception error) { }

    public void Dispose()
    {
        if (ReferenceEquals(Current.Value, this))
            Current.Value = null;
        _allListeners.Dispose();
        lock (_subscriptions)
            foreach (var subscription in _subscriptions)
                subscription.Dispose();
    }
}
