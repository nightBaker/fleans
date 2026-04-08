using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;

namespace Fleans.Domain.Tests;

[TestClass]
public class WorkflowDefinitionErrorEventSubProcessTests
{
    private static EventSubProcess BuildErrorEventSubProcess(string id, string? errorCode)
    {
        var start = new ErrorStartEvent($"{id}_start", errorCode);
        var handler = new ScriptTask($"{id}_handler", "// noop");
        var end = new EndEvent($"{id}_end");
        return new EventSubProcess(id)
        {
            Activities = [start, handler, end],
            SequenceFlows =
            [
                new SequenceFlow($"{id}_sf1", start, handler),
                new SequenceFlow($"{id}_sf2", handler, end)
            ],
            IsInterrupting = true
        };
    }

    [TestMethod]
    public void ReturnsNull_WhenNoErrorEventSubProcessPresent()
    {
        var start = new StartEvent("start1");
        var task1 = new TaskActivity("task1");
        var end = new EndEvent("end1");
        IWorkflowDefinition definition = new WorkflowDefinition
        {
            WorkflowId = "wf1",
            Activities = [start, task1, end],
            SequenceFlows = [new SequenceFlow("sf1", start, task1), new SequenceFlow("sf2", task1, end)]
        };

        var result = definition.FindErrorEventSubProcessHandler("task1", "500");

        Assert.IsNull(result);
    }

    [TestMethod]
    public void MatchesInSameScope_ByErrorCode()
    {
        var task1 = new TaskActivity("task1");
        var esp = BuildErrorEventSubProcess("esp1", "500");
        IWorkflowDefinition definition = new WorkflowDefinition
        {
            WorkflowId = "wf1",
            Activities = [task1, esp],
            SequenceFlows = []
        };

        var result = definition.FindErrorEventSubProcessHandler("task1", "500");

        Assert.IsNotNull(result);
        Assert.AreSame(esp, result.Value.EventSubProcess);
        Assert.AreSame(definition, result.Value.EnclosingScope);
    }

    [TestMethod]
    public void CatchAll_MatchesAnyErrorCode()
    {
        var task1 = new TaskActivity("task1");
        var esp = BuildErrorEventSubProcess("esp1", null);
        IWorkflowDefinition definition = new WorkflowDefinition
        {
            WorkflowId = "wf1",
            Activities = [task1, esp],
            SequenceFlows = []
        };

        var result = definition.FindErrorEventSubProcessHandler("task1", "999");

        Assert.IsNotNull(result);
        Assert.AreSame(esp, result.Value.EventSubProcess);
    }

    [TestMethod]
    public void SpecificCodeBeatsCatchAllInSameScope()
    {
        var task1 = new TaskActivity("task1");
        var catchAll = BuildErrorEventSubProcess("espCatchAll", null);
        var specific = BuildErrorEventSubProcess("espSpecific", "500");
        IWorkflowDefinition definition = new WorkflowDefinition
        {
            WorkflowId = "wf1",
            Activities = [task1, catchAll, specific],
            SequenceFlows = []
        };

        var result = definition.FindErrorEventSubProcessHandler("task1", "500");

        Assert.IsNotNull(result);
        Assert.AreSame(specific, result.Value.EventSubProcess);
    }

    [TestMethod]
    public void BubblesUpThroughSubProcess()
    {
        var innerStart = new StartEvent("innerStart");
        var task1 = new TaskActivity("task1");
        var sub1 = new SubProcess("sub1")
        {
            Activities = [innerStart, task1],
            SequenceFlows = [new SequenceFlow("sf1", innerStart, task1)]
        };
        var esp = BuildErrorEventSubProcess("espOuter", "500");
        IWorkflowDefinition definition = new WorkflowDefinition
        {
            WorkflowId = "wf1",
            Activities = [sub1, esp],
            SequenceFlows = []
        };

        var result = definition.FindErrorEventSubProcessHandler("task1", "500");

        Assert.IsNotNull(result);
        Assert.AreSame(esp, result.Value.EventSubProcess);
        Assert.AreSame(definition, result.Value.EnclosingScope);
    }

    [TestMethod]
    public void InnerScopeBeatsOuterScope()
    {
        var innerStart = new StartEvent("innerStart");
        var task1 = new TaskActivity("task1");
        var innerEsp = BuildErrorEventSubProcess("espInner", "500");
        var sub1 = new SubProcess("sub1")
        {
            Activities = [innerStart, task1, innerEsp],
            SequenceFlows = [new SequenceFlow("sf1", innerStart, task1)]
        };
        var outerEsp = BuildErrorEventSubProcess("espOuter", "500");
        IWorkflowDefinition definition = new WorkflowDefinition
        {
            WorkflowId = "wf1",
            Activities = [sub1, outerEsp],
            SequenceFlows = []
        };

        var result = definition.FindErrorEventSubProcessHandler("task1", "500");

        Assert.IsNotNull(result);
        Assert.AreSame(innerEsp, result.Value.EventSubProcess);
        Assert.AreSame(sub1, result.Value.EnclosingScope);
    }
}
