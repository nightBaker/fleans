using Fleans.Application.Grains;
using Fleans.Application.StartEvents;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Fleans.Application.Tests;

/// <summary>
/// The per-host cache of start-event subscribers (#858): one registration read per name until the
/// entry expires or a deployment change clears it, so a missed change is stale for at most
/// <see cref="StartEventSubscriptionCache.EntryLifetime"/>.
/// </summary>
[TestClass]
public class StartEventSubscriptionCacheTests
{
    private IGrainFactory _grainFactory = null!;
    private IMessageStartEventListenerGrain _listener = null!;
    private IStartEventSubscriptionNotifierGrain _notifier = null!;
    private ManualTime _time = null!;
    private StartEventSubscriptionCache _cache = null!;

    [TestInitialize]
    public void Setup()
    {
        _grainFactory = Substitute.For<IGrainFactory>();
        _listener = Substitute.For<IMessageStartEventListenerGrain>();
        _notifier = Substitute.For<IStartEventSubscriptionNotifierGrain>();
        _grainFactory.GetGrain<IMessageStartEventListenerGrain>("order-placed", Arg.Any<string?>()).Returns(_listener);
        _grainFactory.GetGrain<IStartEventSubscriptionNotifierGrain>(0, Arg.Any<string?>()).Returns(_notifier);
        _listener.GetProcessKeys().Returns(_ => ValueTask.FromResult(new List<string> { "order-process" }));
        _notifier.Subscribe(Arg.Any<IStartEventSubscriptionObserver>()).Returns(Task.FromResult(true));
        _time = new ManualTime();
        _cache = new StartEventSubscriptionCache(_grainFactory, _time, NullLogger<StartEventSubscriptionCache>.Instance);
    }

    [TestMethod]
    public async Task Lookups_WithinTheLifetime_ReadTheRegistrationsOnce()
    {
        // Act
        var first = await _cache.GetMessageStartProcesses("order-placed");
        _time.Advance(StartEventSubscriptionCache.EntryLifetime - TimeSpan.FromSeconds(1));
        var second = await _cache.GetMessageStartProcesses("order-placed");

        // Assert
        CollectionAssert.AreEqual(new[] { "order-process" }, first.ToList());
        CollectionAssert.AreEqual(new[] { "order-process" }, second.ToList());
        await _listener.Received(1).GetProcessKeys();
    }

    [TestMethod]
    public async Task ConcurrentMisses_ShareOneRead()
    {
        // Arrange
        var load = new TaskCompletionSource<List<string>>();
        _listener.GetProcessKeys().Returns(_ => new ValueTask<List<string>>(load.Task));

        // Act
        var lookups = Enumerable.Range(0, 5).Select(_ => _cache.GetMessageStartProcesses("order-placed").AsTask()).ToList();
        load.SetResult(["order-process"]);
        await Task.WhenAll(lookups);

        // Assert
        await _listener.Received(1).GetProcessKeys();
    }

    [TestMethod]
    public async Task Lookup_AfterTheLifetime_ReadsAgain()
    {
        // Arrange — the bound when a host misses a change notification
        await _cache.GetMessageStartProcesses("order-placed");
        _listener.GetProcessKeys().Returns(_ => ValueTask.FromResult(new List<string> { "order-process", "audit-process" }));

        // Act
        _time.Advance(StartEventSubscriptionCache.EntryLifetime);
        var processes = await _cache.GetMessageStartProcesses("order-placed");

        // Assert
        CollectionAssert.AreEqual(new[] { "order-process", "audit-process" }, processes.ToList());
        await _listener.Received(2).GetProcessKeys();
    }

    [TestMethod]
    public async Task ChangeNotification_DropsTheCachedEntries()
    {
        // Arrange
        await _cache.GetMessageStartProcesses("order-placed");
        _listener.GetProcessKeys().Returns(_ => ValueTask.FromResult(new List<string>()));

        // Act
        await ((IStartEventSubscriptionObserver)_cache).SubscriptionsChanged();
        var processes = await _cache.GetMessageStartProcesses("order-placed");

        // Assert
        Assert.IsEmpty(processes);
        await _listener.Received(2).GetProcessKeys();
    }

    [TestMethod]
    public async Task FirstLookup_SubscribesBeforeReading_AndRenewsOnlyWhenDue()
    {
        // Act + Assert — the first lookup subscribes, then reads
        await _cache.GetMessageStartProcesses("order-placed");
        Received.InOrder(() =>
        {
            _notifier.Subscribe(Arg.Any<IStartEventSubscriptionObserver>());
            _listener.GetProcessKeys();
        });

        // Act + Assert — no renewal until it is due
        await _cache.GetMessageStartProcesses("order-placed");
        _time.Advance(StartEventSubscriptionCache.RenewInterval);
        await _cache.GetMessageStartProcesses("order-placed");
        await _notifier.Received(2).Subscribe(Arg.Any<IStartEventSubscriptionObserver>());
    }

    [TestMethod]
    public async Task UnreachableNotifier_StillServesLookups()
    {
        // Arrange
        _notifier.Subscribe(Arg.Any<IStartEventSubscriptionObserver>()).ThrowsAsync(new TimeoutException("notifier down"));

        // Act
        var processes = await _cache.GetMessageStartProcesses("order-placed");

        // Assert — entries then refresh on expiry only
        CollectionAssert.AreEqual(new[] { "order-process" }, processes.ToList());
    }

    [TestMethod]
    public async Task FailedRead_IsNotCached()
    {
        // Arrange
        _listener.GetProcessKeys().Returns(
            _ => ValueTask.FromException<List<string>>(new InvalidOperationException("storage down")),
            _ => ValueTask.FromResult(new List<string> { "order-process" }));

        // Act
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await _cache.GetMessageStartProcesses("order-placed"));
        var processes = await _cache.GetMessageStartProcesses("order-placed");

        // Assert
        CollectionAssert.AreEqual(new[] { "order-process" }, processes.ToList());
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
