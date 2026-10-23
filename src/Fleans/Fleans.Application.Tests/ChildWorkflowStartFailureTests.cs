using Fleans.Application.Grains;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;

namespace Fleans.Application.Tests;

[TestClass]
public class ChildWorkflowStartFailureTests : WorkflowTestBase
{
    [TestMethod]
    public async Task ChildWorkflowStart_DefinitionLoadFails_FailsParentCallActivity()
    {
        // Arrange — parent workflow with a CallActivity pointing at a process key
        // that was never deployed. IProcessDefinitionGrain.GetLatestDefinition()
        // throws KeyNotFoundException. The safety-net catch in
        // WorkflowLifecycleEffectHandler.PerformStartChildWorkflow must route
        // this through IEffectContext.ProcessFailureEffects so the parent
        // activity is failed rather than the grain turn exploding.
        var parentStart = new StartEvent("start");
        var call1 = new CallActivity("call1", "nonExistentChildProcess", [], []);
        var parentEnd = new EndEvent("end");

        var parentWorkflow = new WorkflowDefinition
        {
            WorkflowId = "parentProcessFailingChildStart",
            Activities = [parentStart, call1, parentEnd],
            SequenceFlows =
            [
                new SequenceFlow("ps1", parentStart, call1),
                new SequenceFlow("ps2", call1, parentEnd)
            ]
        };

        var parentProcessGrain = Cluster.GrainFactory.GetGrain<IProcessDefinitionGrain>(
            "parentProcessFailingChildStart");
        await parentProcessGrain.DeployVersion(parentWorkflow, "<xml/>");

        var parentInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        var parentInstanceId = parentInstance.GetPrimaryKey();

        // Act — start parent. Without the safety net this would propagate the
        // KeyNotFoundException to the caller and leave the parent wedged.
        await parentInstance.StartWorkflow(await parentProcessGrain.GetLatestDefinition());

        // Assert — the call activity must end up failed (in CompletedActivities
        // with ErrorState set), and the grain must remain queryable.
        var snapshot = await QueryService.GetStateSnapshot(parentInstanceId);
        Assert.IsNotNull(snapshot, "Parent state snapshot should exist after the safety net catches the child-start failure");

        var failedCall = snapshot.CompletedActivities
            .FirstOrDefault(a => a.ActivityId == "call1");
        Assert.IsNotNull(failedCall, "call1 should be in CompletedActivities after the child-start failure");
        Assert.IsNotNull(failedCall.ErrorState, "call1 must carry an ErrorState reflecting the child-start failure");
        Assert.IsFalse(string.IsNullOrEmpty(failedCall.ErrorState.Message),
            "ErrorState.Message should describe the child-start failure");
    }

    [TestMethod]
    public async Task ChildWorkflowStart_CalledProcessDisabled_FailsParentCallActivity()
    {
        // Arrange — a disabled process starts no new instance, a call activity's child included
        // (#863): the call activity fails like any other child-start failure.
        var childStart = new StartEvent("childStart");
        var childEnd = new EndEvent("childEnd");
        var childProcessGrain = Cluster.GrainFactory.GetGrain<IProcessDefinitionGrain>("disabledChildProcess");
        await childProcessGrain.DeployVersion(new WorkflowDefinition
        {
            WorkflowId = "disabledChildProcess",
            Activities = [childStart, childEnd],
            SequenceFlows = [new SequenceFlow("cs1", childStart, childEnd)]
        }, "<xml/>");
        await childProcessGrain.Disable();

        var parentStart = new StartEvent("start");
        var call1 = new CallActivity("call1", "disabledChildProcess", [], []);
        var parentEnd = new EndEvent("end");
        var parentProcessGrain = Cluster.GrainFactory.GetGrain<IProcessDefinitionGrain>("parentOfDisabledChild");
        await parentProcessGrain.DeployVersion(new WorkflowDefinition
        {
            WorkflowId = "parentOfDisabledChild",
            Activities = [parentStart, call1, parentEnd],
            SequenceFlows = [new SequenceFlow("ps1", parentStart, call1), new SequenceFlow("ps2", call1, parentEnd)]
        }, "<xml/>");
        var parentInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());

        // Act
        await parentInstance.StartWorkflow(await parentProcessGrain.GetLatestDefinition());

        // Assert
        var snapshot = await QueryService.GetStateSnapshot(parentInstance.GetPrimaryKey());
        Assert.IsNotNull(snapshot);
        var failedCall = snapshot.CompletedActivities.FirstOrDefault(a => a.ActivityId == "call1");
        Assert.IsNotNull(failedCall, "call1 should be completed with an error");
        Assert.IsNotNull(failedCall.ErrorState);
        StringAssert.Contains(failedCall.ErrorState.Message, "disabled");
        Assert.IsNotNull(failedCall.ChildWorkflowInstanceId);
        Assert.IsEmpty(FaultInjectingEventStore.AppendsOf(failedCall.ChildWorkflowInstanceId.Value),
            "the child instance was never started");
    }
}
