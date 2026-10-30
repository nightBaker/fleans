using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Fleans.ServiceDefaults.Clustering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Placement;
using Orleans.Runtime;

namespace Fleans.ServiceDefaults.Tests;

/// <summary>
/// #902: a silo that joins a fresh cluster — its first membership read finds the table empty, its
/// next one finds another silo but not itself — is reported dead to itself by Orleans 10.4. Without
/// the guard its consistent ring stops routing to itself, so reminders it registers for grains it owns
/// land on a neighbour, which (since Orleans 10.4) only stores them: they fire at the owner's next
/// reminder-list refresh, minutes late.
/// <para>
/// The cluster reproduces that start deterministically: two in-process silos on localhost clustering,
/// the primary held before it joins until the second silo has read the (still empty) table, and the
/// second held until the primary is up. Reminders are armed by grains activated on the second silo.
/// </para>
/// <para>
/// The control test asserts the misrouted registrations themselves, not reminders that fail to fire:
/// the joiner's reminder service loads its range in the background after start, and when that load
/// lands after the reminders are stored it fires them on time anyway (#954).
/// </para>
/// </summary>
[TestClass]
[DoNotParallelize]
public class ConsistentRingSelfStatusGuardTests
{
    private const int Grains = 32;

    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task Reminders_from_a_silo_that_joined_a_fresh_cluster_fire_on_time_with_the_guard()
    {
        await using var cluster = await FreshTwoSiloCluster.StartAsync(guard: true);

        var keys = await ArmRemindersFromJoiner(cluster);

        var fired = await WaitForFired(keys, TimeSpan.FromSeconds(30));
        Assert.HasCount(Grains, fired, $"{Grains - fired.Count} of {Grains} 1-second reminders did not fire within 30 s.");
    }

    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task Without_the_guard_the_joining_silo_misroutes_its_own_reminders()
    {
        // Control: proves the cluster above reproduces the Orleans bug. When an Orleans upgrade makes
        // this fail, the bug is fixed upstream — remove ConsistentRingSelfStatusGuard.
        await using var cluster = await FreshTwoSiloCluster.StartAsync(guard: false);

        await ArmRemindersFromJoiner(cluster);

        Assert.IsGreaterThan(0, cluster.MisroutedRegistrations.Count,
            "No reminder registration from the joining silo reached a silo that does not own it. Either Orleans " +
            "no longer misroutes them — drop the #902 guard — or it no longer logs LocalReminderService EventId " +
            $"{MisroutedRegistrationLog.NotInMyRangeEventId} for them; check which.");
    }

    private static async Task<List<string>> ArmRemindersFromJoiner(FreshTwoSiloCluster cluster)
    {
        var factory = cluster.Joiner.Services.GetRequiredService<IGrainFactory>();
        var keys = Enumerable.Range(0, Grains).Select(i => $"{Guid.NewGuid():N}-{i}").ToList();
        foreach (var key in keys)
            await factory.GetGrain<IRingGuardReminderGrain>(key).Arm();
        return keys;
    }

    private static async Task<List<string>> WaitForFired(List<string> keys, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && keys.Any(k => !RingGuardReminderGrain.Fired.ContainsKey(k)))
            await Task.Delay(250);
        return keys.Where(RingGuardReminderGrain.Fired.ContainsKey).ToList();
    }

    private sealed class FreshTwoSiloCluster : IAsyncDisposable
    {
        private readonly IHost _primary;

        private FreshTwoSiloCluster(IHost primary, IHost joiner, MisroutedRegistrationLog misrouted)
        {
            _primary = primary;
            Joiner = joiner;
            MisroutedRegistrations = misrouted;
        }

        public IHost Joiner { get; }

        public MisroutedRegistrationLog MisroutedRegistrations { get; }

        public static async Task<FreshTwoSiloCluster> StartAsync(bool guard)
        {
            var primaryPort = FreePort();
            var joinerReadTable = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var primaryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var misrouted = new MisroutedRegistrationLog();

            // The primary joins only after the joiner's initial membership read (empty table) ...
            var primary = BuildSilo(primaryPort, primaryPort, guard, misrouted, () => joinerReadTable.Task);
            // ... and the joiner writes itself only after the primary is in the table.
            var joiner = BuildSilo(FreePort(), primaryPort, guard, misrouted, () =>
            {
                joinerReadTable.TrySetResult();
                return primaryStarted.Task;
            });

            var primaryStart = primary.StartAsync();
            var joinerStart = joiner.StartAsync();
            await primaryStart;
            primaryStarted.TrySetResult();
            await joinerStart;
            return new FreshTwoSiloCluster(primary, joiner, misrouted);
        }

        private static IHost BuildSilo(
            int siloPort, int primaryPort, bool guard, MisroutedRegistrationLog misrouted, Func<Task> beforeJoining) =>
            Host.CreateDefaultBuilder()
                .ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Warning).AddProvider(misrouted))
                .UseOrleans(silo =>
                {
                    silo.UseLocalhostClustering(siloPort, FreePort(), new IPEndPoint(IPAddress.Loopback, primaryPort));
                    silo.UseInMemoryReminderService();
                    // A misrouted reminder would otherwise fire at the next refresh, inside the test window.
                    silo.Configure<ReminderOptions>(o => o.RefreshReminderListPeriod = TimeSpan.FromMinutes(10));
                    silo.ConfigureServices(services => services.AddSingleton<ILifecycleParticipant<ISiloLifecycle>>(
                        new GateBeforeJoining(beforeJoining)));
                    if (guard)
                        silo.AddConsistentRingSelfStatusGuard();
                })
                .Build();

        public async ValueTask DisposeAsync()
        {
            foreach (var host in new[] { Joiner, _primary })
            {
                await host.StopAsync();
                host.Dispose();
            }
        }

        private static int FreePort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
    }

    /// <summary>
    /// Counts reminder registrations that reached a silo whose ring says it does not own the reminder.
    /// The silo still stores such a reminder but does not fire it, which is what #902 makes late.
    /// </summary>
    private sealed class MisroutedRegistrationLog : ILoggerProvider
    {
        // Orleans' LocalReminderService: "I shouldn't have received request 'RegisterReminder' for … It is not in my responsibility range".
        public const int NotInMyRangeEventId = 102910;

        private int _count;

        public int Count => Volatile.Read(ref _count);

        public ILogger CreateLogger(string categoryName) => new Logger(this);

        public void Dispose() { }

        private sealed class Logger(MisroutedRegistrationLog log) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (eventId.Id == NotInMyRangeEventId && formatter(state, exception).Contains("'RegisterReminder'"))
                    Interlocked.Increment(ref log._count);
            }
        }
    }

    /// <summary>
    /// Runs after the silo's initial membership read (<c>RuntimeGrainServices</c>) and before it writes
    /// itself to the table (<c>AfterRuntimeGrainServices</c>).
    /// </summary>
    private sealed class GateBeforeJoining(Func<Task> gate) : ILifecycleParticipant<ISiloLifecycle>
    {
        public void Participate(ISiloLifecycle lifecycle) =>
            lifecycle.Subscribe(nameof(GateBeforeJoining), ServiceLifecycleStage.RuntimeGrainServices + 50, _ => gate());
    }
}

public interface IRingGuardReminderGrain : IGrainWithStringKey
{
    Task Arm();
}

[PreferLocalPlacement]
public sealed class RingGuardReminderGrain : Grain, IRingGuardReminderGrain, IRemindable
{
    internal static readonly ConcurrentDictionary<string, DateTime> Fired = new();

    public async Task Arm() =>
        await this.RegisterOrUpdateReminder("ring-guard", TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1));

    public async Task ReceiveReminder(string reminderName, TickStatus status)
    {
        Fired.TryAdd(this.GetPrimaryKeyString(), DateTime.UtcNow);
        if (await this.GetReminder(reminderName) is { } reminder)
            await this.UnregisterReminder(reminder);
    }
}
