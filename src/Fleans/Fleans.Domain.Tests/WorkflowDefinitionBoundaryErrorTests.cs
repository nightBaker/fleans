using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;

namespace Fleans.Domain.Tests;

[TestClass]
public class WorkflowDefinitionBoundaryErrorTests
{
    [TestMethod]
    public void FindBoundaryErrorHandler_DirectMatch_ReturnsBoundaryEvent()
    {
        var task1 = new TaskActivity("task1");
        var boundary = new BoundaryErrorEvent("boundary1", "task1", "500");
        var endEvent = new EndEvent("end1");
        IWorkflowDefinition definition = new WorkflowDefinition
        {
            WorkflowId = "wf1",
            Activities = [task1, boundary, endEvent],
            SequenceFlows = [new SequenceFlow("sf1", boundary, endEvent)]
        };

        var result = definition.FindBoundaryErrorHandler("task1", "500");

        Assert.IsNotNull(result);
        Assert.AreEqual("boundary1", result.Value.BoundaryEvent.ActivityId);
        Assert.AreEqual("task1", result.Value.AttachedToActivityId);
        Assert.AreSame(definition, result.Value.Scope);
    }

    [TestMethod]
    public void FindBoundaryErrorHandler_NoMatch_ReturnsNull()
    {
        var task1 = new TaskActivity("task1");
        IWorkflowDefinition definition = new WorkflowDefinition
        {
            WorkflowId = "wf1",
            Activities = [task1],
            SequenceFlows = []
        };

        var result = definition.FindBoundaryErrorHandler("task1", "500");

        Assert.IsNull(result);
    }

    [TestMethod]
    public void FindBoundaryErrorHandler_CatchAll_MatchesAnyErrorCode()
    {
        var task1 = new TaskActivity("task1");
        var boundary = new BoundaryErrorEvent("boundary1", "task1", null);
        var endEvent = new EndEvent("end1");
        IWorkflowDefinition definition = new WorkflowDefinition
        {
            WorkflowId = "wf1",
            Activities = [task1, boundary, endEvent],
            SequenceFlows = [new SequenceFlow("sf1", boundary, endEvent)]
        };

        var result = definition.FindBoundaryErrorHandler("task1", "999");

        Assert.IsNotNull(result);
        Assert.AreEqual("boundary1", result.Value.BoundaryEvent.ActivityId);
    }

    [TestMethod]
    public void FindBoundaryErrorHandler_SpecificCodePrioritizedOverCatchAll()
    {
        var task1 = new TaskActivity("task1");
        var catchAll = new BoundaryErrorEvent("catchAll", "task1", null);
        var specific = new BoundaryErrorEvent("specific", "task1", "500");
        var endEvent = new EndEvent("end1");
        IWorkflowDefinition definition = new WorkflowDefinition
        {
            WorkflowId = "wf1",
            Activities = [task1, catchAll, specific, endEvent],
            SequenceFlows = [new SequenceFlow("sf1", catchAll, endEvent), new SequenceFlow("sf2", specific, endEvent)]
        };

        var result = definition.FindBoundaryErrorHandler("task1", "500");

        Assert.IsNotNull(result);
        Assert.AreEqual("specific", result.Value.BoundaryEvent.ActivityId);
    }

    [TestMethod]
    public void FindBoundaryErrorHandler_CodeMismatch_ReturnsNull()
    {
        var task1 = new TaskActivity("task1");
        var boundary = new BoundaryErrorEvent("boundary1", "task1", "404");
        IWorkflowDefinition definition = new WorkflowDefinition
        {
            WorkflowId = "wf1",
            Activities = [task1, boundary],
            SequenceFlows = []
        };

        var result = definition.FindBoundaryErrorHandler("task1", "500");

        Assert.IsNull(result);
    }

    [TestMethod]
    public void FindBoundaryErrorHandler_BubbleUpThroughSubProcess()
    {
        var task1 = new TaskActivity("task1");
        var startEvent = new StartEvent("start1");
        var sub1 = new SubProcess("sub1")
        {
            Activities = [startEvent, task1],
            SequenceFlows = [new SequenceFlow("sf1", startEvent, task1)]
        };
        var boundary = new BoundaryErrorEvent("boundary1", "sub1", "500");
        var endEvent = new EndEvent("end1");
        IWorkflowDefinition definition = new WorkflowDefinition
        {
            WorkflowId = "wf1",
            Activities = [sub1, boundary, endEvent],
            SequenceFlows = [new SequenceFlow("sf2", boundary, endEvent)]
        };

        var result = definition.FindBoundaryErrorHandler("task1", "500");

        Assert.IsNotNull(result);
        Assert.AreEqual("boundary1", result.Value.BoundaryEvent.ActivityId);
        Assert.AreEqual("sub1", result.Value.AttachedToActivityId);
        Assert.AreSame(definition, result.Value.Scope);
    }

    [TestMethod]
    public void FindBoundaryErrorHandler_DirectMatchInsideSubProcess()
    {
        var task1 = new TaskActivity("task1");
        var startEvent = new StartEvent("start1");
        var boundary = new BoundaryErrorEvent("boundary1", "task1", "500");
        var endEvent = new EndEvent("end1");
        var sub1 = new SubProcess("sub1")
        {
            Activities = [startEvent, task1, boundary, endEvent],
            SequenceFlows = [new SequenceFlow("sf1", startEvent, task1), new SequenceFlow("sf2", boundary, endEvent)]
        };
        IWorkflowDefinition definition = new WorkflowDefinition
        {
            WorkflowId = "wf1",
            Activities = [sub1],
            SequenceFlows = []
        };

        var result = definition.FindBoundaryErrorHandler("task1", "500");

        Assert.IsNotNull(result);
        Assert.AreEqual("boundary1", result.Value.BoundaryEvent.ActivityId);
        Assert.AreEqual("task1", result.Value.AttachedToActivityId);
        Assert.AreSame(sub1, result.Value.Scope);
    }
}
