using Fleans.Application.Placement;
using Microsoft.Extensions.Logging;
using Orleans.Concurrency;

namespace Fleans.Application.StartEvents;

/// <summary>
/// One per cluster (key 0), in memory only: a lost activation loses its observers, and each host
/// learns that from its next <see cref="Subscribe"/> returning <c>false</c>.
/// </summary>
[Reentrant]
[CorePlacement]
public sealed partial class StartEventSubscriptionNotifierGrain : Grain, IStartEventSubscriptionNotifierGrain
{
    /// <summary>How long a subscription lives without renewal. Hosts renew every minute while in use.</summary>
    internal static readonly TimeSpan SubscriptionLifetime = TimeSpan.FromMinutes(5);

    /// <summary>A host that does not answer in time is dropped; its next renewal tells it to reload.</summary>
    internal static readonly TimeSpan NotifyTimeout = TimeSpan.FromSeconds(5);

    // [Reentrant] interleaves only at awaits, so the dictionary is never touched concurrently.
    private readonly Dictionary<IStartEventSubscriptionObserver, DateTimeOffset> _observers = new();
    private readonly TimeProvider _time;
    private readonly ILogger<StartEventSubscriptionNotifierGrain> _logger;

    public StartEventSubscriptionNotifierGrain(TimeProvider time, ILogger<StartEventSubscriptionNotifierGrain> logger)
    {
        _time = time;
        _logger = logger;
    }

    public Task<bool> Subscribe(IStartEventSubscriptionObserver observer)
    {
        var wasSubscribed = _observers.ContainsKey(observer);
        _observers[observer] = _time.GetUtcNow();
        if (!wasSubscribed)
            LogObserverSubscribed(_observers.Count);
        return Task.FromResult(wasSubscribed);
    }

    public async Task NotifyChanged()
    {
        var now = _time.GetUtcNow();
        foreach (var expired in _observers.Where(o => now - o.Value > SubscriptionLifetime).Select(o => o.Key).ToList())
            _observers.Remove(expired);

        var observers = _observers.Keys.ToList();
        await Task.WhenAll(observers.Select(Notify));
        LogObserversNotified(observers.Count);
    }

    private async Task Notify(IStartEventSubscriptionObserver observer)
    {
        try
        {
            await observer.SubscriptionsChanged().WaitAsync(NotifyTimeout);
        }
        catch (Exception ex)
        {
            // Dropped, so its next renewal returns false and it reloads; until then its cache
            // entries still expire (StartEventSubscriptionCache.EntryLifetime).
            _observers.Remove(observer);
            LogObserverDropped(ex);
        }
    }

    [LoggerMessage(EventId = 9390, Level = LogLevel.Debug,
        Message = "Start-event subscription observer subscribed ({ObserverCount} subscribed)")]
    private partial void LogObserverSubscribed(int observerCount);

    [LoggerMessage(EventId = 9391, Level = LogLevel.Information,
        Message = "Start-event subscriptions changed; notified {ObserverCount} host cache(s)")]
    private partial void LogObserversNotified(int observerCount);

    [LoggerMessage(EventId = 9392, Level = LogLevel.Warning,
        Message = "A host cache did not take a start-event subscription change and was dropped; it reloads on its next renewal")]
    private partial void LogObserverDropped(Exception ex);
}
