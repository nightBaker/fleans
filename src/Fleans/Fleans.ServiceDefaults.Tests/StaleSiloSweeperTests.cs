using System.Net;
using Fleans.ServiceDefaults.Clustering;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orleans;
using Orleans.Configuration;
using Orleans.Runtime;

namespace Fleans.ServiceDefaults.Tests;

/// <summary>
/// A silo that is gone but still Active in a persistent membership table must not block the next
/// silo's join forever (#1098).
/// </summary>
[TestClass]
public class StaleSiloSweeperTests
{
    private static readonly DateTime Now = new(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);

    // Defaults: 30 s heartbeat × 3 missed = 90 s.
    private static readonly TimeSpan AllowedMiss = TimeSpan.FromSeconds(90);

    private readonly SiloAddress _self = Silo("10.0.0.1", 3);
    private readonly IMembershipTable _table = Substitute.For<IMembershipTable>();

    [TestMethod]
    public async Task Declares_an_Active_silo_with_a_stale_heartbeat_dead()
    {
        var stale = Entry(Silo("10.0.0.9", 1), SiloStatus.Active, lastAlive: Now - AllowedMiss - TimeSpan.FromSeconds(1));
        GivenTable(5, (stale, "etag-stale"));
        _table.UpdateRowAsync(default!, default!, default!, default).ReturnsForAnyArgs(true);

        var declared = await Sweeper().SweepOnceAsync();

        Assert.AreEqual(1, declared);
        await _table.Received(1).UpdateRowAsync(
            Arg.Is<MembershipEntry>(e => e.SiloAddress.Equals(stale.SiloAddress) && e.Status == SiloStatus.Dead),
            "etag-stale",
            Arg.Is<TableVersion>(v => v.Version == 6),
            Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task Leaves_live_silos_itself_and_non_Active_entries_alone()
    {
        var old = Now - AllowedMiss - TimeSpan.FromMinutes(10);
        GivenTable(5,
            (Entry(Silo("10.0.0.2", 1), SiloStatus.Active, lastAlive: Now - AllowedMiss), "live"),
            (Entry(_self, SiloStatus.Active, lastAlive: old), "self"),
            (Entry(Silo("10.0.0.3", 1), SiloStatus.Joining, lastAlive: old), "joining"),
            (Entry(Silo("10.0.0.4", 1), SiloStatus.Dead, lastAlive: old), "dead"));

        var declared = await Sweeper().SweepOnceAsync();

        Assert.AreEqual(0, declared);
        await _table.DidNotReceiveWithAnyArgs().UpdateRowAsync(default!, default!, default!, default);
    }

    [TestMethod]
    public async Task A_silo_that_never_wrote_a_heartbeat_counts_from_its_start_time()
    {
        var started = Entry(Silo("10.0.0.9", 1), SiloStatus.Active, lastAlive: default);
        started.StartTime = Now - TimeSpan.FromSeconds(10);
        GivenTable(5, (started, "etag"));

        Assert.AreEqual(0, await Sweeper().SweepOnceAsync());
    }

    [TestMethod]
    public async Task Stops_at_a_lost_write_and_leaves_the_rest_to_the_next_sweep()
    {
        var old = Now - TimeSpan.FromMinutes(10);
        GivenTable(5,
            (Entry(Silo("10.0.0.8", 1), SiloStatus.Active, lastAlive: old), "a"),
            (Entry(Silo("10.0.0.9", 1), SiloStatus.Active, lastAlive: old), "b"));
        _table.UpdateRowAsync(default!, default!, default!, default).ReturnsForAnyArgs(false);

        var declared = await Sweeper().SweepOnceAsync();

        Assert.AreEqual(0, declared);
        await _table.ReceivedWithAnyArgs(1).UpdateRowAsync(default!, default!, default!, default);
    }

    [TestMethod]
    public async Task The_stale_window_follows_the_configured_heartbeat()
    {
        var options = new ClusterMembershipOptions { IAmAliveTablePublishTimeout = TimeSpan.FromSeconds(10) };
        GivenTable(5, (Entry(Silo("10.0.0.9", 1), SiloStatus.Active, lastAlive: Now - TimeSpan.FromSeconds(31)), "etag"));
        _table.UpdateRowAsync(default!, default!, default!, default).ReturnsForAnyArgs(true);

        Assert.AreEqual(1, await Sweeper(options).SweepOnceAsync());
    }

    private StaleSiloSweeper Sweeper(ClusterMembershipOptions? options = null)
    {
        var local = Substitute.For<ILocalSiloDetails>();
        local.SiloAddress.Returns(_self);
        return new StaleSiloSweeper(_table, local, Options.Create(options ?? new ClusterMembershipOptions()),
            new FixedTimeProvider(Now), NullLogger<StaleSiloSweeper>.Instance);
    }

    private void GivenTable(int version, params (MembershipEntry Entry, string ETag)[] rows)
        => _table.ReadAllAsync(Arg.Any<CancellationToken>()).Returns(new MembershipTableData(
            rows.Select(r => Tuple.Create(r.Entry, r.ETag)).ToList(),
            new TableVersion(version, $"v{version}")));

    private static MembershipEntry Entry(SiloAddress silo, SiloStatus status, DateTime lastAlive) => new()
    {
        SiloAddress = silo,
        SiloName = $"combined-{silo.Endpoint.Address}",
        Status = status,
        IAmAliveTime = lastAlive,
        StartTime = lastAlive == default ? default : lastAlive - TimeSpan.FromMinutes(1),
    };

    private static SiloAddress Silo(string ip, int generation)
        => SiloAddress.New(new IPEndPoint(IPAddress.Parse(ip), 11111), generation);

    private sealed class FixedTimeProvider(DateTime now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(now);
    }
}
