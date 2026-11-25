using Fleans.Application.Grains;
using Fleans.Application.QueryModels;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using Microsoft.Extensions.DependencyInjection;
using Orleans.TestingHost;
using System.Dynamic;

namespace Fleans.Application.Tests;

/// <summary>
/// The journal is the source of truth: an instance deactivated in the middle of a scope must come
/// back — from a snapshot plus the journal tail, or from the journal alone — with the same state it
/// had, and then run to the end. Each scenario stops inside a construct whose state lives only in
/// <c>Apply</c> handlers (sub-process scope, sequential multi-instance, compensation walk, open
/// transaction, complex join with one token), compares the state before and after reactivation,
/// and finishes the instance.
/// </summary>
public abstract class ReactivationMidScopeTestBase : WorkflowTestBase
{
    [TestMethod]
    public async Task MidSubProcess_ReactivatesWithTheSameState_AndCompletes()
    {
        // Arrange — start → sub(a → b) → end; stop on b with a variable set inside the sub-process
        var subStart = new StartEvent("subStart");
        var a = new TaskActivity("a");
        var b = new TaskActivity("b");
        var subEnd = new EndEvent("subEnd");
        var sub = new SubProcess("sub")
        {
            Activities = [subStart, a, b, subEnd],
            SequenceFlows =
            [
                new SequenceFlow("sf1", subStart, a),
                new SequenceFlow("sf2", a, b),
                new SequenceFlow("sf3", b, subEnd)
            ]
        };
        var instance = await Start(Linear("reactivate-mid-sub", sub));
        dynamic vars = new ExpandoObject();
        vars.inside = "sub";
        await instance.CompleteActivity("a", vars);

        // Act + Assert
        await AssertReactivationKeepsState(instance, s => s.ActiveActivityIds.Contains("b"));
        await instance.CompleteActivity("b", new ExpandoObject());
        await AssertCompleted(instance);
    }

    [TestMethod]
    public async Task MidSequentialMultiInstance_ReactivatesWithTheSameState_AndRunsTheRemainingIterations()
    {
        // Arrange — sequential MI x3 over a manual task; stop after the first iteration
        var mi = new MultiInstanceActivity("work", new TaskActivity("work"), IsSequential: true, LoopCardinality: 3);
        var instance = await Start(Linear("reactivate-mid-seq-mi", mi));
        await CompleteIteration(instance);

        // Act + Assert
        await AssertReactivationKeepsState(instance,
            s => s.CompletedActivities.Count(a => a.ActivityId == "work") == 1 && s.ActiveActivityIds.Count(id => id == "work") == 2);
        await CompleteIteration(instance);
        await CompleteIteration(instance);
        var done = await AssertCompleted(instance);
        Assert.AreEqual(4, done.CompletedActivities.Count(a => a.ActivityId == "work"), "3 iterations + the host");
    }

    [TestMethod]
    public async Task MidCompensationWalk_ReactivatesWithTheSameState_AndRunsTheRemainingHandler()
    {
        // Arrange — task_a, task_b (each compensable by a manual handler) → throw → end; stop on the first handler
        var start = new StartEvent("start");
        var taskA = new TaskActivity("task_a");
        var taskB = new TaskActivity("task_b");
        var handlerA = new TaskActivity("handler_a");
        var handlerB = new TaskActivity("handler_b");
        var throwComp = new CompensationIntermediateThrowEvent("throw_comp", null);
        var end = new EndEvent("end");
        var workflow = new WorkflowDefinition
        {
            WorkflowId = "reactivate-mid-compensation",
            Activities =
            [
                start, taskA, taskB, throwComp, end, handlerA, handlerB,
                new CompensationBoundaryEvent("cb_a", "task_a", "handler_a"),
                new CompensationBoundaryEvent("cb_b", "task_b", "handler_b")
            ],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, taskA),
                new SequenceFlow("f2", taskA, taskB),
                new SequenceFlow("f3", taskB, throwComp),
                new SequenceFlow("f4", throwComp, end)
            ]
        };
        var instance = await Start(workflow);
        await instance.CompleteActivity("task_a", new ExpandoObject());
        await instance.CompleteActivity("task_b", new ExpandoObject());

        // Act + Assert — handlers run in reverse completion order: handler_b first
        await AssertReactivationKeepsState(instance, s => s.ActiveActivityIds.Contains("handler_b"));
        await instance.CompleteActivity("handler_b", new ExpandoObject());
        await WaitForCondition(instance.GetPrimaryKey(), s => s.ActiveActivityIds.Contains("handler_a"));
        await instance.CompleteActivity("handler_a", new ExpandoObject());
        var done = await AssertCompleted(instance);
        CollectionAssert.Contains(done.CompletedActivityIds, "end");
    }

    [TestMethod]
    public async Task MidTransaction_ReactivatesWithTheSameState_AndCommits()
    {
        // Arrange — start → tx(t1 → t2) → end; stop on t2
        var txStart = new StartEvent("txStart");
        var t1 = new TaskActivity("t1");
        var t2 = new TaskActivity("t2");
        var txEnd = new EndEvent("txEnd");
        var tx = new Transaction("tx")
        {
            Activities = [txStart, t1, t2, txEnd],
            SequenceFlows =
            [
                new SequenceFlow("tx1", txStart, t1),
                new SequenceFlow("tx2", t1, t2),
                new SequenceFlow("tx3", t2, txEnd)
            ]
        };
        var instance = await Start(Linear("reactivate-mid-tx", tx));
        await instance.CompleteActivity("t1", new ExpandoObject());

        // Act + Assert
        await AssertReactivationKeepsState(instance, s => s.ActiveActivityIds.Contains("t2"));
        await instance.CompleteActivity("t2", new ExpandoObject());
        await AssertCompleted(instance);
        var outcomes = await instance.GetTransactionOutcomes();
        Assert.AreEqual(Domain.States.TransactionOutcome.Completed, outcomes.Values.Single().Outcome);
    }

    [TestMethod]
    public async Task ComplexJoinWithOneToken_ReactivatesWithTheSameState_AndFiresOnTheSecond()
    {
        // Arrange — fork → (a, b) → complex join (waits for all) → end; stop after a
        var start = new StartEvent("start");
        var fork = new ParallelGateway("fork", IsFork: true);
        var a = new TaskActivity("a");
        var b = new TaskActivity("b");
        var join = new ComplexGateway("join", IsFork: false, ActivationCondition: null);
        var end = new EndEvent("end");
        var workflow = new WorkflowDefinition
        {
            WorkflowId = "reactivate-complex-join",
            Activities = [start, fork, a, b, join, end],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, fork),
                new SequenceFlow("f2", fork, a),
                new SequenceFlow("f3", fork, b),
                new SequenceFlow("f4", a, join),
                new SequenceFlow("f5", b, join),
                new SequenceFlow("f6", join, end)
            ]
        };
        var instance = await Start(workflow);
        await instance.CompleteActivity("a", new ExpandoObject());

        // Act + Assert
        await AssertReactivationKeepsState(instance, s => s.ActiveActivityIds.Contains("b") && !s.ActiveActivityIds.Contains("a"));
        await instance.CompleteActivity("b", new ExpandoObject());
        var done = await AssertCompleted(instance);
        Assert.AreEqual(1, done.CompletedActivityIds.Count(id => id == "end"), "the join fires once");
    }

    // --- helpers ---

    private async Task<IWorkflowInstanceGrain> Start(WorkflowDefinition workflow)
    {
        // Deployed, so a reactivated instance can load its definition.
        var process = Cluster.GrainFactory.GetGrain<IProcessDefinitionGrain>(workflow.WorkflowId);
        await process.DeployVersion(workflow, "<placeholder/>");
        var instance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await instance.StartWorkflow(await process.GetLatestDefinition());
        return instance;
    }

    private static WorkflowDefinition Linear(string id, Activity body)
    {
        var start = new StartEvent("start");
        var end = new EndEvent("end");
        return new WorkflowDefinition
        {
            WorkflowId = id,
            Activities = [start, body, end],
            SequenceFlows = [new SequenceFlow("f1", start, body), new SequenceFlow("f2", body, end)]
        };
    }

    private async Task CompleteIteration(IWorkflowInstanceGrain instance)
    {
        var snapshot = await WaitForCondition(instance.GetPrimaryKey(),
            s => s.ActiveActivities.Any(a => a.ActivityId == "work" && a.ActivityType != nameof(MultiInstanceActivity)));
        var iteration = snapshot.ActiveActivities.Single(a => a.ActivityId == "work" && a.ActivityType != nameof(MultiInstanceActivity));
        await instance.CompleteActivity("work", iteration.ActivityInstanceId, new ExpandoObject());
    }

    private async Task<InstanceStateSnapshot> AssertCompleted(IWorkflowInstanceGrain instance)
    {
        var snapshot = await WaitForCondition(instance.GetPrimaryKey(), s => s.IsCompleted);
        Assert.IsFalse(snapshot.IsFailed);
        return snapshot;
    }

    /// <summary>
    /// Waits for <paramref name="ready"/>, deactivates the instance, and asserts the reactivated
    /// instance reports the same active entries, history and variables.
    /// </summary>
    private async Task AssertReactivationKeepsState(IWorkflowInstanceGrain instance, Func<InstanceStateSnapshot, bool> ready)
    {
        var instanceId = instance.GetPrimaryKey();
        await WaitForCondition(instanceId, ready);
        var before = await instance.GetStateSnapshot();

        await ForceAllGrainDeactivation(instanceId);
        var after = await instance.GetStateSnapshot();

        CollectionAssert.AreEquivalent(Active(before), Active(after), "active entries");
        CollectionAssert.AreEquivalent(before.CompletedActivityIds, after.CompletedActivityIds, "history");
        Assert.AreEqual(before.StateVersion, after.StateVersion, "journal version");
        CollectionAssert.AreEquivalent(Variables(before), Variables(after), "variable scopes");
    }

    private static List<string> Active(InstanceStateSnapshot s) =>
        s.ActiveActivities.Select(a => $"{a.ActivityId}/{a.ActivityInstanceId}/{a.ActivityType}/{a.VariablesStateId}/{a.IsExecuting}").ToList();

    private static List<string> Variables(InstanceStateSnapshot s) =>
        s.VariableStates.SelectMany(v => v.Variables.Select(kv => $"{v.VariablesId}/{kv.Key}={kv.Value}"))
            .Concat(s.VariableStates.Select(v => v.VariablesId.ToString())).ToList();
}

/// <summary>Reactivation from a snapshot plus the journal tail (the test silo snapshots on every append).</summary>
[TestClass]
public class ReactivationMidScopeFromSnapshotTests : ReactivationMidScopeTestBase;

/// <summary>Reactivation from the journal alone: snapshots are far apart, so every event is replayed.</summary>
[TestClass]
public class ReactivationMidScopeFromJournalTests : ReactivationMidScopeTestBase
{
    protected override void ConfigureCluster(TestClusterBuilder builder)
        => builder.AddSiloBuilderConfigurator<RareSnapshotsConfigurator>();

    private sealed class RareSnapshotsConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder siloBuilder) =>
            siloBuilder.Services.PostConfigure<Fleans.Persistence.FleansPersistenceOptions>(o => o.SnapshotInterval = 900);
    }
}
