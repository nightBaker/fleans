using System.Net;
using Fleans.ServiceDefaults.Clustering;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Orleans;
using Orleans.Configuration;
using Orleans.Runtime;

namespace Fleans.ServiceDefaults.Tests;

/// <summary>
/// A silo that is gone but still Active in a persistent membership table must not block the next
/// silo's join forever (#1098) — and a live silo must never be declared dead by a joining one.
/// </summary>
[TestClass]
public class StaleSiloSweeperTests
{
    // Defaults: 30 s heartbeat × 3 missed = 90 s.
    private static readonly TimeSpan AllowedMiss = TimeSpan.FromSeconds(90);
    private static readonly DateTime LongAgo = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly SiloAddress _self = Silo("10.0.0.1", 3);
    private readonly IMembershipTable _table = Substitute.For<IMembershipTable>();
    private readonly ManualClock _clock = new();

    [TestInitialize]
    public void AllWritesSucceed()
        => _table.UpdateRowAsync(default!, default!, default!, default).ReturnsForAnyArgs(true);

    [TestMethod]
    public async Task Declares_an_Active_silo_dead_once_its_heartbeat_stays_unchanged_past_the_window()
    {
        var sweeper = Sweeper();
        var stale = Entry(Silo("10.0.0.9", 1), SiloStatus.Active, LongAgo);
        GivenTable(5, (stale, "etag-stale"));

        Assert.AreEqual(0, await sweeper.SweepOnceAsync(), "the first read only starts watching");
        _clock.Advance(AllowedMiss);
        Assert.AreEqual(0, await sweeper.SweepOnceAsync(), "exactly the window is not yet stale");
        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.AreEqual(1, await sweeper.SweepOnceAsync());

        await _table.Received(1).UpdateRowAsync(
            Arg.Is<MembershipEntry>(e => e.SiloAddress.Equals(stale.SiloAddress) && e.Status == SiloStatus.Dead),
            "etag-stale",
            Arg.Is<TableVersion>(v => v.Version == 6),
            Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task An_old_heartbeat_timestamp_alone_is_not_enough()
    {
        // Clock skew: the peer's IAmAliveTime looks ancient to this host, but nothing has been observed yet.
        GivenTable(5, (Entry(Silo("10.0.0.9", 1), SiloStatus.Active, LongAgo), "etag"));

        Assert.AreEqual(0, await Sweeper().SweepOnceAsync());
        await _table.DidNotReceiveWithAnyArgs().UpdateRowAsync(default!, default!, default!, default);
    }

    [TestMethod]
    public async Task A_live_silo_that_keeps_writing_its_heartbeat_is_left_alone()
    {
        var sweeper = Sweeper();
        var silo = Silo("10.0.0.2", 1);
        for (var i = 0; i < 10; i++)
        {
            GivenTable(5, (Entry(silo, SiloStatus.Active, LongAgo.AddSeconds(30 * i)), "etag"));
            await sweeper.SweepOnceAsync();
            _clock.Advance(TimeSpan.FromSeconds(30));
        }

        await _table.DidNotReceiveWithAnyArgs().UpdateRowAsync(default!, default!, default!, default);
    }

    [TestMethod]
    public async Task A_failed_table_read_restarts_the_watch()
    {
        // During a table outage live silos cannot write their heartbeat either.
        var sweeper = Sweeper();
        var row = (Entry(Silo("10.0.0.9", 1), SiloStatus.Active, LongAgo), "etag");
        GivenTable(5, row);
        await sweeper.SweepOnceAsync();

        _table.ReadAllAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new TimeoutException("table down"));
        _clock.Advance(TimeSpan.FromMinutes(5));
        await Assert.ThrowsExactlyAsync<TimeoutException>(() => sweeper.SweepOnceAsync());

        GivenTable(5, row);
        Assert.AreEqual(0, await sweeper.SweepOnceAsync(), "table back: watching starts again");
        await _table.DidNotReceiveWithAnyArgs().UpdateRowAsync(default!, default!, default!, default);
    }

    [TestMethod]
    public async Task Leaves_itself_and_non_Active_entries_alone()
    {
        var sweeper = Sweeper();
        GivenTable(5,
            (Entry(_self, SiloStatus.Active, LongAgo), "self"),
            (Entry(Silo("10.0.0.3", 1), SiloStatus.Joining, LongAgo), "joining"),
            (Entry(Silo("10.0.0.4", 1), SiloStatus.Dead, LongAgo), "dead"));

        await sweeper.SweepOnceAsync();
        _clock.Advance(TimeSpan.FromMinutes(10));
        Assert.AreEqual(0, await sweeper.SweepOnceAsync());

        await _table.DidNotReceiveWithAnyArgs().UpdateRowAsync(default!, default!, default!, default);
    }

    [TestMethod]
    public async Task Stops_at_a_lost_write_and_leaves_the_rest_to_the_next_sweep()
    {
        var sweeper = Sweeper();
        GivenTable(5,
            (Entry(Silo("10.0.0.8", 1), SiloStatus.Active, LongAgo), "a"),
            (Entry(Silo("10.0.0.9", 1), SiloStatus.Active, LongAgo), "b"));
        _table.UpdateRowAsync(default!, default!, default!, default).ReturnsForAnyArgs(false);

        await sweeper.SweepOnceAsync();
        _clock.Advance(TimeSpan.FromMinutes(2));
        Assert.AreEqual(0, await sweeper.SweepOnceAsync());

        await _table.ReceivedWithAnyArgs(1).UpdateRowAsync(default!, default!, default!, default);
    }

    [TestMethod]
    public async Task The_window_follows_the_configured_heartbeat()
    {
        var sweeper = Sweeper(new ClusterMembershipOptions { IAmAliveTablePublishTimeout = TimeSpan.FromSeconds(10) });
        GivenTable(5, (Entry(Silo("10.0.0.9", 1), SiloStatus.Active, LongAgo), "etag"));

        await sweeper.SweepOnceAsync();
        _clock.Advance(TimeSpan.FromSeconds(31));
        Assert.AreEqual(1, await sweeper.SweepOnceAsync());
    }

    private StaleSiloSweeper Sweeper(ClusterMembershipOptions? options = null)
    {
        var local = Substitute.For<ILocalSiloDetails>();
        local.SiloAddress.Returns(_self);
        return new StaleSiloSweeper(_table, local, Options.Create(options ?? new ClusterMembershipOptions()),
            _clock, NullLogger<StaleSiloSweeper>.Instance);
    }

    private void GivenTable(int version, params (MembershipEntry Entry, string ETag)[] rows)
        => _table.ReadAllAsync(Arg.Any<CancellationToken>()).Returns(_ => new MembershipTableData(
            rows.Select(r => Tuple.Create(Copy(r.Entry), r.ETag)).ToList(),
            new TableVersion(version, $"v{version}")));

    // Each read returns fresh entries, as a real table does (the sweeper mutates the one it declares dead).
    private static MembershipEntry Copy(MembershipEntry e) => Entry(e.SiloAddress, e.Status, e.IAmAliveTime);

    private static MembershipEntry Entry(SiloAddress silo, SiloStatus status, DateTime iAmAlive) => new()
    {
        SiloAddress = silo,
        SiloName = $"combined-{silo.Endpoint.Address}",
        Status = status,
        IAmAliveTime = iAmAlive,
        StartTime = iAmAlive,
    };

    private static SiloAddress Silo(string ip, int generation)
        => SiloAddress.New(new IPEndPoint(IPAddress.Parse(ip), 11111), generation);

    private sealed class ManualClock : TimeProvider
    {
        private long _ticks;
        public void Advance(TimeSpan by) => _ticks += by.Ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
    }
}
