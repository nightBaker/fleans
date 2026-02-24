using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using NSubstitute;

namespace Fleans.Domain.Tests;

[TestClass]
public class EndEventScopeTests
{
    [TestMethod]
    public async Task ExecuteAsync_InsideSubProcess_ShouldNotCompleteWorkflow()
    {
        var innerStart = new StartEvent("sub_start");
        var innerEnd = new EndEvent("sub_end");
        var subProcess = new SubProcess("sub1")
        {
            Activities = [innerStart, innerEnd],
            SequenceFlows = [new SequenceFlow("sf1", innerStart, innerEnd)]
        };

        IWorkflowDefinition scopeDefinition = subProcess;
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(scopeDefinition);
        var (activityContext, _) = ActivityTestHelper.CreateActivityContext("sub_end");

        await innerEnd.ExecuteAsync(workflowContext, activityContext, scopeDefinition);

        await activityContext.Received(1).Complete();
        await workflowContext.DidNotReceive().Complete();
    }

    [TestMethod]
    public async Task ExecuteAsync_AtRootLevel_ShouldCompleteWorkflow()
    {
        var start = new StartEvent("start");
        var end = new EndEvent("end");
        var definition = ActivityTestHelper.CreateWorkflowDefinition(
            [start, end], [new SequenceFlow("f1", start, end)]);
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var (activityContext, _) = ActivityTestHelper.CreateActivityContext("end");

        await end.ExecuteAsync(workflowContext, activityContext, definition);

        await activityContext.Received(1).Complete();
        await workflowContext.Received(1).Complete();
    }
}
