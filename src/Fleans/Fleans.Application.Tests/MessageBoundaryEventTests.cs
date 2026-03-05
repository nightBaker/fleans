using Fleans.Application.Grains;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using System.Dynamic;

namespace Fleans.Application.Tests;

[TestClass]
public class MessageBoundaryEventTests : BoundaryEventTestBase
{
    protected override Activity CreateBoundaryEvent(string boundaryId, string attachedToId, bool isInterrupting = true)
        => new MessageBoundaryEvent(boundaryId, attachedToId, "msg1", IsInterrupting: isInterrupting);

    protected override List<MessageDefinition> GetMessageDefinitions()
        => [new MessageDefinition("msg1", "cancelOrder", "orderId")];

    protected override async Task SetupInitialState(IWorkflowInstanceGrain instance)
    {
        dynamic initVars = new ExpandoObject();
        initVars.orderId = "order-456";
        await instance.SetInitialVariables(initVars);
    }

    protected override async Task TriggerBoundaryEvent(IWorkflowInstanceGrain instance, Guid hostInstanceId)
    {
        var correlationGrain = Cluster.GrainFactory.GetGrain<IMessageCorrelationGrain>("cancelOrder");
        var delivered = await correlationGrain.DeliverMessage("order-456", new ExpandoObject());
        Assert.IsTrue(delivered, "Message should be delivered");
    }

    [TestMethod]
    public async Task BoundaryMessage_DirectCallAfterCompletion_ShouldBeSilentlyIgnored()
    {
        var start = new StartEvent("start");
        var task = new TaskActivity("task1");
        var msgDef = new MessageDefinition("msg1", "cancelOrder", "orderId");
        var boundaryMsg = new MessageBoundaryEvent("bmsg1", "task1", "msg1");
        var end = new EndEvent("end");
        var msgEnd = new EndEvent("msgEnd");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "boundary-msg-stale",
            Activities = [start, task, boundaryMsg, end, msgEnd],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, task),
                new SequenceFlow("f2", task, end),
                new SequenceFlow("f3", boundaryMsg, msgEnd)
            ],
            Messages = [msgDef]
        };

        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        dynamic initVars = new ExpandoObject();
        initVars.orderId = "order-stale";
        await workflowInstance.SetInitialVariables(initVars);
        await workflowInstance.StartWorkflow();

        var instanceId = workflowInstance.GetPrimaryKey();
        var preSnapshot = await QueryService.GetStateSnapshot(instanceId);
        var hostInstanceId = preSnapshot!.ActiveActivities
            .First(a => a.ActivityId == "task1").ActivityInstanceId;

        await workflowInstance.CompleteActivity("task1", new ExpandoObject());

        var midSnapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsTrue(midSnapshot!.IsCompleted);

        await workflowInstance.HandleBoundaryMessageFired("bmsg1", hostInstanceId);

        var finalSnapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsTrue(finalSnapshot!.IsCompleted);
        Assert.IsTrue(finalSnapshot.CompletedActivities.Any(a => a.ActivityId == "end"));
    }
}
