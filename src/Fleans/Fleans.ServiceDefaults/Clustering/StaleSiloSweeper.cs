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
/// dead each other <c>Active</c> silo whose heartbeat is older than
/// <c>IAmAliveTablePublishTimeout × NumMissedTableIAmAliveLimit</c> (90 s by default) — the window after
/// which Orleans itself stops counting a silo as alive. A live silo writes its heartbeat every
/// <c>IAmAliveTablePublishTimeout</c> (30 s), so only silos that are gone, or cut off from the table for
/// that long, are removed. Orleans' retry loop rereads the table and the join goes through. The sweeper
/// stops once the silo becomes active; from then on Orleans' own probes and votes take over.
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
        try { await _loop!; }
        catch (OperationCanceledException) { }
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

    /// <summary>Declares every stale <c>Active</c> peer dead; returns how many it declared.</summary>
    internal async Task<int> SweepOnceAsync(CancellationToken cancellationToken = default)
    {
        var options = membershipOptions.Value;
        var allowedMiss = options.IAmAliveTablePublishTimeout * options.NumMissedTableIAmAliveLimit;
        var now = timeProvider.GetUtcNow().UtcDateTime;

        var table = await membershipTable.ReadAllAsync(cancellationToken);
        var version = table.Version;
        var declared = 0;
        foreach (var (entry, etag) in table.Members)
        {
            if (entry.Status != SiloStatus.Active || entry.SiloAddress.Equals(localSilo.SiloAddress))
                continue;

            var lastAlive = entry.IAmAliveTime > entry.StartTime ? entry.IAmAliveTime : entry.StartTime;
            if (now - lastAlive <= allowedMiss)
                continue;

            entry.Status = SiloStatus.Dead;
            version = version.Next();
            if (!await membershipTable.UpdateRowAsync(entry, etag, version, cancellationToken))
                break; // the table moved under us; the next sweep reads it again

            declared++;
            LogDeclaredStaleSiloDead(entry.SiloAddress, entry.SiloName, lastAlive, allowedMiss);
        }
        return declared;
    }

    [LoggerMessage(EventId = 11410, Level = LogLevel.Warning,
        Message = "Declared stale silo {Silo} ('{SiloName}') dead: it is Active in the membership table but its last " +
                  "heartbeat was at {LastAlive:O}, more than {AllowedMiss} ago, so this silo could never pass Orleans' " +
                  "join connectivity check (#1098).")]
    private partial void LogDeclaredStaleSiloDead(SiloAddress silo, string siloName, DateTime lastAlive, TimeSpan allowedMiss);

    [LoggerMessage(EventId = 11411, Level = LogLevel.Warning,
        Message = "Stale-silo sweep failed; retrying at the next sweep (#1098).")]
    private partial void LogSweepFailed(Exception exception);
}
