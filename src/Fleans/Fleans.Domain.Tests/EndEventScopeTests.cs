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

        var commands = await innerEnd.ExecuteAsync(workflowContext, activityContext, scopeDefinition);

        Assert.IsTrue(commands.OfType<CompleteCommand>().Any());
        Assert.IsFalse(commands.OfType<CompleteWorkflowCommand>().Any());
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

        var commands = await end.ExecuteAsync(workflowContext, activityContext, definition);

        Assert.IsTrue(commands.OfType<CompleteCommand>().Any());
        Assert.IsTrue(commands.OfType<CompleteWorkflowCommand>().Any());
    }
}
