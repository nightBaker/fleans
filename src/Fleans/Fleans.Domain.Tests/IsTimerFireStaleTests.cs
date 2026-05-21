using Fleans.Domain.Activities;
using Fleans.Domain.Aggregates;
using Fleans.Domain.Sequences;
using Fleans.Domain.States;

namespace Fleans.Domain.Tests;

/// <summary>
/// #658 — unit tests for <see cref="WorkflowExecution.IsTimerFireStale"/>.
///
/// Covers the 2 × N matrix from the round-3 plan:
///   - Regular timer × {active host, completed host, missing host}
///   - ESP timer × {SubProcess-scoped active container, SubProcess-scoped completed container,
///                  root-scope workflow active, root-scope workflow completed}
/// </summary>
[TestClass]
public class IsTimerFireStaleTests
{
    private static readonly Guid WorkflowInstanceId = Guid.NewGuid();

    // ---------- Regular timer (intermediate catch / boundary) ----------

    [TestMethod]
    public void RegularTimer_ActiveHost_ReturnsFalse()
    {
        var (execution, hostId) = BuildExecutionWithRegularTimer(active: true);

        Assert.IsFalse(execution.IsTimerFireStale("timer1", hostId));
    }

    [TestMethod]
    public void RegularTimer_CompletedHost_ReturnsTrue()
    {
        var (execution, hostId) = BuildExecutionWithRegularTimer(active: false);

        Assert.IsTrue(execution.IsTimerFireStale("timer1", hostId));
    }

    [TestMethod]
    public void RegularTimer_MissingHost_ReturnsTrue()
    {
        var (execution, _) = BuildExecutionWithRegularTimer(active: true);

        Assert.IsTrue(execution.IsTimerFireStale("timer1", Guid.NewGuid()));
    }

    // ---------- ESP timer, SubProcess-scoped (container is a host entry) ----------

    [TestMethod]
    public void EspTimer_SubProcessScoped_ActiveContainer_ReturnsFalse()
    {
        var (execution, containerId) = BuildExecutionWithSubProcessScopedEsp(containerCompleted: false);

        Assert.IsFalse(execution.IsTimerFireStale("evtSub1_timerStart", containerId));
    }

    [TestMethod]
    public void EspTimer_SubProcessScoped_CompletedContainer_ReturnsTrue()
    {
        var (execution, containerId) = BuildExecutionWithSubProcessScopedEsp(containerCompleted: true);

        Assert.IsTrue(execution.IsTimerFireStale("evtSub1_timerStart", containerId));
    }

    // ---------- ESP timer, root-scope (host == _state.Id) ----------

    [TestMethod]
    public void EspTimer_RootScope_WorkflowActive_ReturnsFalse()
    {
        var (execution, state) = BuildExecutionWithRootScopeEsp(workflowCompleted: false);

        Assert.IsFalse(execution.IsTimerFireStale("evtSub1_timerStart", state.Id));
    }

    [TestMethod]
    public void EspTimer_RootScope_WorkflowCompleted_ReturnsTrue()
    {
        var (execution, state) = BuildExecutionWithRootScopeEsp(workflowCompleted: true);

        Assert.IsTrue(execution.IsTimerFireStale("evtSub1_timerStart", state.Id));
    }

    // ---------- Fixture builders ----------

    private static (WorkflowExecution execution, Guid hostId) BuildExecutionWithRegularTimer(bool active)
    {
        var start = new StartEvent("start1");
        var timer = new TimerIntermediateCatchEvent("timer1", new TimerDefinition(TimerType.Duration, "PT5M"));
        var end = new EndEvent("end1");
        var definition = new WorkflowDefinition
        {
            WorkflowId = "wf-regular",
            ProcessDefinitionId = "pd1",
            Activities = [start, timer, end],
            SequenceFlows = [new SequenceFlow("seq1", start, timer), new SequenceFlow("seq2", timer, end)],
        };
        var state = new WorkflowInstanceState();
        var execution = new WorkflowExecution(state, definition);

        var hostEntry = new ActivityInstanceEntry(Guid.NewGuid(), "timer1", state.Id);
        state.AddEntries([hostEntry]);
        hostEntry.Execute();
        if (!active) hostEntry.Complete();

        return (execution, hostEntry.ActivityInstanceId);
    }

    private static (WorkflowExecution execution, Guid containerId) BuildExecutionWithSubProcessScopedEsp(bool containerCompleted)
    {
        var timerStart = new TimerStartEvent("evtSub1_timerStart", new TimerDefinition(TimerType.Duration, "PT5M"));
        var handler = new ScriptTask("evtSub1_handler", "return 1;");
        var evtEnd = new EndEvent("evtSub1_end");
        var esp = new EventSubProcess("evtSub1")
        {
            Activities = [timerStart, handler, evtEnd],
            SequenceFlows =
            [
                new SequenceFlow("evtSub1_sf1", timerStart, handler),
                new SequenceFlow("evtSub1_sf2", handler, evtEnd),
            ],
            IsInterrupting = true,
        };

        // Enclosing SubProcess that "hosts" the ESP — represented by its own host entry.
        var subProcess = new SubProcess("sub1")
        {
            Activities = [esp],
            SequenceFlows = [],
        };
        var start = new StartEvent("start1");
        var end = new EndEvent("end1");
        var definition = new WorkflowDefinition
        {
            WorkflowId = "wf-esp-sub",
            ProcessDefinitionId = "pd1",
            Activities = [start, subProcess, end],
            SequenceFlows = [new SequenceFlow("seq1", start, subProcess), new SequenceFlow("seq2", subProcess, end)],
        };
        var state = new WorkflowInstanceState();
        var execution = new WorkflowExecution(state, definition);

        var containerEntry = new ActivityInstanceEntry(Guid.NewGuid(), "sub1", state.Id);
        state.AddEntries([containerEntry]);
        containerEntry.Execute();
        if (containerCompleted) containerEntry.Complete();

        return (execution, containerEntry.ActivityInstanceId);
    }

    private static (WorkflowExecution execution, WorkflowInstanceState state) BuildExecutionWithRootScopeEsp(bool workflowCompleted)
    {
        var timerStart = new TimerStartEvent("evtSub1_timerStart", new TimerDefinition(TimerType.Duration, "PT5M"));
        var handler = new ScriptTask("evtSub1_handler", "return 1;");
        var evtEnd = new EndEvent("evtSub1_end");
        var esp = new EventSubProcess("evtSub1")
        {
            Activities = [timerStart, handler, evtEnd],
            SequenceFlows =
            [
                new SequenceFlow("evtSub1_sf1", timerStart, handler),
                new SequenceFlow("evtSub1_sf2", handler, evtEnd),
            ],
            IsInterrupting = true,
        };
        var start = new StartEvent("start1");
        var end = new EndEvent("end1");
        var definition = new WorkflowDefinition
        {
            WorkflowId = "wf-esp-root",
            ProcessDefinitionId = "pd1",
            Activities = [start, end, esp],
            SequenceFlows = [new SequenceFlow("seq1", start, end)],
        };
        var state = new WorkflowInstanceState();
        var execution = new WorkflowExecution(state, definition);

        if (workflowCompleted) state.Complete();

        return (execution, state);
    }
}
