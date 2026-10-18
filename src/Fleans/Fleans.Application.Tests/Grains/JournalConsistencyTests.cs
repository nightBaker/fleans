using Fleans.Application.Effects;
using Fleans.Application.Grains;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Events;
using Fleans.Domain.Sequences;
using System.Dynamic;

namespace Fleans.Application.Tests.Grains;

/// <summary>
/// The live <c>State</c> of a <see cref="WorkflowInstance"/> must stay what its journal says when
/// journal writes interleave or fail (#869). Each test drives one of the three ways it used to
/// go wrong: a batch applied twice by Orleans after an interleaved enqueue (#846), a queued
/// callback that re-transitioned the turn's completions (#837), and a re-read after a failed
/// append that left the aggregate on the old state object (#845).
/// </summary>
[TestClass]
public class JournalConsistencyTests : WorkflowTestBase
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private async Task<IWorkflowInstanceGrain> StartedDeployedInstance()
    {
        var start = new StartEvent("start");
        var task = new TaskActivity("task");
        var end = new EndEvent("end");
        var workflow = new WorkflowDefinition
        {
            WorkflowId = $"journal-{Guid.NewGuid():N}",
            Activities = [start, task, end],
            SequenceFlows = [new SequenceFlow("s1", start, task), new SequenceFlow("s2", task, end)]
        };

        var processGrain = Cluster.GrainFactory.GetGrain<IProcessDefinitionGrain>(workflow.WorkflowId);
        await processGrain.DeployVersion(workflow, "<xml/>");
        var grain = await processGrain.CreateInstance();
        await grain.StartWorkflow();
        return grain;
    }

    private async Task<IReadOnlyList<WorkflowEvent>> Journal(Guid instanceId) =>
        await GetSiloService<IEventStore>().ReadEventsAsync(instanceId, 0);

    // #846: an [AlwaysInterleave] enqueue confirms while the main turn's batch waits for the next
    // write cycle. When the enqueue's confirm returned first, Orleans applied that batch to State a
    // second time, failed on the already-completed entry, re-read, and left the aggregate behind.
    [TestMethod]
    public async Task InterleavedEnqueue_DuringDrain_DoesNotReapplyTheBatch()
    {
        // Arrange
        var grain = await StartedDeployedInstance();
        var instanceId = grain.GetPrimaryKey();
        var opId = GrainFactoryRetryExtensions.ChildCompletedOpId(Guid.NewGuid(), "absent-call");
        var gates = FaultInjectingEventStore.HoldNextAppends(instanceId, 2);

        // Act — the enqueue's append (write cycle 1) is held at the store.
        var enqueue = grain.OnChildWorkflowCompleted(opId, "absent-call", new ExpandoObject());
        await gates[0].Entered.Task.WaitAsync(Timeout);

        // The main turn applies its batch and raises it; it waits behind cycle 1 for cycle 2.
        var complete = grain.CompleteActivity("task", new ExpandoObject());
        await Task.Delay(500);

        // Cycle 1 commits, so the enqueue's confirm returns while cycle 2 is still appending.
        gates[0].Release.SetResult();
        await gates[1].Entered.Task.WaitAsync(Timeout);
        await Task.Delay(300);
        gates[1].Release.SetResult();

        await complete.WaitAsync(Timeout);
        await enqueue.WaitAsync(Timeout);

        // Assert — the live state took every journaled event exactly once.
        Assert.AreEqual(0, (await grain.GetActiveActivities()).Count, "Workflow should have completed");
        Assert.AreEqual(0, (await grain.GetPendingOperationKeys()).Count,
            "PendingEventDrained is journaled, so the live state must not still hold the op");
        CollectionAssert.Contains((await grain.GetAppliedOperationKeys()).ToList(), opId);

        var journal = await Journal(instanceId);
        Assert.AreEqual(1, journal.OfType<ActivitySpawned>().Count(e => e.ActivityId == "end"));
        Assert.AreEqual(1, journal.OfType<WorkflowCompleted>().Count());

        // The next activation, rebuilt from the journal, agrees with the live state.
        await ForceAllGrainDeactivation(instanceId);
        Assert.AreEqual(0, (await grain.GetActiveActivities()).Count);
        Assert.AreEqual(0, (await grain.GetPendingOperationKeys()).Count);
    }

    // #837: a callback queued while the turn awaits a write is processed at the end of that turn,
    // with the turn's own later completions still undrained. ResolveExternalCompletions transitioned
    // every completion in the buffer, so those successors were spawned a second time.
    [TestMethod]
    public async Task CallbackQueuedDuringTurn_SpawnsEachSuccessorOnce()
    {
        // Arrange — task → fork → [catchA, catchB, pass → task2]. Each signal catch writes the journal
        // before it subscribes; `pass` completes after those writes, inside the same turn.
        var start = new StartEvent("start");
        var task = new TaskActivity("task");
        var fork = new ParallelGateway("fork", IsFork: true);
        var catchA = new SignalIntermediateCatchEvent("catchA", "sigA");
        var catchB = new SignalIntermediateCatchEvent("catchB", "sigB");
        var pass = new ParallelGateway("pass", IsFork: true);
        var task2 = new TaskActivity("task2");
        var workflow = new WorkflowDefinition
        {
            WorkflowId = $"journal-{Guid.NewGuid():N}",
            Activities = [start, task, fork, catchA, catchB, pass, task2],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, task),
                new SequenceFlow("f2", task, fork),
                new SequenceFlow("f3", fork, catchA),
                new SequenceFlow("f4", fork, catchB),
                new SequenceFlow("f5", fork, pass),
                new SequenceFlow("f6", pass, task2)
            ],
            Signals = [new SignalDefinition("sigA", "journal-signal-a"), new SignalDefinition("sigB", "journal-signal-b")]
        };
        var grain = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await grain.SetWorkflow(workflow);
        await grain.StartWorkflow();
        var instanceId = grain.GetPrimaryKey();
        var opId = GrainFactoryRetryExtensions.ChildCompletedOpId(Guid.NewGuid(), "absent-call");
        var gates = FaultInjectingEventStore.HoldNextAppends(instanceId, 2);

        // Act — catchA's write is held; a callback arrives meanwhile and its write is next.
        var complete = grain.CompleteActivity("task", new ExpandoObject());
        await gates[0].Entered.Task.WaitAsync(Timeout);
        var callback = grain.OnChildWorkflowCompleted(opId, "absent-call", new ExpandoObject());
        await Task.Delay(300);
        gates[0].Release.SetResult();

        // The callback's write is held while catchB's write queues behind it, so the callback is in
        // the queue before `pass` completes and the turn ends.
        await gates[1].Entered.Task.WaitAsync(Timeout);
        await Task.Delay(300);
        gates[1].Release.SetResult();

        await complete.WaitAsync(Timeout);
        await callback.WaitAsync(Timeout);

        // Assert — one spawn per sequence-flow transition.
        var journal = await Journal(instanceId);
        Assert.AreEqual(1, journal.OfType<ActivitySpawned>().Count(e => e.ActivityId == "pass"));
        Assert.AreEqual(1, journal.OfType<ActivitySpawned>().Count(e => e.ActivityId == "task2"),
            "pass → task2 must be transitioned once");
        CollectionAssert.Contains((await grain.GetAppliedOperationKeys()).ToList(), opId,
            "The callback must have been processed in this turn");
    }

    // #845: a failed append makes Orleans re-read the state and swap State for the new object; the
    // aggregate kept the old one, so State silently lacked the batch the retry then appended. The
    // activation now rejects the call and deactivates; the next one replays the journal.
    [TestMethod]
    public async Task FailedAppend_RetiresActivation_NextActivationReplaysJournal()
    {
        // Arrange
        var grain = await StartedDeployedInstance();
        var instanceId = grain.GetPrimaryKey();
        FaultInjectingEventStore.FailNextAppends(instanceId, 1);

        // Act — Orleans retries the append after the re-read, so the batch is stored once.
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => grain.CompleteActivity("task", new ExpandoObject()));

        // Assert
        var journal = await Journal(instanceId);
        Assert.AreEqual(1, journal.OfType<WorkflowCompleted>().Count(), "The retried append stores the batch once");

        // Calls reach the new activation once the old one has deactivated.
        var deadline = DateTime.UtcNow + Timeout;
        while ((await grain.GetActiveActivities()).Count > 0 && DateTime.UtcNow < deadline)
            await Task.Delay(100);
        Assert.AreEqual(0, (await grain.GetActiveActivities()).Count,
            "The next activation replays the journal, where the workflow completed");

        // The read model skipped the write from the stale State and caught up on activation.
        var snapshot = await WaitForCondition(instanceId, s => s.IsCompleted);
        Assert.AreEqual(0, snapshot.ActiveActivities.Count);
    }
}
