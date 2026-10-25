using System.Collections.Concurrent;
using Fleans.Application.Grains;
using Microsoft.Extensions.Logging;

namespace Fleans.Application.StartEvents;

/// <summary>
/// Which processes a message, signal or conditional start event starts, cached in every host
/// (#858) so a start goes straight to the new <c>WorkflowInstance</c> instead of through the
/// event's listener grain. The listener and registry grains stay the persisted registrations;
/// this cache reads them on a miss.
/// </summary>
/// <remarks>
/// <para><b>Refresh.</b> Deploy, enable and disable tell <see cref="IStartEventSubscriptionNotifierGrain"/>,
/// which calls <see cref="SubscriptionsChanged"/> on every subscribed host before the deployment
/// call returns. A host that misses it still reloads each entry <see cref="EntryLifetime"/> after
/// loading it, and drops everything when a renewal finds it was not subscribed — so a cached
/// entry is at most <see cref="EntryLifetime"/> stale.</para>
/// <para><b>Stale entries are safe one way.</b> The start path still asks the process's
/// <see cref="IProcessDefinitionGrain"/> for its startable latest definition, so a process that
/// was disabled or no longer has the start event starts nothing even from a stale entry. Only a
/// newly subscribed process can be missed, until the refresh.</para>
/// <para>Concurrent misses for one name share one load. Unknown names are cached too (empty), so
/// the cache is cleared once it holds <see cref="MaxEntries"/> names.</para>
/// </remarks>
public sealed partial class StartEventSubscriptionCache : IStartEventSubscriptionObserver
{
    /// <summary>The staleness bound when a host misses a change notification.</summary>
    public static readonly TimeSpan EntryLifetime = TimeSpan.FromSeconds(30);

    /// <summary>How often a host in use renews its subscription with the notifier.</summary>
    internal static readonly TimeSpan RenewInterval = TimeSpan.FromMinutes(1);

    internal const int MaxEntries = 10_000;

    private const string ConditionalKey = "";

    private readonly ConcurrentDictionary<string, Entry<IReadOnlyList<string>>> _messages = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Entry<IReadOnlyList<string>>> _signals = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Entry<IReadOnlyList<ConditionalStartEntry>>> _conditionals = new(StringComparer.Ordinal);

    private readonly IGrainFactory _grainFactory;
    private readonly TimeProvider _time;
    private readonly ILogger<StartEventSubscriptionCache> _logger;
    private readonly object _renewLock = new();

    private long _generation;
    private IStartEventSubscriptionObserver? _reference;
    private Task? _renewal;
    private long _renewDueAtTicks;
    private volatile bool _subscribed;

    public StartEventSubscriptionCache(IGrainFactory grainFactory, TimeProvider time, ILogger<StartEventSubscriptionCache> logger)
    {
        _grainFactory = grainFactory;
        _time = time;
        _logger = logger;
    }

    /// <summary>Keys of the processes a message start event named <paramref name="messageName"/> starts.</summary>
    public ValueTask<IReadOnlyList<string>> GetMessageStartProcesses(string messageName) =>
        GetOrLoad(_messages, messageName, async name =>
            (IReadOnlyList<string>)await _grainFactory.GetGrain<IMessageStartEventListenerGrain>(name).GetProcessKeys());

    /// <summary>Keys of the processes a signal start event named <paramref name="signalName"/> starts.</summary>
    public ValueTask<IReadOnlyList<string>> GetSignalStartProcesses(string signalName) =>
        GetOrLoad(_signals, signalName, async name =>
            (IReadOnlyList<string>)await _grainFactory.GetGrain<ISignalStartEventListenerGrain>(name).GetProcessKeys());

    /// <summary>Every registered conditional start event.</summary>
    public ValueTask<IReadOnlyList<ConditionalStartEntry>> GetConditionalStarts() =>
        GetOrLoad(_conditionals, ConditionalKey, async _ =>
            (IReadOnlyList<ConditionalStartEntry>)await _grainFactory.GetGrain<IConditionalStartEventRegistryGrain>(0).GetAll());

    /// <summary>Drops every cached entry; the next lookups reload.</summary>
    public void Invalidate()
    {
        // The generation first: a load that started before this call is never served after it.
        Interlocked.Increment(ref _generation);
        _messages.Clear();
        _signals.Clear();
        _conditionals.Clear();
        LogInvalidated();
    }

    Task IStartEventSubscriptionObserver.SubscriptionsChanged()
    {
        Invalidate();
        return Task.CompletedTask;
    }

    private async ValueTask<T> GetOrLoad<T>(
        ConcurrentDictionary<string, Entry<T>> entries, string key, Func<string, Task<T>> load)
    {
        await RenewSubscriptionIfDue();

        while (true)
        {
            if (!entries.TryGetValue(key, out var entry))
            {
                // Only the entry that wins TryAdd ever starts its load (the Lazy), so concurrent
                // misses share one read.
                var created = NewEntry(key, load);
                if (!entries.TryAdd(key, created))
                    continue;
                entry = created;

                // Count takes every bucket lock: checked only when a name is added, off the hit path.
                if (entries.Count > MaxEntries)
                {
                    LogCacheFull(MaxEntries);
                    entries.Clear();
                }
            }
            else if (!IsCurrent(entry))
            {
                entries.TryRemove(KeyValuePair.Create(key, entry));
                continue;
            }

            try
            {
                return await entry.Value.Value;
            }
            catch
            {
                // Only this failed load: a newer one that already replaced it stays.
                entries.TryRemove(KeyValuePair.Create(key, entry));
                throw;
            }
        }
    }

    // The generation is read before the load starts: a load that an Invalidate overtakes keeps the
    // older generation, so it is never served after that Invalidate.
    private Entry<T> NewEntry<T>(string key, Func<string, Task<T>> load) =>
        new(new Lazy<Task<T>>(() => load(key)), Interlocked.Read(ref _generation), _time.GetUtcNow() + EntryLifetime);

    private bool IsCurrent<T>(Entry<T> entry) =>
        entry.Generation == Interlocked.Read(ref _generation) && _time.GetUtcNow() < entry.ExpiresAt;

    /// <summary>
    /// The first lookup waits for the subscription, so a change made after it is not missed; later
    /// renewals run in the background. A failed attempt is retried at the next renewal.
    /// </summary>
    private Task RenewSubscriptionIfDue()
    {
        // Hot path: subscribed and not yet due, so no lock.
        if (_subscribed && _time.GetUtcNow().UtcTicks < Interlocked.Read(ref _renewDueAtTicks))
            return Task.CompletedTask;

        Task? renewal;
        lock (_renewLock)
        {
            var now = _time.GetUtcNow();
            if (now.UtcTicks >= _renewDueAtTicks && _renewal is null or { IsCompleted: true })
            {
                Interlocked.Exchange(ref _renewDueAtTicks, (now + RenewInterval).UtcTicks);
                _renewal = Renew();
            }
            renewal = _renewal;
        }

        return _subscribed || renewal is null ? Task.CompletedTask : renewal;
    }

    private async Task Renew()
    {
        try
        {
            _reference ??= _grainFactory.CreateObjectReference<IStartEventSubscriptionObserver>(this);
            var wasSubscribed = await _grainFactory.GetGrain<IStartEventSubscriptionNotifierGrain>(0)
                .Subscribe(_reference);
            if (!wasSubscribed)
                Invalidate();
            _subscribed = true;
        }
        catch (Exception ex)
        {
            // Cached entries still expire after EntryLifetime, so this degrades freshness only.
            LogRenewFailed(ex);
        }
    }

    private sealed record Entry<T>(Lazy<Task<T>> Value, long Generation, DateTimeOffset ExpiresAt);

    [LoggerMessage(EventId = 9385, Level = LogLevel.Debug,
        Message = "Start-event subscription cache cleared; the next starts reload the registrations")]
    private partial void LogInvalidated();

    [LoggerMessage(EventId = 9386, Level = LogLevel.Warning,
        Message = "Start-event subscription cache could not subscribe to deployment changes; entries refresh only on expiry until it can")]
    private partial void LogRenewFailed(Exception ex);

    [LoggerMessage(EventId = 9387, Level = LogLevel.Warning,
        Message = "Start-event subscription cache passed {MaxEntries} event names and was cleared")]
    private partial void LogCacheFull(int maxEntries);
}
