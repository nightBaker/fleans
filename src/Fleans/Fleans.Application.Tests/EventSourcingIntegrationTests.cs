using Fleans.Application.Grains;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Events;
using Fleans.Domain.Sequences;
using Fleans.Persistence;
using Fleans.Persistence.Events;
using Microsoft.EntityFrameworkCore;
using System.Dynamic;

namespace Fleans.Application.Tests;

[TestClass]
public class EventSourcingIntegrationTests : WorkflowTestBase
{
    [TestMethod]
    public async Task StateRecovery_AfterDeactivation_ShouldMatchPreDeactivationState()
    {
        // Arrange — run a simple workflow to completion
        var workflow = CreateSequentialWorkflow();
        var instanceId = Guid.NewGuid();
        var grain = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(instanceId);
        await grain.SetWorkflow(workflow);
        await grain.StartWorkflow();

        dynamic vars = new ExpandoObject();
        vars.result = "done";
        await grain.CompleteActivity("task1", (ExpandoObject)vars);
        await grain.CompleteActivity("task2", new ExpandoObject());

        // Record pre-deactivation state
        var preSnapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsNotNull(preSnapshot);
        Assert.IsTrue(preSnapshot.IsCompleted);

        // Act — deactivate and reactivate
        await ForceAllGrainDeactivation();

        // Trigger reactivation by calling a method
        var postGrain = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(instanceId);
        var postId = await postGrain.GetWorkflowInstanceId();

        // Assert — state should match after reactivation
        // Query via the projected query store (written during ApplyUpdatesToStorage)
        var postSnapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsNotNull(postSnapshot);
        Assert.AreEqual(preSnapshot.IsStarted, postSnapshot.IsStarted);
        Assert.AreEqual(preSnapshot.IsCompleted, postSnapshot.IsCompleted);
        Assert.AreEqual(preSnapshot.CompletedActivities.Count, postSnapshot.CompletedActivities.Count);
        Assert.AreEqual(preSnapshot.ActiveActivities.Count, postSnapshot.ActiveActivities.Count);

        // Verify completed activity IDs match
        var preCompletedIds = preSnapshot.CompletedActivities.Select(a => a.ActivityId).OrderBy(x => x).ToList();
        var postCompletedIds = postSnapshot.CompletedActivities.Select(a => a.ActivityId).OrderBy(x => x).ToList();
        CollectionAssert.AreEqual(preCompletedIds, postCompletedIds);

        // Verify grain still reports correct instance ID
        Assert.AreEqual(instanceId, postId);
    }

    [TestMethod]
    public async Task StateRecovery_ParallelWorkflow_ShouldPreserveAllBranches()
    {
        // Arrange — run a fork-join workflow to completion
        var workflow = CreateForkJoinWorkflow();
        var instanceId = Guid.NewGuid();
        var grain = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(instanceId);
        await grain.SetWorkflow(workflow);
        await grain.StartWorkflow();

        dynamic vars1 = new ExpandoObject();
        vars1.fromBranch1 = "value1";
        await grain.CompleteActivity("task1", (ExpandoObject)vars1);

        dynamic vars2 = new ExpandoObject();
        vars2.fromBranch2 = "value2";
        await grain.CompleteActivity("task2", (ExpandoObject)vars2);

        var preSnapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsNotNull(preSnapshot);
        Assert.IsTrue(preSnapshot.IsCompleted);

        // Act — deactivate and reactivate
        await ForceAllGrainDeactivation();
        var postGrain = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(instanceId);
        await postGrain.GetWorkflowInstanceId();

        // Assert
        var postSnapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsNotNull(postSnapshot);
        Assert.IsTrue(postSnapshot.IsCompleted);
        Assert.AreEqual(preSnapshot.CompletedActivities.Count, postSnapshot.CompletedActivities.Count);

        // Verify merged variables survived
        var mergedScope = postSnapshot.VariableStates.Single();
        Assert.IsTrue(mergedScope.Variables.ContainsKey("fromBranch1"));
        Assert.IsTrue(mergedScope.Variables.ContainsKey("fromBranch2"));
        Assert.AreEqual("value1", mergedScope.Variables["fromBranch1"]);
        Assert.AreEqual("value2", mergedScope.Variables["fromBranch2"]);
    }

    [TestMethod]
    public async Task MidExecutionRecovery_ShouldResumeAfterDeactivation()
    {
        // Arrange — workflow with message catch: start → task1 → messageCatch → task2 → end
        // Must deploy via factory so ProcessDefinitionId is set and grain can reload
        // the definition from ProcessDefinitionGrain on reactivation.
        var msgDef = new MessageDefinition("msg1", "paymentReceived", "orderId");
        var start = new StartEvent("start");
        var task1 = new TaskActivity("task1");
        var msgCatch = new MessageIntermediateCatchEvent("waitPayment", "msg1");
        var task2 = new TaskActivity("task2");
        var end = new EndEvent("end");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "mid-exec-recovery",
            Activities = [start, task1, msgCatch, task2, end],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, task1),
                new SequenceFlow("f2", task1, msgCatch),
                new SequenceFlow("f3", msgCatch, task2),
                new SequenceFlow("f4", task2, end)
            ],
            Messages = [msgDef]
        };

        // Deploy via processGrain so ProcessDefinitionId is persisted
        var processGrain = Cluster.GrainFactory.GetGrain<IProcessDefinitionGrain>("mid-exec-recovery");
        await processGrain.DeployVersion(workflow, "<bpmn/>");
        var grain = await processGrain.CreateInstance();
        var instanceId = await grain.GetWorkflowInstanceId();

        await grain.StartWorkflow();

        // Complete task1 with orderId for message correlation
        dynamic vars = new ExpandoObject();
        vars.orderId = "order-456";
        await grain.CompleteActivity("task1", (ExpandoObject)vars);

        // Verify workflow is waiting at message catch
        var midSnapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsFalse(midSnapshot!.IsCompleted);
        Assert.IsTrue(midSnapshot.ActiveActivities.Any(a => a.ActivityId == "waitPayment"));

        // Act — deactivate grain while waiting for message
        await ForceAllGrainDeactivation();

        // Send message via correlation grain (full correlation path per design review)
        var grainKey = MessageCorrelationKey.Build("paymentReceived", "order-456");
        var correlationGrain = Cluster.GrainFactory.GetGrain<IMessageCorrelationGrain>(grainKey);
        dynamic msgVars = new ExpandoObject();
        msgVars.paymentStatus = "confirmed";
        var delivered = await correlationGrain.DeliverMessage((ExpandoObject)msgVars);

        Assert.IsTrue(delivered, "Message should be delivered after grain reactivation");

        // Complete task2 to finish workflow
        var reactivatedGrain = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(instanceId);
        await reactivatedGrain.CompleteActivity("task2", new ExpandoObject());

        // Assert — workflow completed after mid-execution recovery
        var finalSnapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsTrue(finalSnapshot!.IsCompleted);
        Assert.IsTrue(finalSnapshot.CompletedActivities.Any(a => a.ActivityId == "waitPayment"));
        Assert.IsTrue(finalSnapshot.CompletedActivities.Any(a => a.ActivityId == "task2"));
    }

    [TestMethod]
    public async Task SnapshotOptimization_DeactivationWritesSnapshot()
    {
        // Arrange — run a workflow to completion
        var workflow = CreateSequentialWorkflow();
        var instanceId = Guid.NewGuid();
        var grain = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(instanceId);
        await grain.SetWorkflow(workflow);
        await grain.StartWorkflow();
        await grain.CompleteActivity("task1", new ExpandoObject());
        await grain.CompleteActivity("task2", new ExpandoObject());

        // Act — deactivate (triggers OnDeactivateAsync → snapshot write)
        await ForceAllGrainDeactivation();

        // Assert — verify snapshot exists in DB
        var dbFactory = GetSiloService<IDbContextFactory<FleanCommandDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();

        var grainIdStr = instanceId.ToString();
        var snapshot = await db.WorkflowSnapshots
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.GrainId == grainIdStr);

        Assert.IsNotNull(snapshot, "Snapshot should be written on graceful deactivation");
        Assert.IsTrue(snapshot.Version > 0, "Snapshot version should be > 0");

        // Verify events exist
        var eventCount = await db.WorkflowEvents
            .AsNoTracking()
            .CountAsync(e => e.GrainId == grainIdStr);
        Assert.IsTrue(eventCount > 0, "Events should be persisted");

        // Snapshot version should match event count
        Assert.AreEqual(eventCount, snapshot.Version,
            "Snapshot version should equal total event count after full workflow completion");
    }

    [TestMethod]
    public async Task VersionTracking_EventCountMatchesExpected()
    {
        // Arrange
        var workflow = CreateSimpleWorkflow("es-simple-workflow");
        var instanceId = Guid.NewGuid();
        var grain = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(instanceId);
        var grainIdStr = instanceId.ToString();

        var dbFactory = GetSiloService<IDbContextFactory<FleanCommandDbContext>>();

        // Act — SetWorkflow + StartWorkflow
        await grain.SetWorkflow(workflow);
        await grain.StartWorkflow();

        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            var countAfterStart = await db.WorkflowEvents
                .AsNoTracking()
                .CountAsync(e => e.GrainId == grainIdStr);
            Assert.IsTrue(countAfterStart > 0, "Events should be emitted after SetWorkflow + StartWorkflow");
        }

        // Act — Complete task
        await grain.CompleteActivity("task", new ExpandoObject());

        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            var countAfterComplete = await db.WorkflowEvents
                .AsNoTracking()
                .CountAsync(e => e.GrainId == grainIdStr);
            Assert.IsTrue(countAfterComplete > 0, "Events should exist after activity completion");

            // Verify events are ordered by version
            var versions = await db.WorkflowEvents
                .AsNoTracking()
                .Where(e => e.GrainId == grainIdStr)
                .OrderBy(e => e.Version)
                .Select(e => e.Version)
                .ToListAsync();

            // Versions should be contiguous starting from 0
            for (int i = 0; i < versions.Count; i++)
            {
                Assert.AreEqual(i, versions[i],
                    $"Event version at index {i} should be {i} but was {versions[i]}");
            }
        }
    }

    [TestMethod]
    public async Task ExpandoObjectRoundTrip_VariablesSurviveEventStoreReplay()
    {
        // Arrange — workflow with a task that sets diverse variable types
        var workflow = CreateSimpleWorkflow("es-simple-workflow");
        var instanceId = Guid.NewGuid();
        var grain = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(instanceId);
        await grain.SetWorkflow(workflow);
        await grain.StartWorkflow();

        // Complete task with various types
        dynamic vars = new ExpandoObject();
        vars.intValue = 42;
        vars.stringValue = "hello world";
        vars.boolValue = true;
        vars.doubleValue = 3.14;
        await grain.CompleteActivity("task", (ExpandoObject)vars);

        // Record pre-deactivation variable state
        var preSnapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsNotNull(preSnapshot);
        Assert.IsTrue(preSnapshot.IsCompleted);

        var preVars = preSnapshot.VariableStates.First().Variables;

        // Act — deactivate and reactivate
        await ForceAllGrainDeactivation();
        var postGrain = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(instanceId);
        await postGrain.GetWorkflowInstanceId();

        // Assert — variables survived round-trip
        var postSnapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsNotNull(postSnapshot);

        var postVars = postSnapshot.VariableStates.First().Variables;

        // Note: int may be deserialized as long, double as double — check values via ToString
        Assert.AreEqual(preVars["intValue"]?.ToString(), postVars["intValue"]?.ToString());
        Assert.AreEqual(preVars["stringValue"], postVars["stringValue"]);
        Assert.AreEqual(preVars["boolValue"]?.ToString(), postVars["boolValue"]?.ToString());
        Assert.AreEqual(preVars["doubleValue"]?.ToString(), postVars["doubleValue"]?.ToString());
    }

    // ── Workflow Builders ────────────────────────────────────────────────

    private static IWorkflowDefinition CreateSequentialWorkflow()
    {
        var start = new StartEvent("start");
        var task1 = new TaskActivity("task1");
        var task2 = new TaskActivity("task2");
        var end = new EndEvent("end");

        return new WorkflowDefinition
        {
            WorkflowId = "es-sequential-workflow",
            Activities = [start, task1, task2, end],
            SequenceFlows =
            [
                new SequenceFlow("seq1", start, task1),
                new SequenceFlow("seq2", task1, task2),
                new SequenceFlow("seq3", task2, end)
            ]
        };
    }

    private static IWorkflowDefinition CreateForkJoinWorkflow()
    {
        var start = new StartEvent("start");
        var fork = new ParallelGateway("fork", IsFork: true);
        var task1 = new TaskActivity("task1");
        var task2 = new TaskActivity("task2");
        var join = new ParallelGateway("join", IsFork: false);
        var end = new EndEvent("end");

        return new WorkflowDefinition
        {
            WorkflowId = "es-fork-join-workflow",
            Activities = [start, fork, task1, task2, join, end],
            SequenceFlows =
            [
                new SequenceFlow("seq1", start, fork),
                new SequenceFlow("seq2", fork, task1),
                new SequenceFlow("seq3", fork, task2),
                new SequenceFlow("seq4", task1, join),
                new SequenceFlow("seq5", task2, join),
                new SequenceFlow("seq6", join, end)
            ]
        };
    }
}
