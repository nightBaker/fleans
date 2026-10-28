using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using NSubstitute;

namespace Fleans.Domain.Tests;

[TestClass]
public class SubProcessActivityTests
{
    [TestMethod]
    public async Task ExecuteAsync_ShouldCallExecuteButNotComplete()
    {
        var innerStart = new StartEvent("sub_start");
        var innerEnd = new EndEvent("sub_end");
        var subProcess = new SubProcess("sub1")
        {
            Activities = [innerStart, innerEnd],
            SequenceFlows = [new SequenceFlow("sf1", innerStart, innerEnd)]
        };

        var definition = ActivityTestHelper.CreateWorkflowDefinition([subProcess], []);
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var (activityContext, _) = ActivityTestHelper.CreateActivityContext("sub1");

        var commands = await subProcess.ExecuteAsync(workflowContext, activityContext, definition);

        await activityContext.Received(1).Execute();
        await activityContext.DidNotReceive().Complete();
        var subCmd = commands.OfType<OpenSubProcessCommand>().Single();
        Assert.AreEqual(subProcess, subCmd.SubProcess);
    }

    [TestMethod]
    public async Task GetNextActivities_ShouldReturnOutgoingFlowsFromParentDefinition()
    {
        var innerStart = new StartEvent("sub_start");
        var innerEnd = new EndEvent("sub_end");
        var subProcess = new SubProcess("sub1")
        {
            Activities = [innerStart, innerEnd],
            SequenceFlows = [new SequenceFlow("inner_f1", innerStart, innerEnd)]
        };
        var endEvent = new EndEvent("end");

        var definition = ActivityTestHelper.CreateWorkflowDefinition(
            [subProcess, endEvent],
            [new SequenceFlow("f1", subProcess, endEvent)]);
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var (activityContext, _) = ActivityTestHelper.CreateActivityContext("sub1");

        var nextActivities = await subProcess.GetNextActivities(workflowContext, activityContext, definition);

        Assert.AreEqual(1, nextActivities.Count);
        Assert.AreEqual("end", nextActivities[0].NextActivity.ActivityId);
    }

    [TestMethod]
    public async Task ExecuteAsync_WhenExecuteFails_ShouldPropagateException()
    {
        var innerStart = new StartEvent("sub_start");
        var innerEnd = new EndEvent("sub_end");
        var subProcess = new SubProcess("sub1")
        {
            Activities = [innerStart, innerEnd],
            SequenceFlows = [new SequenceFlow("sf1", innerStart, innerEnd)]
        };

        var definition = ActivityTestHelper.CreateWorkflowDefinition([subProcess], []);
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var (activityContext, _) = ActivityTestHelper.CreateActivityContext("sub1");
        activityContext.Execute().Returns(ValueTask.FromException(new InvalidOperationException("exec failed")));

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => subProcess.ExecuteAsync(workflowContext, activityContext, definition));
    }
}
