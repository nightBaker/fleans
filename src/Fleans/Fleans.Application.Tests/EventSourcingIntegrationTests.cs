using Fleans.Application.Grains;
using Fleans.Application.QueryModels;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Events;
using Fleans.Domain.Sequences;
using Fleans.Domain.States;
using Fleans.Persistence;
using Fleans.Persistence.Events;
using Microsoft.EntityFrameworkCore;
using System.Dynamic;

namespace Fleans.Application.Tests;

[TestClass]
public class EventSourcingIntegrationTests : WorkflowTestBase
{
    [TestMethod]
    public async Task WorkflowStarted_StoresGrainKeyAsInstanceId()
    {
        // Arrange — replay rebuilds State.Id from WorkflowStarted.InstanceId, and effects
        // (message/signal/timer registrations) address the instance by State.Id. A fresh Guid here
        // was masked at runtime only because the first state projection overwrote State.Id.
        var instanceId = Guid.NewGuid();
        var grain = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(instanceId);

        // Act
        await grain.SetWorkflow(CreateSimpleWorkflow("es-started-instance-id"));

        // Assert
        var events = await GetSiloService<IEventStore>().ReadEventsAsync(instanceId, afterVersion: 0);
        var started = events.OfType<WorkflowStarted>().Single();
        Assert.AreEqual(instanceId, started.InstanceId);
    }

    [TestMethod]
    public async Task StateRecovery_WithoutSnapshotOrReadModel_ReplaysAllEventsIncludingWorkflowStarted()
    {
        // Arrange — an instance waiting on task2, then its snapshot and read-model row removed:
        // the journal alone must be enough (#833). Deployed, so the reactivated grain can load
        // its definition by ProcessDefinitionId.
        var workflow = (WorkflowDefinition)CreateSequentialWorkflow();
        await Cluster.GrainFactory.GetGrain<IProcessDefinitionGrain>(workflow.WorkflowId)
            .DeployVersion(workflow, "<placeholder/>");
        var grain = await Cluster.GrainFactory.GetGrain<IProcessDefinitionGrain>(workflow.WorkflowId)
            .CreateInstance();
        var instanceId = grain.GetPrimaryKey();
        await grain.StartWorkflow();
        await grain.CompleteActivity("task1", new ExpandoObject());
        await ForceAllGrainDeactivation(instanceId);

        var dbFactory = GetSiloService<IDbContextFactory<FleanCommandDbContext>>();
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            await db.WorkflowSnapshots.Where(s => s.InstanceId == instanceId).ExecuteDeleteAsync();
            await db.WorkflowInstances.Where(s => s.Id == instanceId).ExecuteDeleteAsync();
        }

        // Act — the grain is inactive and has no snapshot, so this activation replays every event
        await grain.CompleteActivity("task2", new ExpandoObject());

        // Assert
        var snapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsNotNull(snapshot);
        Assert.IsTrue(snapshot.IsCompleted);
        Assert.IsFalse(snapshot.IsFailed);

        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            var versions = await JournalVersions(db, instanceId);
            CollectionAssert.AreEqual(Enumerable.Range(1, versions.Count).ToList(), versions,
                "the replayed grain must continue at the right version — no skipped or duplicated events");
            Assert.IsTrue(await db.WorkflowInstances.AnyAsync(s => s.Id == instanceId && s.IsCompleted),
                "the read-model row must be rebuilt and completed");
        }
    }

    [TestMethod]
    public async Task StateRecovery_RestoresStateTheReadModelDoesNotStore()
    {
        // Arrange — TransactionOutcomes lives only in the grain state (the WorkflowInstances read
        // model ignores it), so it survives reactivation only if the snapshot holds the full state
        // or replay starts from the first event.
        var start = new StartEvent("start");
        var txStart = new StartEvent("tx_start");
        var txTask = new TaskActivity("tx_task");
        var txEnd = new EndEvent("tx_end");
        var tx = new Transaction("tx")
        {
            Activities = [txStart, txTask, txEnd],
            SequenceFlows =
            [
                new SequenceFlow("tx_sf1", txStart, txTask),
                new SequenceFlow("tx_sf2", txTask, txEnd)
            ]
        };
        var waitAfter = new TaskActivity("after");
        var end = new EndEvent("end");
        var workflow = new WorkflowDefinition
        {
            WorkflowId = "es-full-state-recovery",
            Activities = [start, tx, waitAfter, end],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, tx),
                new SequenceFlow("f2", tx, waitAfter),
                new SequenceFlow("f3", waitAfter, end)
            ]
        };
        await Cluster.GrainFactory.GetGrain<IProcessDefinitionGrain>(workflow.WorkflowId)
            .DeployVersion(workflow, "<placeholder/>");
        var grain = await Cluster.GrainFactory.GetGrain<IProcessDefinitionGrain>(workflow.WorkflowId)
            .CreateInstance();
        var instanceId = grain.GetPrimaryKey();
        await grain.StartWorkflow();
        await grain.CompleteActivity("tx_task", new ExpandoObject());
        Assert.AreEqual(1, (await grain.GetTransactionOutcomes()).Count);

        // Act
        await ForceAllGrainDeactivation(instanceId);
        var outcomes = await grain.GetTransactionOutcomes();

        // Assert
        Assert.AreEqual(1, outcomes.Count, "the transaction outcome must survive reactivation");
        Assert.AreEqual(TransactionOutcome.Completed, outcomes.Values.Single().Outcome);
    }

    [TestMethod]
    [DoNotParallelize] // listens to the process-wide Fleans meter
    public async Task ActivityDuration_SpanningReactivation_IsMeasuredFromTheJournal()
    {
        // Arrange — task1 spawns, the grain deactivates, task1 completes in a new activation
        var measurements = new System.Collections.Concurrent.ConcurrentBag<double>();
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "Fleans" && instrument.Name == "fleans.activity.duration")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>((_, value, _, _) => measurements.Add(value));
        listener.Start();

        var workflow = (WorkflowDefinition)CreateSequentialWorkflow();
        await Cluster.GrainFactory.GetGrain<IProcessDefinitionGrain>(workflow.WorkflowId)
            .DeployVersion(workflow, "<placeholder/>");
        var grain = await Cluster.GrainFactory.GetGrain<IProcessDefinitionGrain>(workflow.WorkflowId)
            .CreateInstance();
        await grain.StartWorkflow();
        await ForceAllGrainDeactivation(grain.GetPrimaryKey());
        await Task.Delay(TimeSpan.FromMilliseconds(400));
        measurements.Clear();

        // Act
        await grain.CompleteActivity("task1", new ExpandoObject());

        // Assert
        Assert.IsTrue(measurements.Any(ms => ms >= 400),
            "task1's duration must include the time before the reactivation");
    }

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

        // Deploy via ProcessDefinitionGrain so ProcessDefinitionId is persisted
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
    public async Task Projection_WritesTheRowOnlyWhenItChanges()
    {
        // Most appends change no column of a running instance's row, so they write nothing (#878).
        // The appends that do (start, completion) write before the call returns: queries see them.
        var instanceId = Guid.NewGuid();
        var grain = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(instanceId);
        var dbFactory = GetSiloService<IDbContextFactory<FleanCommandDbContext>>();

        async Task<WorkflowInstanceState?> Row()
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            return await db.WorkflowInstances.AsNoTracking().SingleOrDefaultAsync(s => s.Id == instanceId);
        }

        // Act & Assert
        await grain.SetWorkflow(CreateSequentialWorkflow());
        Assert.IsNotNull(await Row(), "the row must exist once the instance is created");

        await grain.StartWorkflow();
        Assert.IsTrue((await Row())!.IsStarted);
        var writesWhenStarted = FaultInjectingWorkflowStateProjection.WriteCount(instanceId);

        await grain.CompleteActivity("task1", new ExpandoObject());
        Assert.AreEqual(writesWhenStarted, FaultInjectingWorkflowStateProjection.WriteCount(instanceId),
            "completing task1 changes no column of the row, so it must not be written");

        await grain.CompleteActivity("task2", new ExpandoObject());
        var completed = await Row();
        Assert.IsTrue(completed!.IsCompleted);
        Assert.IsNotNull(completed.CompletedAt);
        Assert.IsGreaterThan(writesWhenStarted, FaultInjectingWorkflowStateProjection.WriteCount(instanceId));
    }

    [TestMethod]
    public async Task Snapshot_IsWrittenEverySnapshotInterval()
    {
        // Arrange
        var instanceId = Guid.NewGuid();
        var grain = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(instanceId);
        var dbFactory = GetSiloService<IDbContextFactory<FleanCommandDbContext>>();

        // Act
        await grain.SetWorkflow(CreateSequentialWorkflow());
        await grain.StartWorkflow();
        await grain.CompleteActivity("task1", new ExpandoObject());
        await grain.CompleteActivity("task2", new ExpandoObject());

        // Assert — a snapshot exists, covers a stored version, and lags by less than one interval
        await using var db = await dbFactory.CreateDbContextAsync();
        var eventCount = (await JournalVersions(db, instanceId)).Count;
        Assert.IsGreaterThanOrEqualTo(TestSnapshotInterval, eventCount, "the workflow must cross one interval");
        var snapshot = await db.WorkflowSnapshots.AsNoTracking().SingleAsync(s => s.InstanceId == instanceId);
        Assert.IsLessThanOrEqualTo(eventCount, snapshot.Version);
        Assert.IsLessThan(TestSnapshotInterval, eventCount - snapshot.Version);
    }

    [TestMethod]
    public async Task StateRecovery_UnreadableSnapshot_RetriesTheReadInsteadOfReplaying()
    {
        // Arrange — a snapshot this build cannot read. Pre-1.0 there is no fallback for state from
        // older builds (fresh database), and a transient read error must not trigger a full replay.
        var workflow = (WorkflowDefinition)CreateSequentialWorkflow();
        await Cluster.GrainFactory.GetGrain<IProcessDefinitionGrain>(workflow.WorkflowId)
            .DeployVersion(workflow, "<placeholder/>");
        var grain = await Cluster.GrainFactory.GetGrain<IProcessDefinitionGrain>(workflow.WorkflowId)
            .CreateInstance();
        var instanceId = grain.GetPrimaryKey();
        await grain.StartWorkflow();
        await grain.CompleteActivity("task1", new ExpandoObject());
        await ForceAllGrainDeactivation(instanceId);

        var dbFactory = GetSiloService<IDbContextFactory<FleanCommandDbContext>>();
        int eventCount;
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            eventCount = (await JournalVersions(db, instanceId)).Count;
            await db.WorkflowSnapshots.Where(s => s.InstanceId == instanceId).ExecuteDeleteAsync();
            db.WorkflowSnapshots.Add(new WorkflowSnapshotEntity
            {
                InstanceId = instanceId, Version = eventCount, Payload = [0xFF, 0x00, 0x13], Timestamp = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }

        // Act — the call activates the grain, whose snapshot read keeps failing
        var call = grain.CompleteActivity("task2", new ExpandoObject());

        // Assert — Orleans retries the read instead of the grain replaying around it: the call
        // waits, and nothing is appended
        Assert.AreNotSame(call, await Task.WhenAny(call, Task.Delay(TimeSpan.FromSeconds(3))),
            "activation must not succeed on an unreadable snapshot");
        await using (var db = await dbFactory.CreateDbContextAsync())
            Assert.HasCount(eventCount, await JournalVersions(db, instanceId));

        // Once the snapshot is readable (here: removed), the retried read succeeds and the call
        // goes through
        await using (var db = await dbFactory.CreateDbContextAsync())
            await db.WorkflowSnapshots.Where(s => s.InstanceId == instanceId).ExecuteDeleteAsync();
        await call;
        var state = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsNotNull(state);
        Assert.IsTrue(state.IsCompleted);
    }

    [TestMethod]
    public async Task ReadModel_FailedWriteOfTheLastAppend_IsRetriedUntilItLands()
    {
        // Arrange — the projection write of the final append (WorkflowCompleted) fails twice;
        // no further append will come to carry it.
        var instanceId = Guid.NewGuid();
        var grain = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(instanceId);
        await grain.SetWorkflow(CreateSequentialWorkflow());
        await grain.StartWorkflow();
        await grain.CompleteActivity("task1", new ExpandoObject());
        FaultInjectingWorkflowStateProjection.FailNextWrites(instanceId, 2);

        // Act — the command succeeds: the append is the commit
        await grain.CompleteActivity("task2", new ExpandoObject());

        // Assert — the read model catches up without another command
        InstanceStateSnapshot? state = null;
        for (var i = 0; i < 100 && state?.IsCompleted != true; i++) // retries back off: 2 s, then 4 s
        {
            await Task.Delay(200);
            state = await QueryService.GetStateSnapshot(instanceId);
        }
        Assert.IsNotNull(state);
        Assert.IsTrue(state.IsCompleted, "the failed projection write must be retried");

        // ...and no batch was appended twice
        var dbFactory = GetSiloService<IDbContextFactory<FleanCommandDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();
        var versions = await JournalVersions(db, instanceId);
        CollectionAssert.AreEqual(Enumerable.Range(1, versions.Count).ToList(), versions);
        var journal = await GetSiloService<IEventStore>().ReadEventsAsync(instanceId, afterVersion: 0);
        Assert.AreEqual(1, journal.OfType<WorkflowCompleted>().Count(), "the completing batch was stored once");
    }

    [TestMethod]
    public async Task StateRecovery_ReadModelBehindJournal_IsRebuiltOnActivation()
    {
        // Arrange — the read-model row lost (a projection write that failed after its append)
        var workflow = (WorkflowDefinition)CreateSequentialWorkflow();
        await Cluster.GrainFactory.GetGrain<IProcessDefinitionGrain>(workflow.WorkflowId)
            .DeployVersion(workflow, "<placeholder/>");
        var grain = await Cluster.GrainFactory.GetGrain<IProcessDefinitionGrain>(workflow.WorkflowId)
            .CreateInstance();
        var instanceId = grain.GetPrimaryKey();
        await grain.StartWorkflow();
        await grain.CompleteActivity("task1", new ExpandoObject());
        await ForceAllGrainDeactivation(instanceId);

        var dbFactory = GetSiloService<IDbContextFactory<FleanCommandDbContext>>();
        await using (var db = await dbFactory.CreateDbContextAsync())
            await db.WorkflowInstances.Where(s => s.Id == instanceId).ExecuteDeleteAsync();
        Assert.IsNull(await QueryService.GetStateSnapshot(instanceId));

        // Act — any call activates the grain; nothing is appended
        await grain.GetWorkflowInstanceId();

        // Assert
        var state = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsNotNull(state, "activation must rebuild the read model from the journal");
        Assert.IsTrue(state.CompletedActivities.Any(a => a.ActivityId == "task1"));
        Assert.IsTrue(state.ActiveActivities.Any(a => a.ActivityId == "task2"));
    }

    [TestMethod]
    public async Task StateRecovery_StaleReadModelRow_IsRepairedOnActivation()
    {
        // Arrange — the row holds older values (a write lost after its append)
        var workflow = (WorkflowDefinition)CreateSequentialWorkflow();
        await Cluster.GrainFactory.GetGrain<IProcessDefinitionGrain>(workflow.WorkflowId)
            .DeployVersion(workflow, "<placeholder/>");
        var grain = await Cluster.GrainFactory.GetGrain<IProcessDefinitionGrain>(workflow.WorkflowId)
            .CreateInstance();
        var instanceId = grain.GetPrimaryKey();
        await grain.StartWorkflow();
        await ForceAllGrainDeactivation(instanceId);

        var dbFactory = GetSiloService<IDbContextFactory<FleanCommandDbContext>>();
        await using (var db = await dbFactory.CreateDbContextAsync())
            await db.WorkflowInstances.Where(s => s.Id == instanceId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(e => e.IsStarted, false)
                    .SetProperty(e => e.ExecutionStartedAt, (DateTimeOffset?)null));

        // Act — any call activates the grain; nothing is appended
        await grain.GetWorkflowInstanceId();

        // Assert
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            var row = await db.WorkflowInstances.AsNoTracking().SingleAsync(s => s.Id == instanceId);
            Assert.IsTrue(row.IsStarted, "activation must rewrite a row that differs from the journal's state");
            Assert.IsNotNull(row.ExecutionStartedAt);
        }
    }

    [TestMethod]
    public async Task VersionTracking_EventCountMatchesExpected()
    {
        // Arrange
        var workflow = CreateSimpleWorkflow("es-simple-workflow");
        var instanceId = Guid.NewGuid();
        var grain = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(instanceId);

        var dbFactory = GetSiloService<IDbContextFactory<FleanCommandDbContext>>();

        // Act — SetWorkflow + StartWorkflow
        await grain.SetWorkflow(workflow);
        await grain.StartWorkflow();

        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            var countAfterStart = (await JournalVersions(db, instanceId)).Count;
            Assert.IsTrue(countAfterStart > 0, "Events should be emitted after SetWorkflow + StartWorkflow");
        }

        // Act — Complete task
        await grain.CompleteActivity("task", new ExpandoObject());

        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            var countAfterComplete = (await JournalVersions(db, instanceId)).Count;
            Assert.IsTrue(countAfterComplete > 0, "Events should exist after activity completion");

            // Verify events are ordered by version
            var versions = await JournalVersions(db, instanceId);

            // Versions should be contiguous starting from 1 (#833)
            for (int i = 0; i < versions.Count; i++)
            {
                Assert.AreEqual(i + 1, versions[i],
                    $"Event version at index {i} should be {i + 1} but was {versions[i]}");
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

    /// <summary>The journaled event versions of an instance, in order, expanded from its batches.</summary>
    private static async Task<List<int>> JournalVersions(FleanCommandDbContext db, Guid instanceId)
    {
        var batches = await db.WorkflowEventBatches.AsNoTracking()
            .Where(b => b.InstanceId == instanceId)
            .OrderBy(b => b.FirstVersion)
            .Select(b => new { b.FirstVersion, b.LastVersion })
            .ToListAsync();
        return batches.SelectMany(b => Enumerable.Range(b.FirstVersion, b.LastVersion - b.FirstVersion + 1)).ToList();
    }
}
