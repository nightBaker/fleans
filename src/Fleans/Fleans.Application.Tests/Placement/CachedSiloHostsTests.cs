using Fleans.Application.Placement;
using Orleans.Runtime;

namespace Fleans.Application.Tests.Placement;

[TestClass]
public class CachedSiloHostsTests
{
    private sealed class ManualClock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public void Advance(TimeSpan by) => _ticks += by.Ticks;
    }

    private static MembershipEntry[] Hosts(params string[] names) =>
        names.Select(n => new MembershipEntry { SiloName = n }).ToArray();

    [TestMethod]
    public async Task Within_the_lifetime_the_hosts_are_fetched_once()
    {
        // Arrange
        var clock = new ManualClock();
        var fetches = 0;
        var cache = new CachedSiloHosts(() => { fetches++; return Task.FromResult(Hosts("core-1")); }, clock);

        // Act
        await cache.GetAsync();
        clock.Advance(CachedSiloHosts.Lifetime - TimeSpan.FromMilliseconds(1));
        var hosts = await cache.GetAsync();

        // Assert
        Assert.AreEqual(1, fetches);
        Assert.AreEqual("core-1", hosts.Single().SiloName);
    }

    [TestMethod]
    public async Task After_the_lifetime_the_hosts_are_fetched_again()
    {
        // Arrange
        var clock = new ManualClock();
        var fetches = 0;
        var cache = new CachedSiloHosts(() => Task.FromResult(Hosts($"core-{++fetches}")), clock);
        await cache.GetAsync();

        // Act
        clock.Advance(CachedSiloHosts.Lifetime);
        var hosts = await cache.GetAsync();

        // Assert
        Assert.AreEqual(2, fetches);
        Assert.AreEqual("core-2", hosts.Single().SiloName);
    }

    [TestMethod]
    public async Task ForceRefresh_fetches_even_when_the_entry_is_fresh()
    {
        // Arrange
        var fetches = 0;
        var cache = new CachedSiloHosts(() => Task.FromResult(Hosts($"core-{++fetches}")), new ManualClock());
        await cache.GetAsync();

        // Act
        var hosts = await cache.GetAsync(forceRefresh: true);

        // Assert
        Assert.AreEqual(2, fetches);
        Assert.AreEqual("core-2", hosts.Single().SiloName);
    }

    [TestMethod]
    public async Task Concurrent_callers_share_one_fetch()
    {
        // Arrange
        var fetches = 0;
        var gate = new TaskCompletionSource<MembershipEntry[]>();
        var cache = new CachedSiloHosts(() => { fetches++; return gate.Task; }, new ManualClock());

        // Act
        var first = cache.GetAsync();
        var second = cache.GetAsync();
        gate.SetResult(Hosts("core-1"));
        await Task.WhenAll(first, second);

        // Assert
        Assert.AreEqual(1, fetches);
    }

    [TestMethod]
    public async Task A_failed_fetch_is_not_cached()
    {
        // Arrange
        var fetches = 0;
        var cache = new CachedSiloHosts(() =>
            ++fetches == 1
                ? Task.FromException<MembershipEntry[]>(new TimeoutException("membership table"))
                : Task.FromResult(Hosts("core-1")), new ManualClock());

        // Act
        await Assert.ThrowsExactlyAsync<TimeoutException>(() => cache.GetAsync());
        var hosts = await cache.GetAsync();

        // Assert
        Assert.AreEqual(2, fetches);
        Assert.AreEqual("core-1", hosts.Single().SiloName);
    }
}
