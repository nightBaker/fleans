using Fleans.Domain.Activities;
using Fleans.Domain.Aggregates;
using Fleans.Domain.Events;
using Fleans.Domain.Sequences;
using Fleans.Domain.States;

namespace Fleans.Domain.Tests;

/// <summary>
/// Unit tests for the Transaction Hazard path (BPMN §10.4.3):
/// InitiateTransactionHazardFlowIfNeeded, ActivatePendingHazardBoundaries,
/// and the FailActivity suppression guard.
/// </summary>
[TestClass]
public class TransactionHazardDomainTests
{
    // ── Definition builders ───────────────────────────────────────────────────

    /// Simple TX: process → tx(task-b) → end. No error boundary on TX host.
    private static (WorkflowDefinition def, WorkflowExecution exec, WorkflowInstanceState state)
        CreateSimpleTx()
    {
        var taskB = new ScriptTask("task-b", "return 1;");
        var tx = new Transaction("tx") { Activities = [taskB] };
        var start = new StartEvent("start");
        var end = new EndEvent("end");
        var def = new WorkflowDefinition
        {
            WorkflowId = "test", ProcessDefinitionId = "pd1",
            Activities = [start, tx, end],
            SequenceFlows = [new("s1", start, tx), new("s2", tx, end)]
        };
        var state = new WorkflowInstanceState();
        var exec = new WorkflowExecution(state, def);
        exec.Start();
        exec.ClearUncommittedEvents();
        return (def, exec, state);
    }

    /// TX with catch-all error boundary on host: process → tx(task-a, task-b) + errBoundary → end / recovery
    private static (WorkflowDefinition def, WorkflowExecution exec, WorkflowInstanceState state)
        CreateTxWithErrorBoundary()
    {
        var taskA = new ScriptTask("task-a", "return 1;");
        var taskB = new ScriptTask("task-b", "return 1;");
        var tx = new Transaction("tx") { Activities = [taskA, taskB] };
        var start = new StartEvent("start");
        var end = new EndEvent("end");
        var errBoundary = new BoundaryErrorEvent("err-boundary", "tx", null);
        var recovery = new TaskActivity("recovery");
        var def = new WorkflowDefinition
        {
            WorkflowId = "test", ProcessDefinitionId = "pd1",
            Activities = [start, tx, end, errBoundary, recovery],
            SequenceFlows = [new("s1", start, tx), new("s2", tx, end), new("s3", errBoundary, recovery)]
        };
        var state = new WorkflowInstanceState();
        var exec = new WorkflowExecution(state, def);
        exec.Start();
        exec.ClearUncommittedEvents();
        return (def, exec, state);
    }

    // ── Scenario A: single failing child ─────────────────────────────────────

    [TestMethod]
    public void ScenarioA_SingleChildFails_HazardOutcomeAndCompWalkStarted()
    {
        var (_, exec, state) = CreateSimpleTx();
        var wfId = state.Id;
        var txId = Guid.NewGuid();
        var taskBId = Guid.NewGuid();

        var txEntry = new ActivityInstanceEntry(txId, "tx", wfId);
        var taskBEntry = new ActivityInstanceEntry(taskBId, "task-b", wfId, scopeId: txId);
        state.AddEntries([txEntry, taskBEntry]);
        state.FailEntry(taskBId, "500", "task-b failed");

        exec.InitiateTransactionHazardFlowIfNeeded();

        var events = exec.GetUncommittedEvents();
        Assert.IsTrue(events.OfType<TransactionOutcomeSet>().Any(e =>
            e.TransactionInstanceId == txId
            && e.Outcome == TransactionOutcome.Hazard
            && e.ErrorCode == "500"),
            "TransactionOutcomeSet(Hazard) with correct error code must be emitted");
        Assert.IsTrue(events.OfType<CompensationWalkStarted>().Any(),
            "CompensationWalkStarted must be emitted after hazard is detected");
    }

    [TestMethod]
    public void ScenarioA_HazardNotDetected_WhenChildStillActive()
    {
        var (_, exec, state) = CreateSimpleTx();
        var wfId = state.Id;
        var txId = Guid.NewGuid();
        var taskBId = Guid.NewGuid();

        var txEntry = new ActivityInstanceEntry(txId, "tx", wfId);
        var taskBEntry = new ActivityInstanceEntry(taskBId, "task-b", wfId, scopeId: txId);
        state.AddEntries([txEntry, taskBEntry]);
        // taskBEntry left active — detection must not fire

        exec.InitiateTransactionHazardFlowIfNeeded();

        Assert.AreEqual(0, exec.GetUncommittedEvents().Count,
            "No hazard detected while a TX child is still active");
    }

    [TestMethod]
    public void ScenarioA_HazardNotDetected_WhenAllChildrenSucceeded()
    {
        var (_, exec, state) = CreateSimpleTx();
        var wfId = state.Id;
        var txId = Guid.NewGuid();
        var taskBId = Guid.NewGuid();

        var txEntry = new ActivityInstanceEntry(txId, "tx", wfId);
        var taskBEntry = new ActivityInstanceEntry(taskBId, "task-b", wfId, scopeId: txId);
        state.AddEntries([txEntry, taskBEntry]);
        state.CompleteEntries([taskBEntry]);

        exec.InitiateTransactionHazardFlowIfNeeded();

        Assert.IsFalse(exec.GetUncommittedEvents().OfType<TransactionOutcomeSet>().Any(e =>
            e.Outcome == TransactionOutcome.Hazard),
            "No hazard when all children completed successfully (no ErrorCode)");
    }

    [TestMethod]
    public void ScenarioA_HazardNotDetected_WhenOutcomeAlreadyHazard()
    {
        var (_, exec, state) = CreateSimpleTx();
        var wfId = state.Id;
        var txId = Guid.NewGuid();
        var taskBId = Guid.NewGuid();

        var txEntry = new ActivityInstanceEntry(txId, "tx", wfId);
        var taskBEntry = new ActivityInstanceEntry(taskBId, "task-b", wfId, scopeId: txId);
        state.AddEntries([txEntry, taskBEntry]);
        state.FailEntry(taskBId, "500", "first failure");
        exec.SetTransactionOutcomeHazard(txId, "500", "first failure");
        exec.ClearUncommittedEvents();

        exec.InitiateTransactionHazardFlowIfNeeded();

        Assert.AreEqual(0, exec.GetUncommittedEvents().Count,
            "No duplicate hazard detection when outcome is already Hazard");
    }

    // ── Scenario B: FailActivity suppression + sibling cancellation ───────────

    [TestMethod]
    public void ScenarioB_FailActivity_Suppression_EmitsActivityFailed_AndCancelsSibling()
    {
        var (_, exec, state) = CreateTxWithErrorBoundary();
        var wfId = state.Id;
        var txId = Guid.NewGuid();
        var taskAId = Guid.NewGuid();
        var taskBId = Guid.NewGuid();

        var txEntry = new ActivityInstanceEntry(txId, "tx", wfId);
        var taskAEntry = new ActivityInstanceEntry(taskAId, "task-a", wfId, scopeId: txId);
        var taskBEntry = new ActivityInstanceEntry(taskBId, "task-b", wfId, scopeId: txId);
        state.AddEntries([txEntry, taskAEntry, taskBEntry]);

        exec.FailActivity("task-b", taskBId, new Exception("task-b failed"));

        var events = exec.GetUncommittedEvents();
        Assert.IsTrue(events.OfType<ActivityFailed>().Any(e => e.ActivityInstanceId == taskBId),
            "ActivityFailed(task-b) must be emitted");
        Assert.IsTrue(events.OfType<ActivityCancelled>().Any(e => e.ActivityInstanceId == taskAId),
            "ActivityCancelled(task-a) must be emitted by suppression sibling cancellation");
        Assert.IsFalse(events.OfType<ActivitySpawned>().Any(e => e.ActivityId == "err-boundary"),
            "Error boundary must NOT be spawned immediately — suppressed pending comp walk");
    }

    [TestMethod]
    public void ScenarioB_AfterSuppression_AllTxScopeEntriesAreCompleted()
    {
        var (_, exec, state) = CreateTxWithErrorBoundary();
        var wfId = state.Id;
        var txId = Guid.NewGuid();
        var taskAId = Guid.NewGuid();
        var taskBId = Guid.NewGuid();

        var txEntry = new ActivityInstanceEntry(txId, "tx", wfId);
        var taskAEntry = new ActivityInstanceEntry(taskAId, "task-a", wfId, scopeId: txId);
        var taskBEntry = new ActivityInstanceEntry(taskBId, "task-b", wfId, scopeId: txId);
        state.AddEntries([txEntry, taskAEntry, taskBEntry]);

        exec.FailActivity("task-b", taskBId, new Exception("task-b failed"));

        Assert.IsTrue(taskAEntry.IsCompleted, "task-a must be completed (cancelled) by suppression block");
        Assert.IsTrue(taskBEntry.IsCompleted, "task-b must be completed (failed) by FailActivity");
    }

    [TestMethod]
    public void ScenarioB_AfterSuppression_HazardFlowDetectsHazard()
    {
        var (_, exec, state) = CreateTxWithErrorBoundary();
        var wfId = state.Id;
        var txId = Guid.NewGuid();
        var taskAId = Guid.NewGuid();
        var taskBId = Guid.NewGuid();

        var txEntry = new ActivityInstanceEntry(txId, "tx", wfId);
        var taskAEntry = new ActivityInstanceEntry(taskAId, "task-a", wfId, scopeId: txId);
        var taskBEntry = new ActivityInstanceEntry(taskBId, "task-b", wfId, scopeId: txId);
        state.AddEntries([txEntry, taskAEntry, taskBEntry]);

        exec.FailActivity("task-b", taskBId, new Exception("task-b failed"));
        exec.ClearUncommittedEvents();

        exec.InitiateTransactionHazardFlowIfNeeded();

        Assert.IsTrue(exec.GetUncommittedEvents().OfType<TransactionOutcomeSet>().Any(e =>
            e.TransactionInstanceId == txId && e.Outcome == TransactionOutcome.Hazard),
            "Hazard flow detects TX Hazard after FailActivity suppression");
    }

    // ── Scenario C: ActivatePendingHazardBoundaries — boundary found ──────────

    [TestMethod]
    public void ScenarioC_AfterCompWalk_BoundaryFound_CancelsTxHost_SpawnsBoundary()
    {
        var (_, exec, state) = CreateTxWithErrorBoundary();
        var wfId = state.Id;
        var txId = Guid.NewGuid();
        var taskAId = Guid.NewGuid();
        var taskBId = Guid.NewGuid();

        var txEntry = new ActivityInstanceEntry(txId, "tx", wfId);
        var taskAEntry = new ActivityInstanceEntry(taskAId, "task-a", wfId, scopeId: txId);
        var taskBEntry = new ActivityInstanceEntry(taskBId, "task-b", wfId, scopeId: txId);
        state.AddEntries([txEntry, taskAEntry, taskBEntry]);
        state.CompleteEntries([taskAEntry]);
        state.FailEntry(taskBId, "500", "task-b failed");

        exec.SetTransactionOutcomeHazard(txId, "500", "task-b failed");
        exec.ClearUncommittedEvents();
        // No ActiveCompensationWalks — comp walk already completed

        exec.ActivatePendingHazardBoundaries();

        var events = exec.GetUncommittedEvents();
        Assert.IsTrue(events.OfType<ActivityCancelled>().Any(e => e.ActivityInstanceId == txId),
            "TX host must be cancelled when error boundary is found");
        Assert.IsTrue(events.OfType<ActivitySpawned>().Any(e => e.ActivityId == "err-boundary"),
            "Error boundary must be spawned in parent scope");
    }

    [TestMethod]
    public void ScenarioC_PendingBoundaries_NotActivated_WhileCompWalkActive()
    {
        var (_, exec, state) = CreateTxWithErrorBoundary();
        var wfId = state.Id;
        var txId = Guid.NewGuid();
        var taskBId = Guid.NewGuid();

        var txEntry = new ActivityInstanceEntry(txId, "tx", wfId);
        var taskBEntry = new ActivityInstanceEntry(taskBId, "task-b", wfId, scopeId: txId);
        state.AddEntries([txEntry, taskBEntry]);
        state.FailEntry(taskBId, "500", "task-b failed");

        exec.SetTransactionOutcomeHazard(txId, "500", "task-b failed");
        // Simulate an active comp walk by adding it to state
        state.StartCompensationWalk(txId, new CompensationWalkState(txId, null, taskBId));
        exec.ClearUncommittedEvents();

        exec.ActivatePendingHazardBoundaries();

        Assert.AreEqual(0, exec.GetUncommittedEvents().Count,
            "Boundary must not activate while comp walk is still in progress");
    }

    // ── Scenario D: no compensatable activities → boundary activates immediately

    [TestMethod]
    public void ScenarioD_NoCompensatableActivities_BoundaryActivatesImmediately()
    {
        var (_, exec, state) = CreateTxWithErrorBoundary();
        var wfId = state.Id;
        var txId = Guid.NewGuid();
        var taskBId = Guid.NewGuid();

        var txEntry = new ActivityInstanceEntry(txId, "tx", wfId);
        var taskBEntry = new ActivityInstanceEntry(taskBId, "task-b", wfId, scopeId: txId);
        state.AddEntries([txEntry, taskBEntry]);
        state.FailEntry(taskBId, "500", "task-b failed");

        exec.SetTransactionOutcomeHazard(txId, "500", "task-b failed");
        exec.ClearUncommittedEvents();
        // No active comp walk (no compensatable activities — walk would complete instantly)

        exec.ActivatePendingHazardBoundaries();

        var events = exec.GetUncommittedEvents();
        Assert.IsTrue(events.OfType<ActivityCancelled>().Any(e => e.ActivityInstanceId == txId),
            "TX host cancelled — boundary found even with no compensatable activities");
        Assert.IsTrue(events.OfType<ActivitySpawned>().Any(e => e.ActivityId == "err-boundary"),
            "Error boundary spawned immediately when no comp walk needed");
    }

    // ── Scenario E: no boundary on TX host → TX host fails ───────────────────

    [TestMethod]
    public void ScenarioE_NoBoundaryOnTxHost_TxHostFails()
    {
        var (_, exec, state) = CreateSimpleTx();
        var wfId = state.Id;
        var txId = Guid.NewGuid();
        var taskBId = Guid.NewGuid();

        var txEntry = new ActivityInstanceEntry(txId, "tx", wfId);
        var taskBEntry = new ActivityInstanceEntry(taskBId, "task-b", wfId, scopeId: txId);
        state.AddEntries([txEntry, taskBEntry]);
        state.FailEntry(taskBId, "500", "task-b failed");

        exec.SetTransactionOutcomeHazard(txId, "500", "task-b failed");
        exec.ClearUncommittedEvents();

        exec.ActivatePendingHazardBoundaries();

        var events = exec.GetUncommittedEvents();
        Assert.IsTrue(events.OfType<ActivityFailed>().Any(e => e.ActivityInstanceId == txId),
            "TX host must be failed when no error boundary exists in parent scope");
        Assert.IsFalse(events.OfType<ActivitySpawned>().Any(),
            "No boundary spawned when none is defined");
    }

    // ── Scenario F: two children, task-A active, task-B fails ────────────────

    [TestMethod]
    public void ScenarioF_TwoChildren_TaskBFails_TaskASiblingCancelled()
    {
        var (_, exec, state) = CreateTxWithErrorBoundary();
        var wfId = state.Id;
        var txId = Guid.NewGuid();
        var taskAId = Guid.NewGuid();
        var taskBId = Guid.NewGuid();

        var txEntry = new ActivityInstanceEntry(txId, "tx", wfId);
        var taskAEntry = new ActivityInstanceEntry(taskAId, "task-a", wfId, scopeId: txId);
        var taskBEntry = new ActivityInstanceEntry(taskBId, "task-b", wfId, scopeId: txId);
        state.AddEntries([txEntry, taskAEntry, taskBEntry]);

        exec.FailActivity("task-b", taskBId, new Exception("task-b failed"));

        var events = exec.GetUncommittedEvents();
        Assert.AreEqual(1, events.OfType<ActivityFailed>().Count(e => e.ActivityInstanceId == taskBId),
            "Exactly one ActivityFailed for task-b");
        Assert.AreEqual(1, events.OfType<ActivityCancelled>().Count(e => e.ActivityInstanceId == taskAId),
            "Exactly one ActivityCancelled for task-a (sibling cancel)");

        Assert.IsTrue(taskAEntry.IsCompleted && taskAEntry.IsCancelled,
            "task-a IsCompleted+IsCancelled after sibling cancellation");
        Assert.IsTrue(taskBEntry.IsCompleted && taskBEntry.ErrorCode != null,
            "task-b IsCompleted+ErrorCode set after failure");
    }

    // ── Scenario G: error boundary inside TX catches error — no Hazard ────────

    [TestMethod]
    public void ScenarioG_ErrorBoundaryInsideTx_OnTaskItself_NormalPathFires_NoHazard()
    {
        // Error boundary is on task-b itself (inside TX), not on the TX host in parent scope.
        var taskB = new ScriptTask("task-b", "return 1;");
        var errBoundaryOnTaskB = new BoundaryErrorEvent("err-boundary-b", "task-b", null);
        var recovery = new TaskActivity("recovery-b");
        var tx = new Transaction("tx")
        {
            Activities = [taskB, errBoundaryOnTaskB, recovery],
            SequenceFlows = [new("s-rec", errBoundaryOnTaskB, recovery)]
        };
        var start = new StartEvent("start");
        var end = new EndEvent("end");
        var def = new WorkflowDefinition
        {
            WorkflowId = "test", ProcessDefinitionId = "pd1",
            Activities = [start, tx, end],
            SequenceFlows = [new("s1", start, tx), new("s2", tx, end)]
        };
        var state = new WorkflowInstanceState();
        var exec = new WorkflowExecution(state, def);
        exec.Start();
        exec.ClearUncommittedEvents();

        var wfId = state.Id;
        var txId = Guid.NewGuid();
        var taskBId = Guid.NewGuid();

        var txEntry = new ActivityInstanceEntry(txId, "tx", wfId);
        var taskBEntry = new ActivityInstanceEntry(taskBId, "task-b", wfId, scopeId: txId);
        state.AddEntries([txEntry, taskBEntry]);

        exec.FailActivity("task-b", taskBId, new Exception("task-b failed"));

        var events = exec.GetUncommittedEvents();
        Assert.IsFalse(events.OfType<TransactionOutcomeSet>().Any(e =>
            e.Outcome == TransactionOutcome.Hazard),
            "No Hazard outcome — error caught inside TX by task-b's own boundary");
        Assert.IsTrue(events.OfType<ActivitySpawned>().Any(e => e.ActivityId == "err-boundary-b"),
            "Task-b's inner error boundary fires via normal FailActivity path");
    }
}
