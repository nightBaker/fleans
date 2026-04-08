using System.Linq;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Fleans.Domain.Tests;

[TestClass]
public class WorkflowDefinitionEventSubProcessTimerTests
{
    private static EventSubProcess BuildTimerEventSubProcess(
        string id, string duration, string timerStartId, string handlerId = "handler1")
    {
        var timerStart = new TimerStartEvent(timerStartId,
            new TimerDefinition(TimerType.Duration, duration));
        var handler = new ScriptTask(handlerId, "return 1;");
        var end = new EndEvent($"{id}_end");
        return new EventSubProcess(id)
        {
            Activities = [timerStart, handler, end],
            SequenceFlows =
            [
                new SequenceFlow($"{id}_sf1", timerStart, handler),
                new SequenceFlow($"{id}_sf2", handler, end),
            ],
            IsInterrupting = true,
        };
    }

    [TestMethod]
    public void GetEventSubProcessTimers_Empty_WhenNoEventSubProcesses()
    {
        var definition = new WorkflowDefinition
        {
            WorkflowId = "wf",
            Activities = [new StartEvent("s"), new ScriptTask("t", "x"), new EndEvent("e")],
            SequenceFlows = [],
        };

        Assert.AreEqual(0, ((IWorkflowDefinition)definition).GetEventSubProcessTimers().Count());
    }

    [TestMethod]
    public void GetEventSubProcessTimers_ReturnsRootLevelTimerEventSubProcesses()
    {
        var evtSub = BuildTimerEventSubProcess("evtSub1", "PT5S", "timerStart1");
        var definition = new WorkflowDefinition
        {
            WorkflowId = "wf",
            Activities = [new StartEvent("s"), new ScriptTask("t", "x"), new EndEvent("e"), evtSub],
            SequenceFlows = [],
        };

        var timers = ((IWorkflowDefinition)definition).GetEventSubProcessTimers().ToList();

        Assert.AreEqual(1, timers.Count);
        Assert.AreEqual("evtSub1", timers[0].EventSubProcess.ActivityId);
        Assert.AreEqual("timerStart1", timers[0].TimerStart.ActivityId);
    }

    [TestMethod]
    public void GetEventSubProcessTimers_ExcludesEventSubProcessesWithNonTimerStartEvent()
    {
        var errorEsp = new EventSubProcess("errSub")
        {
            Activities =
            [
                new ErrorStartEvent("errStart", "500"),
                new ScriptTask("errHandler", "x"),
                new EndEvent("errEnd"),
            ],
            SequenceFlows = [],
        };
        var definition = new WorkflowDefinition
        {
            WorkflowId = "wf",
            Activities = [new StartEvent("s"), errorEsp],
            SequenceFlows = [],
        };

        Assert.AreEqual(0, ((IWorkflowDefinition)definition).GetEventSubProcessTimers().Count());
    }

    [TestMethod]
    public void GetEventSubProcessTimers_DoesNotRecurseIntoSubProcessScopes()
    {
        var innerEvtSub = BuildTimerEventSubProcess("innerEvtSub", "PT5S", "innerTimerStart");
        var subProcess = new SubProcess("sub1")
        {
            Activities = [new StartEvent("innerStart"), new EndEvent("innerEnd"), innerEvtSub],
            SequenceFlows = [],
        };
        var definition = new WorkflowDefinition
        {
            WorkflowId = "wf",
            Activities = [new StartEvent("s"), subProcess, new EndEvent("e")],
            SequenceFlows = [],
        };

        Assert.AreEqual(0, ((IWorkflowDefinition)definition).GetEventSubProcessTimers().Count(),
            "Root-level enumeration must not return nested event sub-process timers");
        Assert.AreEqual(1, ((IWorkflowDefinition)subProcess).GetEventSubProcessTimers().Count(),
            "Scope-level enumeration on the SubProcess returns its own timers");
    }

    [TestMethod]
    public void FindEventSubProcessByStartEvent_Matches_AtRoot()
    {
        var evtSub = BuildTimerEventSubProcess("evtSub1", "PT5S", "timerStart1");
        var definition = new WorkflowDefinition
        {
            WorkflowId = "wf",
            Activities = [new StartEvent("s"), evtSub],
            SequenceFlows = [],
        };

        var result = ((IWorkflowDefinition)definition).FindEventSubProcessByStartEvent("timerStart1");

        Assert.IsNotNull(result);
        Assert.AreEqual("evtSub1", result.Value.EventSubProcess.ActivityId);
        Assert.AreSame(definition, result.Value.EnclosingScope);
    }

    [TestMethod]
    public void FindEventSubProcessByStartEvent_Matches_InsideSubProcess()
    {
        var innerEvtSub = BuildTimerEventSubProcess("innerEvtSub", "PT5S", "innerTimerStart");
        var subProcess = new SubProcess("sub1")
        {
            Activities = [new StartEvent("innerStart"), new EndEvent("innerEnd"), innerEvtSub],
            SequenceFlows = [],
        };
        var definition = new WorkflowDefinition
        {
            WorkflowId = "wf",
            Activities = [new StartEvent("s"), subProcess],
            SequenceFlows = [],
        };

        var result = ((IWorkflowDefinition)definition).FindEventSubProcessByStartEvent("innerTimerStart");

        Assert.IsNotNull(result);
        Assert.AreEqual("innerEvtSub", result.Value.EventSubProcess.ActivityId);
        Assert.AreSame(subProcess, result.Value.EnclosingScope);
    }

    [TestMethod]
    public void FindEventSubProcessByStartEvent_ReturnsNull_WhenNotFound()
    {
        var definition = new WorkflowDefinition
        {
            WorkflowId = "wf",
            Activities = [new StartEvent("s"), new EndEvent("e")],
            SequenceFlows = [],
        };

        Assert.IsNull(((IWorkflowDefinition)definition).FindEventSubProcessByStartEvent("nope"));
    }
}
