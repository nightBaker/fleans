using Fleans.Application.Effects;
using Fleans.Application.Grains;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Events;
using Fleans.Domain.Sequences;
using System.Dynamic;

namespace Fleans.Application.Tests.Grains;

/// <summary>
/// Pending-external-event durability + op-id dedup (issue #657).
///
/// The three caller-retried RPCs (child completed/failed/escalation) take a deterministic op-id and
/// dedup via the AppliedOperations ledger; the queue is persisted via PendingEventEnqueued so it
/// survives forced deactivation. Signal paths persist with a fresh-Guid key and no dedup.
///
/// These tests drive the grain directly with a started simple workflow. When the targeted parent
/// activity is not active, the aggregate's stale guard makes the operation a no-op, but the durable
/// enqueue → drain → AppliedOperations machinery still runs — exactly the layer under test.
/// </summary>
[TestClass]
public class PendingEventsDurabilityTests : WorkflowTestBase
{
    private async Task<IWorkflowInstanceGrain> StartedSimpleInstance()
    {
        var grain = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await grain.SetWorkflow(CreateSimpleWorkflow($"pend-{Guid.NewGuid():N}"));
        await grain.StartWorkflow();
        return grain;
    }

    /// <summary>
    /// Starts an instance from a DEPLOYED definition (has a ProcessDefinitionId) so the workflow
    /// definition can be reloaded after a forced deactivation/reactivation.
    /// </summary>
    private async Task<IWorkflowInstanceGrain> StartedDeployedInstance()
    {
        var start = new StartEvent("start");
        var task = new TaskActivity("task");
        var end = new EndEvent("end");
        var workflow = new WorkflowDefinition
        {
            WorkflowId = $"pend-dep-{Guid.NewGuid():N}",
            Activities = [start, task, end],
            SequenceFlows = [new SequenceFlow("s1", start, task), new SequenceFlow("s2", task, end)]
        };

        var processGrain = Cluster.GrainFactory.GetGrain<IProcessDefinitionGrain>(workflow.WorkflowId);
        await processGrain.DeployVersion(workflow, "<xml/>");
        var grain = await processGrain.CreateInstance();
        await grain.StartWorkflow();
        return grain;
    }

    // 1 — Enqueue/drain/apply are persisted to the journal (PendingEvent* domain events), so the
    // queue survives forced deactivation rather than being lost with the in-memory queue (#657 core).
    [TestMethod]
    public async Task Enqueue_PersistsViaJournal_SurvivesReactivation()
    {
        var grain = await StartedDeployedInstance();
        var opId = GrainFactoryRetryExtensions.ChildCompletedOpId(Guid.NewGuid(), "absent-call");

        await grain.OnChildWorkflowCompleted(opId, "absent-call", new ExpandoObject());

        var appliedBefore = await grain.GetAppliedOperationKeys();
        CollectionAssert.Contains(appliedBefore.ToList(), opId, "Applied ledger should record the op");

        // The durability mechanism is journal-backed: the PendingEventEnqueued/Drained/Applied events
        // for this op-id must be in the event store. (The in-memory queue alone was the lost-on-deactivation bug.)
        var eventStore = GetSiloService<IEventStore>();
        var events = await eventStore.ReadEventsAsync(grain.GetPrimaryKey().ToString(), 0);
        Assert.IsTrue(events.OfType<PendingEventEnqueued>().Any(e => e.OpKey == opId),
            "PendingEventEnqueued must be persisted to the journal");
        Assert.IsTrue(events.OfType<PendingEventApplied>().Any(e => e.OpKey == opId),
            "PendingEventApplied must be persisted to the journal");
        Assert.IsTrue(events.OfType<PendingEventDrained>().Any(e => e.OpKey == opId),
            "PendingEventDrained must be persisted to the journal");

        // Reactivation must succeed and leave the queue empty (the awaited op already drained).
        await ForceAllGrainDeactivation();
        var grainAfter = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(grain.GetPrimaryKey());
        var pendingAfter = await grainAfter.GetPendingOperationKeys();
        Assert.AreEqual(0, pendingAfter.Count, "Drained queue stays empty across reactivation");
    }

    // 2 — A duplicate op-id short-circuits to the persisted outcome (no re-processing).
    [TestMethod]
    public async Task Enqueue_DuplicateOpId_ReturnsExistingTcs()
    {
        var grain = await StartedSimpleInstance();
        var opId = GrainFactoryRetryExtensions.ChildCompletedOpId(Guid.NewGuid(), "absent-call");

        await grain.OnChildWorkflowCompleted(opId, "absent-call", new ExpandoObject());
        // Second call with the same op-id: must return immediately via the dedup ledger.
        await grain.OnChildWorkflowCompleted(opId, "absent-call", new ExpandoObject());

        var applied = await grain.GetAppliedOperationKeys();
        Assert.AreEqual(1, applied.Count(k => k == opId), "Op-id must appear exactly once in the dedup ledger");
        var pending = await grain.GetPendingOperationKeys();
        Assert.AreEqual(0, pending.Count, "No residual pending entry after dedup");
    }

    // 3 — Value-returning escalation op: retry returns the persisted EscalationHandledResult.
    [TestMethod]
    public async Task Escalation_RetryReturnsPersistedResult()
    {
        var grain = await StartedSimpleInstance();
        var escInstanceId = Guid.NewGuid();
        var opId = GrainFactoryRetryExtensions.ChildEscalationOpId(escInstanceId);

        // No boundary in a simple workflow ⇒ Unhandled at root.
        var first = await grain.OnChildEscalationRaised(
            opId, escInstanceId, Guid.NewGuid(), "absent-host", "ESC-1", new ExpandoObject());
        Assert.AreEqual(EscalationHandledResult.Unhandled, first);

        var second = await grain.OnChildEscalationRaised(
            opId, escInstanceId, Guid.NewGuid(), "absent-host", "ESC-1", new ExpandoObject());
        Assert.AreEqual(first, second, "Retry must return the persisted escalation result");

        var applied = await grain.GetAppliedOperationKeys();
        Assert.AreEqual(1, applied.Count(k => k == opId), "Escalation op recorded exactly once");
    }

    // 4 — After force-deactivation, the grain reactivates and a retried child op still returns
    // (no hang) and is applied idempotently — fresh TCSes are minted on reactivation.
    [TestMethod]
    public async Task Reactivation_ReplaysQueueWithFreshTcs()
    {
        var grain = await StartedDeployedInstance();
        var opId = GrainFactoryRetryExtensions.ChildFailedOpId(Guid.NewGuid(), "absent-call");

        await grain.OnChildWorkflowFailed(opId, "absent-call", new Exception("boom"));
        await ForceAllGrainDeactivation();

        // Caller retries after reactivation — must complete (no hang); the absent-activity stale guard
        // keeps it idempotent regardless of whether the ledger was snapshot-covered.
        var grainAfter = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(grain.GetPrimaryKey());
        await grainAfter.OnChildWorkflowFailed(opId, "absent-call", new Exception("boom"));

        // Queue is drained and the workflow remains operable post-reactivation.
        var pending = await grainAfter.GetPendingOperationKeys();
        Assert.AreEqual(0, pending.Count, "Queue drained after retried call on reactivated grain");
    }

    // 5 — AppliedOperations is GC'd on terminal state.
    [TestMethod]
    public async Task AppliedOperations_GcOnCompletion()
    {
        var grain = await StartedSimpleInstance();
        var opId = GrainFactoryRetryExtensions.ChildCompletedOpId(Guid.NewGuid(), "absent-call");

        await grain.OnChildWorkflowCompleted(opId, "absent-call", new ExpandoObject());
        var beforeTerminal = await grain.GetAppliedOperationKeys();
        CollectionAssert.Contains(beforeTerminal.ToList(), opId);

        // Drive the workflow to terminal state.
        await grain.CompleteActivity("task", new ExpandoObject());
        var snapshot = await QueryService.GetStateSnapshot(grain.GetPrimaryKey());
        Assert.IsTrue(snapshot!.IsCompleted, "Workflow should be completed");

        var afterTerminal = await grain.GetAppliedOperationKeys();
        Assert.AreEqual(0, afterTerminal.Count, "Dedup ledger must be cleared on terminal state (Q1')");
    }

    // 6 — Repeated non-interrupting signal boundary fires each apply (no dedup on the signal path).
    [TestMethod]
    public async Task Signal_RepeatedNonInterruptingBoundary_AppliesEachFire()
    {
        var start = new StartEvent("start");
        var task = new TaskActivity("task1");
        var signalDef = new SignalDefinition("sig1", "go");
        var boundary = new SignalBoundaryEvent("bsig1", "task1", "sig1", IsInterrupting: false);
        var end = new EndEvent("end");
        var sigEnd = new EndEvent("sigEnd");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "sig-repeat",
            Activities = [start, task, boundary, end, sigEnd],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, task),
                new SequenceFlow("f2", task, end),
                new SequenceFlow("f3", boundary, sigEnd)
            ],
            Signals = [signalDef]
        };

        var grain = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await grain.SetWorkflow(workflow);
        await grain.StartWorkflow();

        var instanceId = grain.GetPrimaryKey();
        var snapshot = await QueryService.GetStateSnapshot(instanceId);
        var hostInstanceId = snapshot!.ActiveActivities.First(a => a.ActivityId == "task1").ActivityInstanceId;

        // Two non-interrupting fires — each must apply (fresh-Guid op key, no dedup).
        await grain.HandleBoundarySignalFired("bsig1", hostInstanceId);
        await grain.HandleBoundarySignalFired("bsig1", hostInstanceId);

        // Both fires drain — no residual queue, and the signal path leaves no dedup ledger entries.
        var pending = await grain.GetPendingOperationKeys();
        Assert.AreEqual(0, pending.Count, "Signal queue should be fully drained");
        var applied = await grain.GetAppliedOperationKeys();
        Assert.IsFalse(applied.Any(k => k.StartsWith("boundary-signal:")),
            "Signal path must not write to the dedup ledger");
    }

    // 7 — Distinct escalation throws of the same code get distinct op-ids ⇒ both apply.
    [TestMethod]
    public async Task Escalation_RepeatedSameCode_DistinctThrows_AppliesEach()
    {
        var grain = await StartedSimpleInstance();

        var esc1 = Guid.NewGuid();
        var esc2 = Guid.NewGuid();
        var op1 = GrainFactoryRetryExtensions.ChildEscalationOpId(esc1);
        var op2 = GrainFactoryRetryExtensions.ChildEscalationOpId(esc2);

        await grain.OnChildEscalationRaised(op1, esc1, Guid.NewGuid(), "absent-host", "ESC-SAME", new ExpandoObject());
        await grain.OnChildEscalationRaised(op2, esc2, Guid.NewGuid(), "absent-host", "ESC-SAME", new ExpandoObject());

        var applied = (await grain.GetAppliedOperationKeys()).ToList();
        CollectionAssert.Contains(applied, op1, "First distinct throw recorded");
        CollectionAssert.Contains(applied, op2, "Second distinct throw recorded");
        Assert.AreNotEqual(op1, op2, "Distinct throws must produce distinct op-ids");
    }

    // 8 — A re-escalated hop carries the same op-id ⇒ a retried hop dedups (single applied entry).
    [TestMethod]
    public async Task Escalation_ReEscalationPropagatesSameOpId_DedupAcrossHops()
    {
        var grain = await StartedSimpleInstance();
        var escInstanceId = Guid.NewGuid();
        var opId = GrainFactoryRetryExtensions.ChildEscalationOpId(escInstanceId);

        // Same originating throw id ⇒ same op-id across hops/retries.
        var r1 = await grain.OnChildEscalationRaised(
            opId, escInstanceId, Guid.NewGuid(), "absent-host", "ESC-HOP", new ExpandoObject());
        var r2 = await grain.OnChildEscalationRaised(
            opId, escInstanceId, Guid.NewGuid(), "absent-host", "ESC-HOP", new ExpandoObject());

        Assert.AreEqual(r1, r2, "Re-escalation/retry with the same op-id returns the same result");
        var applied = await grain.GetAppliedOperationKeys();
        Assert.AreEqual(1, applied.Count(k => k == opId), "Same op-id dedups across hops — single ledger entry");
    }
}
