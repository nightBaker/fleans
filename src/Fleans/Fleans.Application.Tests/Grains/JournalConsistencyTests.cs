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
/// journal writes interleave or fail (#869). The tests drive the ways it used to go wrong: a batch
/// applied twice by Orleans after an interleaved enqueue (#846), a queued callback that
/// re-transitioned the turn's completions (#837), and a re-read after a failed append that left the
/// aggregate on the old state object (#845). Interleavings are ordered by gates on the event store
/// and by log entries the grain writes just before <c>RaiseEvent</c>, not by delays.
/// </summary>
[TestClass]
[DoNotParallelize] // CapturedLogEvents.Capture is process-wide
public class JournalConsistencyTests : WorkflowTestBase
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private const int WorkflowCompletedEventId = 3002;
    private const int ProcessingPendingEventEventId = 1074;
    private const int StateReloadedEventId = 1137;
    private const int AppendRetryKeepsStateEventId = 1138;
    private const int PendingEventQueuedEventId = 1142;

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

    /// <summary>Waits until a captured entry with <paramref name="eventId"/> matches.</summary>
    private static async Task WaitForLog(int eventId, Func<CapturedLogEvents.Entry, bool> match)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!CapturedLogEvents.Entries(eventId).Any(match))
        {
            Assert.IsTrue(DateTime.UtcNow < deadline, $"No log entry {eventId} within {Timeout}");
            await Task.Delay(20);
        }
    }

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
        using var capture = CapturedLogEvents.Capture(WorkflowCompletedEventId, PendingEventQueuedEventId);
        var gates = FaultInjectingEventStore.HoldNextAppends(instanceId, 2);

        // Act — the enqueue's append (write cycle 1) is held at the store.
        var enqueue = grain.OnChildWorkflowCompleted(opId, "absent-call", new ExpandoObject());
        await gates[0].Entered.Task.WaitAsync(Timeout);

        // The main turn raises its batch (logged just before RaiseEvent) and waits for cycle 2.
        var complete = grain.CompleteActivity("task", new ExpandoObject());
        await WaitForLog(WorkflowCompletedEventId, _ => true);

        // Cycle 1 commits: the enqueue's confirm returns while cycle 2 is still appending.
        gates[0].Release.SetResult();
        var cycle2 = await gates[1].Entered.Task.WaitAsync(Timeout);
        Assert.AreEqual(1, cycle2.OfType<WorkflowCompleted>().Count(), "Cycle 2 carries the main turn's batch");
        await WaitForLog(PendingEventQueuedEventId, e => e.Message.Contains(opId));
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
        using var capture = CapturedLogEvents.Capture(ProcessingPendingEventEventId);
        var gates = FaultInjectingEventStore.HoldNextAppends(instanceId, 2);

        // Act — catchA's write is held; a callback arrives meanwhile and raises its enqueue.
        var complete = grain.CompleteActivity("task", new ExpandoObject());
        await gates[0].Entered.Task.WaitAsync(Timeout);
        var callback = grain.OnChildWorkflowCompleted(opId, "absent-call", new ExpandoObject());
        await WaitForLog(ProcessingPendingEventEventId, e => e.Message.Contains(nameof(PendingEventEnqueued)));

        // The callback's write is cycle 2, so catchB's write comes after it and the callback is
        // queued before `pass` completes and the turn ends.
        gates[0].Release.SetResult();
        var cycle2 = await gates[1].Entered.Task.WaitAsync(Timeout);
        Assert.IsTrue(cycle2.OfType<PendingEventEnqueued>().Any(), "Cycle 2 is the callback's enqueue");
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
    // aggregate kept the old one, so State silently lacked the batch the retry then appended. When
    // nothing was stored meanwhile, the live State is kept and the call succeeds.
    [TestMethod]
    public async Task FailedAppend_JournalUnchanged_KeepsLiveState()
    {
        // Arrange
        var grain = await StartedDeployedInstance();
        var instanceId = grain.GetPrimaryKey();
        using var capture = CapturedLogEvents.Capture(StateReloadedEventId, AppendRetryKeepsStateEventId);
        FaultInjectingEventStore.FailNextAppends(instanceId, 1);

        // Act — Orleans re-reads, then retries the append.
        await grain.CompleteActivity("task", new ExpandoObject());

        // Assert — stored once, and the live state is the journal's.
        Assert.AreEqual(1, CapturedLogEvents.Entries(AppendRetryKeepsStateEventId).Count);
        Assert.AreEqual(0, CapturedLogEvents.Entries(StateReloadedEventId).Count);
        var journal = await Journal(instanceId);
        Assert.AreEqual(1, journal.OfType<WorkflowCompleted>().Count(), "The retried append stores the batch once");
        Assert.AreEqual(0, (await grain.GetActiveActivities()).Count);
        var snapshot = await WaitForCondition(instanceId, s => s.IsCompleted);
        Assert.AreEqual(0, snapshot.ActiveActivities.Count);

        await ForceAllGrainDeactivation(instanceId);
        Assert.AreEqual(0, (await grain.GetActiveActivities()).Count);
    }

    // #845: when the journal moved on during the failed append, the aggregate is out of date. The
    // batch still lands (Orleans appends it on top); the activation retires, and the next one
    // replays the journal and catches the read model up.
    [TestMethod]
    public async Task FailedAppend_JournalMoved_RetiresActivation()
    {
        // Arrange
        var grain = await StartedDeployedInstance();
        var instanceId = grain.GetPrimaryKey();
        using var capture = CapturedLogEvents.Capture(StateReloadedEventId);
        FaultInjectingEventStore.FailNextAppendAfterForeignWrite(instanceId, new VariableScopesRemoved([]));

        // Act
        await grain.CompleteActivity("task", new ExpandoObject());

        // Assert
        Assert.AreEqual(1, CapturedLogEvents.Entries(StateReloadedEventId).Count);
        var journal = await Journal(instanceId);
        Assert.AreEqual(1, journal.OfType<VariableScopesRemoved>().Count());
        Assert.AreEqual(1, journal.OfType<WorkflowCompleted>().Count(), "The batch is appended after the foreign write");

        // Calls reach the next activation once the retired one has deactivated.
        var deadline = DateTime.UtcNow + Timeout;
        while ((await grain.GetActiveActivities()).Count > 0 && DateTime.UtcNow < deadline)
            await Task.Delay(100);
        Assert.AreEqual(0, (await grain.GetActiveActivities()).Count,
            "The next activation replays the journal, where the workflow completed");

        var snapshot = await WaitForCondition(instanceId, s => s.IsCompleted);
        Assert.AreEqual(0, snapshot.ActiveActivities.Count);
    }
}
