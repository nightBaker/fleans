using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime;

namespace Fleans.ServiceDefaults.Clustering;

/// <summary>
/// Lets a silo join when the membership table still lists a silo that is gone as <c>Active</c> (#1098).
/// <para>
/// Before a silo becomes active, Orleans 10.4 probes every <c>Active</c> silo in the table and retries
/// for <c>MaxJoinAttemptTime</c> (5 min) until all of them answer. It has no switch for this. A silo that
/// crashed, or whose stop ran out of time before it left the table, stays <c>Active</c> in a persistent
/// table (Postgres, Redis). Only active silos vote a silo dead, so when the whole cluster went down,
/// as with a single <c>Combined</c> silo, nobody can remove it and every restart fails the check.
/// </para>
/// <para>
/// While that check runs, this sweeper reads the table every <see cref="SweepPeriod"/> and declares
/// dead each other <c>Active</c> silo whose heartbeat (<c>IAmAliveTime</c>) it has watched stay
/// unchanged for longer than <c>IAmAliveTablePublishTimeout × NumMissedTableIAmAliveLimit</c> (90 s by
/// default). A live silo rewrites its heartbeat every <c>IAmAliveTablePublishTimeout</c> (30 s).
/// The time is measured on this silo's monotonic clock, never compared with timestamps other hosts
/// wrote, so clock skew cannot evict a live silo; and a failed table read restarts every observation,
/// so a table outage (during which live silos cannot write their heartbeat) cannot either.
/// Orleans' retry loop rereads the table and the join goes through. The sweeper stops once the silo
/// becomes active; from then on Orleans' own probes and votes take over.
/// </para>
/// </summary>
internal sealed partial class StaleSiloSweeper(
    IMembershipTable membershipTable,
    ILocalSiloDetails localSilo,
    IOptions<ClusterMembershipOptions> membershipOptions,
    TimeProvider timeProvider,
    ILogger<StaleSiloSweeper> logger) : ILifecycleParticipant<ISiloLifecycle>
{
    internal static readonly TimeSpan SweepPeriod = TimeSpan.FromSeconds(5);

    // Per Active peer: the heartbeat last read and the local timestamp it was first read at.
    private readonly Dictionary<SiloAddress, (DateTime IAmAliveTime, long Since)> _observed = new();

    private CancellationTokenSource? _cancellation;
    private Task? _loop;

    public void Participate(ISiloLifecycle lifecycle)
    {
        // Runs next to MembershipAgent's own observer of this stage, which holds the stage until the
        // connectivity check passes; the sweep therefore runs exactly while the silo waits to join.
        lifecycle.Subscribe(nameof(StaleSiloSweeper), ServiceLifecycleStage.ValidateInitialConnectivity,
            _ => Start(), _ => StopAsync());
        lifecycle.Subscribe(nameof(StaleSiloSweeper), ServiceLifecycleStage.BecomeActive,
            _ => StopAsync(), _ => Task.CompletedTask);
    }

    private Task Start()
    {
        _cancellation = new CancellationTokenSource();
        var token = _cancellation.Token;
        _loop = Task.Run(() => RunAsync(token), CancellationToken.None);
        return Task.CompletedTask;
    }

    private async Task StopAsync()
    {
        if (_cancellation is null) return;
        await _cancellation.CancelAsync();
        try
        {
            await _loop!;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A provider may throw its own error instead of honouring the token; stopping the sweep
            // must never fail the silo's lifecycle stage.
            LogSweepFailed(ex);
        }
        catch (OperationCanceledException)
        {
        }
        _cancellation.Dispose();
        _cancellation = null;
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(SweepPeriod, timeProvider);
        do
        {
            try
            {
                await SweepOnceAsync(cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                LogSweepFailed(ex);
            }
        }
        while (await timer.WaitForNextTickAsync(cancellationToken));
    }

    /// <summary>
    /// Reads the table once and declares dead every Active peer whose heartbeat has not changed for
    /// longer than the allowed miss period; returns how many it declared.
    /// </summary>
    internal async Task<int> SweepOnceAsync(CancellationToken cancellationToken = default)
    {
        var options = membershipOptions.Value;
        var allowedMiss = options.IAmAliveTablePublishTimeout * options.NumMissedTableIAmAliveLimit;

        MembershipTableData table;
        try
        {
            table = await membershipTable.ReadAllAsync(cancellationToken);
        }
        catch
        {
            // While the table is unreachable no silo can write its heartbeat: start watching anew.
            _observed.Clear();
            throw;
        }

        var now = timeProvider.GetTimestamp();
        var version = table.Version;
        var declared = 0;
        var active = new HashSet<SiloAddress>();
        foreach (var (entry, etag) in table.Members)
        {
            if (entry.Status != SiloStatus.Active || entry.SiloAddress.Equals(localSilo.SiloAddress))
                continue;

            active.Add(entry.SiloAddress);
            if (!_observed.TryGetValue(entry.SiloAddress, out var seen) || seen.IAmAliveTime != entry.IAmAliveTime)
            {
                _observed[entry.SiloAddress] = (entry.IAmAliveTime, now);
                continue;
            }

            var unchangedFor = timeProvider.GetElapsedTime(seen.Since, now);
            if (unchangedFor <= allowedMiss)
                continue;

            entry.Status = SiloStatus.Dead;
            version = version.Next();
            if (!await membershipTable.UpdateRowAsync(entry, etag, version, cancellationToken))
                break; // the table moved under us; the next sweep reads it again

            declared++;
            _observed.Remove(entry.SiloAddress);
            LogDeclaredStaleSiloDead(entry.SiloAddress, entry.SiloName, entry.IAmAliveTime, unchangedFor, allowedMiss);
        }

        foreach (var silo in _observed.Keys.Where(s => !active.Contains(s)).ToList())
            _observed.Remove(silo);

        return declared;
    }

    [LoggerMessage(EventId = 11410, Level = LogLevel.Warning,
        Message = "Declared stale silo {Silo} ('{SiloName}') dead: it is Active in the membership table but its " +
                  "heartbeat ({LastAlive:O}) has not changed for {UnchangedFor}, more than {AllowedMiss}, so this " +
                  "silo could never pass Orleans' join connectivity check (#1098).")]
    private partial void LogDeclaredStaleSiloDead(SiloAddress silo, string siloName, DateTime lastAlive, TimeSpan unchangedFor, TimeSpan allowedMiss);

    [LoggerMessage(EventId = 11411, Level = LogLevel.Warning,
        Message = "Stale-silo sweep failed; retrying at the next sweep (#1098).")]
    private partial void LogSweepFailed(Exception exception);
}
