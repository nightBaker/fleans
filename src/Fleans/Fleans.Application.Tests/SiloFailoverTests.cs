using System.Collections.Concurrent;
using System.Diagnostics;
using System.Dynamic;
using Fleans.Application.Grains;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using Microsoft.Extensions.DependencyInjection;
using Orleans.TestingHost;

namespace Fleans.Application.Tests;

/// <summary>
/// #723 — a silo that dies (or is stopped) while it hosts a waiting workflow instance does not lose
/// the instance: the grain reactivates on a surviving silo from the journal, the BPMN timer's
/// reminder fires from the survivor, an external completion reaches the new activation, and the
/// workflow finishes with every activity completed exactly once.
/// <para>
/// The victim is always a secondary silo: the test cluster's membership table lives on the primary,
/// so killing the primary ends the cluster, not a failover (see <see cref="EventStoreDurabilityTests"/>).
/// Reminders use <see cref="SharedReminderTable"/> — state outside every silo, as the Redis and
/// Postgres providers are in production — because the in-memory table is a grain that may die
/// with the victim. Membership runs on Orleans' defaults: an in-process kill drops the victim's
/// connections, so the survivors declare it dead within the first retried call.
/// </para>
/// </summary>
[TestClass]
[DoNotParallelize]
public class SiloFailoverTests : WorkflowTestBase
{
    // The reminder's due time; long enough that it is still pending when the victim goes down.
    private const string TimerDuration = "PT5S";

    protected override void ConfigureCluster(TestClusterBuilder builder)
    {
        builder.Options.InitialSilosCount = 3;
        builder.AddSiloBuilderConfigurator<FailoverSiloConfigurator>();
    }

    [TestCleanup]
    public void ClearReminders() => SharedReminderTable.Clear();

    [TestMethod]
    [DataRow(true, DisplayName = "Silo killed (no shutdown)")]
    [DataRow(false, DisplayName = "Silo stopped gracefully")]
    public async Task Waiting_instance_survives_the_loss_of_its_silo(bool kill)
    {
        // Arrange — start → fork → { approve (task), wait (timer) } → join → end, on a secondary silo.
        // Deployed, so the activation on the survivor can load the definition by its id.
        var definitions = Cluster.GrainFactory.GetGrain<IProcessDefinitionGrain>("silo-failover");
        await definitions.DeployVersion(CreateTaskAndTimerWorkflow(), "<placeholder/>");
        var (instance, victim) = await StartOnSecondarySilo(await definitions.GetLatestDefinition());
        var instanceId = instance.GetPrimaryKey();
        var waiting = await WaitForCondition(instanceId, s =>
            s.ActiveActivities.Any(a => a.ActivityId == "approve") &&
            s.ActiveActivities.Any(a => a.ActivityId == "wait"));
        var approve = waiting.ActiveActivities.Single(a => a.ActivityId == "approve").ActivityInstanceId;
        await WaitForReminder();

        // Act — take the hosting silo down, then complete the task as a client would.
        var clock = Stopwatch.StartNew();
        if (kill)
            await Cluster.KillSiloAsync(victim);
        else
            await Cluster.StopSiloAsync(victim);

        var variables = new ExpandoObject();
        ((IDictionary<string, object?>)variables)["approved"] = true;
        await CallUntilAccepted(() => instance.CompleteActivity("approve", approve, variables));
        var recovery = clock.Elapsed;

        // Assert — the timer fired from a survivor and the workflow finished on it.
        var done = await WaitForCondition(instanceId, s => s.IsCompleted, timeoutMs: 60_000);
        Console.WriteLine($"{(kill ? "Kill" : "Graceful stop")}: instance accepted calls again after " +
                          $"{recovery.TotalSeconds:F1}s, completed after {clock.Elapsed.TotalSeconds:F1}s");

        Assert.IsFalse(done.IsFailed, "The workflow must not fail because its silo went away");
        Assert.AreEqual(1, done.CompletedActivities.Count(a => a.ActivityId == "approve"),
            "The task must complete exactly once");
        Assert.AreEqual(1, done.CompletedActivities.Count(a => a.ActivityId == "wait"),
            "The timer must fire exactly once");
        Assert.AreEqual("True", done.VariableStates.Single().Variables["approved"],
            "Variables written after failover must be kept");

        // The read model is built from a stream: it catching up proves streams resumed on a survivor.
        await WaitForProjection(instanceId);
    }

    private static WorkflowDefinition CreateTaskAndTimerWorkflow()
    {
        var start = new StartEvent("start");
        var fork = new ParallelGateway("fork", IsFork: true);
        var approve = new TaskActivity("approve");
        var wait = new TimerIntermediateCatchEvent("wait", new TimerDefinition(TimerType.Duration, TimerDuration));
        var join = new ParallelGateway("join", IsFork: false);
        var end = new EndEvent("end");
        return new WorkflowDefinition
        {
            WorkflowId = "silo-failover",
            Activities = [start, fork, approve, wait, join, end],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, fork),
                new SequenceFlow("f2", fork, approve),
                new SequenceFlow("f3", fork, wait),
                new SequenceFlow("f4", approve, join),
                new SequenceFlow("f5", wait, join),
                new SequenceFlow("f6", join, end)
            ]
        };
    }

    /// <summary>
    /// Starts instances until one is placed on a secondary silo; the others keep waiting, harmlessly.
    /// Placement round-robins over the core silos it knows, and its silo list is cached for
    /// <c>CachedSiloHosts.Lifetime</c>, so silos that joined just now are picked up after that.
    /// </summary>
    private async Task<(IWorkflowInstanceGrain Instance, SiloHandle Silo)> StartOnSecondarySilo(
        IWorkflowDefinition workflow)
    {
        var management = Cluster.GrainFactory.GetGrain<IManagementGrain>(0);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while ((await management.GetHosts(onlyActive: true)).Count < Cluster.Silos.Count)
        {
            Assert.IsTrue(DateTime.UtcNow < deadline, "The cluster did not reach its initial silo count");
            await Task.Delay(100);
        }

        while (DateTime.UtcNow < deadline)
        {
            var instance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
            await instance.StartWorkflow(workflow);
            var host = await HostOf(instance.GetPrimaryKey());
            var secondary = Cluster.SecondarySilos.FirstOrDefault(s => s.SiloAddress.Equals(host));
            if (secondary is not null)
                return (instance, secondary);
            await Task.Delay(500);
        }
        Assert.Fail("No instance was placed on a secondary silo");
        return default;
    }

    private async Task<SiloAddress?> HostOf(Guid instanceId)
    {
        // Matched on the grain id: the type filter expects "Namespace.Type,Assembly", not FullName.
        var key = "workflowinstance/" + instanceId.ToString("N");
        var statistics = await Cluster.GrainFactory.GetGrain<IManagementGrain>(0)
            .GetDetailedGrainStatistics();
        return statistics.FirstOrDefault(s => s.GrainId.ToString() == key)?.SiloAddress;
    }

    /// <summary>The timer callback registers its reminder after the instance call returns.</summary>
    private static async Task WaitForReminder()
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (SharedReminderTable.Count == 0)
        {
            Assert.IsTrue(DateTime.UtcNow < deadline, "The timer's reminder was never registered");
            await Task.Delay(25);
        }
    }

    /// <summary>
    /// Until membership declares the victim dead, calls to its grains fail; a client retries.
    /// Each attempt is safe to repeat: a completion carrying the activity instance id is ignored
    /// once that instance is no longer active.
    /// </summary>
    private static async Task CallUntilAccepted(Func<Task> call)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (true)
        {
            try
            {
                await call();
                return;
            }
            catch (Exception) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(250);
            }
        }
    }

    private class FailoverSiloConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder hostBuilder) =>
            hostBuilder
                .ConfigureServices(services => services.AddSingleton<IReminderTable, SharedReminderTable>());
    }

    /// <summary>
    /// A reminder table shared by every silo of the in-process cluster, so it outlives any one of them.
    /// </summary>
    private sealed class SharedReminderTable : IReminderTable
    {
        private static readonly ConcurrentDictionary<(GrainId, string), ReminderEntry> Rows = new();

        public static int Count => Rows.Count;

        public static void Clear() => Rows.Clear();

        public Task<ReminderTableData> ReadRows(GrainId grainId) =>
            Task.FromResult(new ReminderTableData(Rows.Values.Where(r => r.GrainId.Equals(grainId)).Select(Copy)));

        public Task<ReminderTableData> ReadRows(uint begin, uint end) =>
            Task.FromResult(new ReminderTableData(Rows.Values.Where(r => InRange(r.GrainId.GetUniformHashCode(), begin, end)).Select(Copy)));

        public Task<ReminderEntry?> ReadRow(GrainId grainId, string reminderName) =>
            Task.FromResult(Rows.TryGetValue((grainId, reminderName), out var row) ? Copy(row) : null);

        public Task<string> UpsertRow(ReminderEntry entry)
        {
            var stored = Copy(entry);
            stored.ETag = Guid.NewGuid().ToString("N");
            Rows[(entry.GrainId, entry.ReminderName)] = stored;
            return Task.FromResult(stored.ETag);
        }

        public Task<bool> RemoveRow(GrainId grainId, string reminderName, string eTag)
        {
            var key = (grainId, reminderName);
            return Task.FromResult(Rows.TryGetValue(key, out var row) && row.ETag == eTag &&
                                   Rows.TryRemove(new KeyValuePair<(GrainId, string), ReminderEntry>(key, row)));
        }

        public Task TestOnlyClearTable()
        {
            Clear();
            return Task.CompletedTask;
        }

        // The ring range (begin, end] wraps around when begin >= end.
        private static bool InRange(uint hash, uint begin, uint end) =>
            begin < end ? hash > begin && hash <= end : hash > begin || hash <= end;

        private static ReminderEntry Copy(ReminderEntry r) => new()
        {
            GrainId = r.GrainId,
            ReminderName = r.ReminderName,
            StartAt = r.StartAt,
            Period = r.Period,
            ETag = r.ETag
        };
    }
}
