using System.Dynamic;
using Fleans.Application.Grains;
using Fleans.Application.QueryModels;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Events;
using Fleans.Domain.Sequences;
using Fleans.Domain.States;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization;

namespace Fleans.Application.Tests;

/// <summary>
/// Finished history stays out of an instance's snapshots, and its full history is still served
/// (#1050). The test silo's snapshot interval is 1, so pruning runs at every settled append.
/// </summary>
[TestClass]
public class InstanceHistoryPruningTests : WorkflowTestBase
{
    private IEventStore EventStore => GetSiloService<IEventStore>();

    /// <summary>start → task → gateway, whose "true" flow leads back to task. It never ends by itself.</summary>
    private static WorkflowDefinition RootLoop()
    {
        var start = new StartEvent("start");
        var task = new TaskActivity("task");
        var gateway = new ExclusiveGateway("gateway");
        var end = new EndEvent("end");
        return new WorkflowDefinition
        {
            WorkflowId = "root-loop",
            Activities = [start, task, gateway, end],
            SequenceFlows =
            [
                new SequenceFlow("in", start, task),
                new SequenceFlow("toGateway", task, gateway),
                new ConditionalSequenceFlow("again", gateway, task, "true"),
                new ConditionalSequenceFlow("done", gateway, end, "false")
            ]
        };
    }

    /// <summary>A chain of script task + exclusive gateway steps, to an end event.</summary>
    internal static WorkflowDefinition ScriptAndGatewayChain(int steps)
    {
        var start = new StartEvent("start");
        var end = new EndEvent("end");
        var deadEnd = new EndEvent("deadEnd");
        var activities = new List<Activity> { start, end, deadEnd };
        var flows = new List<SequenceFlow>();
        Activity previous = start;
        for (var i = 0; i < steps; i++)
        {
            var script = new ScriptTask($"script{i}", "_context.x = 1");
            var gateway = new ExclusiveGateway($"gateway{i}");
            activities.Add(script);
            activities.Add(gateway);
            flows.Add(previous is ExclusiveGateway
                ? new ConditionalSequenceFlow($"true{i}", previous, script, "true")
                : new SequenceFlow($"in{i}", previous, script));
            flows.Add(new SequenceFlow($"toGateway{i}", script, gateway));
            flows.Add(new ConditionalSequenceFlow($"false{i}", gateway, deadEnd, "false"));
            previous = gateway;
        }
        flows.Add(new ConditionalSequenceFlow("last", previous, end, "true"));
        return new WorkflowDefinition
        {
            WorkflowId = $"chain-{steps}",
            Activities = activities,
            SequenceFlows = flows
        };
    }

    private async Task<IWorkflowInstanceGrain> Start(WorkflowDefinition workflow)
    {
        var grain = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await grain.StartWorkflow(workflow);
        return grain;
    }

    private async Task<IWorkflowInstanceGrain> DeployAndStart(WorkflowDefinition workflow)
    {
        var process = Cluster.GrainFactory.GetGrain<IProcessDefinitionGrain>(workflow.WorkflowId);
        await process.DeployVersion(workflow, "<placeholder/>");
        var grain = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await grain.StartWorkflow(await process.GetLatestDefinition());
        return grain;
    }

    private static async Task Iterate(IWorkflowInstanceGrain grain, int times)
    {
        for (var i = 0; i < times; i++)
            await grain.CompleteActivity("task", new ExpandoObject());
    }

    private static void AssertFullLoopHistory(InstanceStateSnapshot snapshot, int iterations)
    {
        Assert.AreEqual(iterations, snapshot.CompletedActivityIds.Count(id => id == "task"));
        Assert.AreEqual(iterations, snapshot.CompletedActivityIds.Count(id => id == "gateway"));
        Assert.AreEqual(1, snapshot.CompletedActivityIds.Count(id => id == "start"));
        Assert.AreEqual("task", snapshot.ActiveActivityIds.Single());
        Assert.HasCount(iterations * 2, snapshot.ConditionSequences);
    }

    [TestMethod]
    public async Task RootLoop_SnapshotDoesNotGrow_WithTheStepCount()
    {
        // Arrange
        var grain = await Start(RootLoop());
        var instanceId = grain.GetPrimaryKey();
        var serializer = GetSiloService<Serializer>();

        // Act
        await Iterate(grain, 5);
        var (early, earlyVersion) = await EventStore.ReadSnapshotAsync(instanceId);
        await Iterate(grain, 45);
        var (late, lateVersion) = await EventStore.ReadSnapshotAsync(instanceId);

        // Assert — only the waiting task is left; the size differs by nothing but varint widths
        Assert.IsGreaterThan(earlyVersion, lateVersion);
        Assert.AreEqual("task", late!.Entries.Single().ActivityId);
        Assert.IsFalse(late.Entries.Single().IsCompleted);
        Assert.HasCount(0, late.ConditionSequenceStates);
        var earlySize = serializer.SerializeToArray(early!).Length;
        var lateSize = serializer.SerializeToArray(late).Length;
        Assert.IsLessThanOrEqualTo(earlySize + 16, lateSize, $"Snapshot grew from {earlySize} to {lateSize} bytes");
    }

    [TestMethod]
    public async Task RunningInstance_WithPrunedEntries_ShowsItsFullHistory()
    {
        // Arrange
        var grain = await Start(RootLoop());
        var instanceId = grain.GetPrimaryKey();

        // Act
        await Iterate(grain, 10);
        var fromGrain = await grain.GetStateSnapshot();
        var fromService = await QueryService.GetStateSnapshot(instanceId);

        // Assert — the grain says its state lacks history; the service serves all of it from the journal
        Assert.IsTrue(fromGrain.HistoryPruned);
        Assert.IsEmpty(fromGrain.CompletedActivityIds);
        Assert.IsNotNull(fromService);
        Assert.IsFalse(fromService.HistoryPruned);
        Assert.IsFalse(fromService.IsFromReadModel);
        AssertFullLoopHistory(fromService, 10);
        Assert.AreEqual(fromGrain.StateVersion, fromService.StateVersion);
    }

    [TestMethod]
    public async Task PrunedInstance_ReactivatedFromItsSnapshot_CarriesOn()
    {
        // Arrange
        var grain = await DeployAndStart(RootLoop());
        var instanceId = grain.GetPrimaryKey();
        await Iterate(grain, 5);

        // Act
        await ForceAllGrainDeactivation(instanceId);
        await Iterate(grain, 5);

        // Assert
        AssertFullLoopHistory((await QueryService.GetStateSnapshot(instanceId))!, 10);
        var (snapshot, _) = await EventStore.ReadSnapshotAsync(instanceId);
        Assert.HasCount(1, snapshot!.Entries);
    }

    [TestMethod]
    public async Task CompletedInstance_ShowsItsFullHistory_FromTheReadModel()
    {
        // Arrange
        const int steps = 10;
        var grain = await Start(ScriptAndGatewayChain(steps));
        var instanceId = grain.GetPrimaryKey();

        // Act
        var completed = await WaitForCondition(instanceId, s => s.IsCompleted);
        await WaitForProjection(instanceId);
        var fromReadModel = await QueryService.GetStateSnapshot(instanceId);

        // Assert — start + steps of (script + gateway) + end, also before the row said completed
        Assert.HasCount(steps * 2 + 2, completed.CompletedActivityIds);
        Assert.IsNotNull(fromReadModel);
        Assert.IsTrue(fromReadModel.IsFromReadModel);
        Assert.HasCount(steps * 2 + 2, fromReadModel.CompletedActivityIds);
        Assert.HasCount(steps * 2, fromReadModel.ConditionSequences);
        var (snapshot, _) = await EventStore.ReadSnapshotAsync(instanceId);
        Assert.IsLessThan(steps, snapshot!.Entries.Count);
    }

    [TestMethod]
    public async Task InstanceLongerThanOneRead_IsServedAndDebuggable()
    {
        // Arrange — more events than one ReadEventsAsync may load
        var grain = await Start(ScriptAndGatewayChain(125));
        var instanceId = grain.GetPrimaryKey();
        var completed = await WaitForCondition(instanceId, s => s.IsCompleted, timeoutMs: 60000);
        await WaitForProjection(instanceId);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => EventStore.ReadEventsAsync(instanceId, 0));

        // Act
        var journal = await InstanceJournal.LoadAsync(EventStore, instanceId);
        var final = journal.StateAt(journal.Steps.Count);
        var halfway = journal.StateAt(journal.Steps.Count / 2);

        // Assert
        Assert.IsGreaterThan(1000, journal.Steps.Count);
        Assert.IsTrue(final.IsCompleted);
        Assert.HasCount(completed.CompletedActivityIds.Count, final.CompletedActivityIds);
        Assert.IsFalse(halfway.IsCompleted);
    }

    [TestMethod]
    public async Task FailingSnapshotWrites_DoNotLoseHistoryFromDetailsReads()
    {
        // Arrange — no snapshot can be stored, so pruning only ever changes the live state
        var instanceId = Guid.NewGuid();
        var grain = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(instanceId);
        FaultInjectingEventStore.FailSnapshotWrites(instanceId);
        try
        {
            await grain.StartWorkflow(RootLoop());

            // Act
            await Iterate(grain, 3);

            // Assert
            Assert.IsTrue((await grain.GetStateSnapshot()).HistoryPruned);
            AssertFullLoopHistory((await QueryService.GetStateSnapshot(instanceId))!, 3);
        }
        finally
        {
            FaultInjectingEventStore.FailSnapshotWrites(instanceId, fail: false);
        }
    }

    [TestMethod]
    public async Task LateCallbacksForAPrunedEntry_AreIgnored()
    {
        // Arrange
        var grain = await Start(RootLoop());
        var instanceId = grain.GetPrimaryKey();
        await Iterate(grain, 2);
        var before = (await QueryService.GetStateSnapshot(instanceId))!;
        var pruned = before.CompletedActivities.First(a => a.ActivityId == "task").ActivityInstanceId;

        // Act
        await grain.CompleteActivity("task", pruned, new ExpandoObject());
        await grain.FailActivity("task", pruned, new InvalidOperationException("late"));

        // Assert
        var after = (await QueryService.GetStateSnapshot(instanceId))!;
        Assert.AreEqual(before.StateVersion, after.StateVersion);
        AssertFullLoopHistory(after, 2);
    }

    /// <summary>
    /// A signal subscription's effect persists the turn, and at that point another activity of the
    /// same iteration may have completed and still wait for its transition. That drain's snapshot
    /// must not prune; the next settled one does.
    /// </summary>
    [TestMethod]
    public async Task MidTurnDrain_SnapshotKeepsFinishedEntries()
    {
        // Arrange
        var start = new StartEvent("start");
        var waitSignal = new SignalIntermediateCatchEvent("waitSignal", "sig1");
        var afterSignal = new TaskActivity("afterSignal");
        var workflow = new WorkflowDefinition
        {
            WorkflowId = "mid-turn",
            Activities = [start, waitSignal, afterSignal],
            SequenceFlows =
            [
                new SequenceFlow("a", start, waitSignal),
                new SequenceFlow("b", waitSignal, afterSignal)
            ],
            Signals = [new SignalDefinition("sig1", $"midTurn-{Guid.NewGuid():N}")]
        };

        // Act
        var grain = await Start(workflow);

        // Assert
        var instanceId = grain.GetPrimaryKey();
        var snapshots = FaultInjectingEventStore.FinishedEntriesPerSnapshotOf(instanceId);
        Assert.IsTrue(snapshots.Any(finished => finished.Contains("start")), "the subscription's drain keeps it");
    }

    [TestMethod]
    public async Task ParallelFork_InALoop_JoinsEveryIteration()
    {
        // Arrange — task → fork → (a, b) → join → gateway → back to task
        var workflow = ParallelLoop();
        var grain = await Start(workflow);
        var instanceId = grain.GetPrimaryKey();

        for (var iteration = 1; iteration <= 3; iteration++)
        {
            // Act
            await grain.CompleteActivity("task", new ExpandoObject());
            await grain.CompleteActivity("a", new ExpandoObject());
            await grain.CompleteActivity("b", new ExpandoObject());

            // Assert
            var snapshot = (await QueryService.GetStateSnapshot(instanceId))!;
            Assert.AreEqual("task", snapshot.ActiveActivityIds.Single(), $"iteration {iteration}");
            Assert.AreEqual(iteration, snapshot.CompletedActivityIds.Count(id => id == "gateway"));
        }
    }

    internal static WorkflowDefinition ParallelLoop()
    {
        var start = new StartEvent("start");
        var task = new TaskActivity("task");
        var fork = new ParallelGateway("fork", IsFork: true);
        var a = new TaskActivity("a");
        var b = new TaskActivity("b");
        var join = new ParallelGateway("join", IsFork: false);
        var gateway = new ExclusiveGateway("gateway");
        var end = new EndEvent("end");
        return new WorkflowDefinition
        {
            WorkflowId = "parallel-loop",
            Activities = [start, task, fork, a, b, join, gateway, end],
            SequenceFlows =
            [
                new SequenceFlow("in", start, task),
                new SequenceFlow("toFork", task, fork),
                new SequenceFlow("toA", fork, a),
                new SequenceFlow("toB", fork, b),
                new SequenceFlow("aToJoin", a, join),
                new SequenceFlow("bToJoin", b, join),
                new SequenceFlow("toGateway", join, gateway),
                new ConditionalSequenceFlow("again", gateway, task, "true"),
                new ConditionalSequenceFlow("done", gateway, end, "false")
            ]
        };
    }
}

/// <summary>
/// The same behaviour without pruning, with snapshots far apart: an engine decision must not depend
/// on whether finished entries have been pruned yet.
/// </summary>
[TestClass]
public class InstanceHistoryUnprunedTests : WorkflowTestBase
{
    protected override void ConfigureCluster(Orleans.TestingHost.TestClusterBuilder builder)
        => builder.AddSiloBuilderConfigurator<RareSnapshotsConfigurator>();

    private sealed class RareSnapshotsConfigurator : Orleans.TestingHost.ISiloConfigurator
    {
        public void Configure(ISiloBuilder siloBuilder) =>
            siloBuilder.Services.PostConfigure<Fleans.Persistence.FleansPersistenceOptions>(o => o.SnapshotInterval = 900);
    }

    [TestMethod]
    public async Task ParallelFork_InALoop_JoinsEveryIteration()
    {
        // Arrange
        var grain = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await grain.StartWorkflow(InstanceHistoryPruningTests.ParallelLoop());

        for (var iteration = 1; iteration <= 3; iteration++)
        {
            // Act
            await grain.CompleteActivity("task", new ExpandoObject());
            await grain.CompleteActivity("a", new ExpandoObject());
            await grain.CompleteActivity("b", new ExpandoObject());

            // Assert
            var snapshot = await grain.GetStateSnapshot();
            Assert.IsFalse(snapshot.HistoryPruned);
            Assert.AreEqual("task", snapshot.ActiveActivityIds.Single(), $"iteration {iteration}");
            Assert.AreEqual(iteration, snapshot.CompletedActivityIds.Count(id => id == "gateway"));
        }
    }
}
