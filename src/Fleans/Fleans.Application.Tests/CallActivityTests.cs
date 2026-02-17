using Fleans.Application.Grains;
using Fleans.Application.WorkflowFactory;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using System.Dynamic;

namespace Fleans.Application.Tests;

[TestClass]
public class CallActivityTests : WorkflowTestBase
{
    [TestMethod]
    public async Task CallActivity_ShouldCompleteParent_WhenChildCompletes()
    {
        // Arrange — deploy child workflow: start → task → end
        var childStart = new StartEvent("childStart");
        var childTask = new TaskActivity("childTask");
        var childEnd = new EndEvent("childEnd");

        var childWorkflow = new WorkflowDefinition
        {
            WorkflowId = "childProcess",
            Activities = [childStart, childTask, childEnd],
            SequenceFlows =
            [
                new SequenceFlow("cs1", childStart, childTask),
                new SequenceFlow("cs2", childTask, childEnd)
            ]
        };

        var factory = Cluster.GrainFactory.GetGrain<IWorkflowInstanceFactoryGrain>(0);
        await factory.DeployWorkflow(childWorkflow, "<xml/>");

        // Arrange — parent workflow: start → callActivity → end
        var parentStart = new StartEvent("start");
        var call1 = new CallActivity("call1", "childProcess", [], []);
        var parentEnd = new EndEvent("end");

        var parentWorkflow = new WorkflowDefinition
        {
            WorkflowId = "parentProcess",
            Activities = [parentStart, call1, parentEnd],
            SequenceFlows =
            [
                new SequenceFlow("ps1", parentStart, call1),
                new SequenceFlow("ps2", call1, parentEnd)
            ]
        };

        await factory.DeployWorkflow(parentWorkflow, "<xml/>");

        var parentInstance = await factory.CreateWorkflowInstanceGrain("parentProcess");
        var parentInstanceId = parentInstance.GetPrimaryKey();

        // Act — start parent, which spawns the child
        await parentInstance.StartWorkflow();

        // Get the child workflow instance id from the parent's active call activity
        var parentSnapshot = await QueryService.GetStateSnapshot(parentInstanceId);
        Assert.IsNotNull(parentSnapshot);
        var callActivitySnapshot = parentSnapshot.ActiveActivities.First(a => a.ActivityId == "call1");
        Assert.IsNotNull(callActivitySnapshot.ChildWorkflowInstanceId, "Child workflow instance should have been spawned");
        var childInstanceId = callActivitySnapshot.ChildWorkflowInstanceId.Value;

        // Complete the child task
        var childInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(childInstanceId);
        await childInstance.CompleteActivity("childTask", new ExpandoObject());

        // Assert — parent workflow should be completed
        var finalSnapshot = await QueryService.GetStateSnapshot(parentInstanceId);
        Assert.IsNotNull(finalSnapshot);
        Assert.IsTrue(finalSnapshot.IsCompleted, "Parent workflow should be completed after child completes");
        Assert.AreEqual(0, finalSnapshot.ActiveActivities.Count, "No active activities should remain");
        CollectionAssert.Contains(finalSnapshot.CompletedActivityIds, "call1");
        CollectionAssert.Contains(finalSnapshot.CompletedActivityIds, "end");
    }

    [TestMethod]
    public async Task CallActivity_ShouldMapInputVariables_ToChild()
    {
        // Arrange — deploy child workflow
        var childStart = new StartEvent("childStart");
        var childTask = new TaskActivity("childTask");
        var childEnd = new EndEvent("childEnd");

        var childWorkflow = new WorkflowDefinition
        {
            WorkflowId = "childProcess2",
            Activities = [childStart, childTask, childEnd],
            SequenceFlows =
            [
                new SequenceFlow("cs1", childStart, childTask),
                new SequenceFlow("cs2", childTask, childEnd)
            ]
        };

        var factory = Cluster.GrainFactory.GetGrain<IWorkflowInstanceFactoryGrain>(0);
        await factory.DeployWorkflow(childWorkflow, "<xml/>");

        // Arrange — parent workflow with input mappings, propagation disabled
        var parentStart = new StartEvent("start");
        var parentTask = new TaskActivity("parentTask");
        var call1 = new CallActivity(
            "call1",
            "childProcess2",
            InputMappings: [new VariableMapping("orderId", "orderId"), new VariableMapping("amount", "paymentAmount")],
            OutputMappings: [],
            PropagateAllParentVariables: false,
            PropagateAllChildVariables: false);
        var parentEnd = new EndEvent("end");

        var parentWorkflow = new WorkflowDefinition
        {
            WorkflowId = "parentProcess2",
            Activities = [parentStart, parentTask, call1, parentEnd],
            SequenceFlows =
            [
                new SequenceFlow("ps1", parentStart, parentTask),
                new SequenceFlow("ps2", parentTask, call1),
                new SequenceFlow("ps3", call1, parentEnd)
            ]
        };

        await factory.DeployWorkflow(parentWorkflow, "<xml/>");

        var parentInstance = await factory.CreateWorkflowInstanceGrain("parentProcess2");
        var parentInstanceId = parentInstance.GetPrimaryKey();

        // Act — start parent and complete parentTask with variables
        await parentInstance.StartWorkflow();

        dynamic variables = new ExpandoObject();
        variables.orderId = 42;
        variables.amount = 100;
        variables.secret = "should-not-pass";
        await parentInstance.CompleteActivity("parentTask", (ExpandoObject)variables);

        // Get child instance id
        var parentSnapshot = await QueryService.GetStateSnapshot(parentInstanceId);
        Assert.IsNotNull(parentSnapshot);
        var callActivitySnapshot = parentSnapshot.ActiveActivities.First(a => a.ActivityId == "call1");
        Assert.IsNotNull(callActivitySnapshot.ChildWorkflowInstanceId);
        var childInstanceId = callActivitySnapshot.ChildWorkflowInstanceId.Value;

        // Assert — check child's variable state
        var childSnapshot = await QueryService.GetStateSnapshot(childInstanceId);
        Assert.IsNotNull(childSnapshot);
        Assert.IsTrue(childSnapshot.VariableStates.Count > 0, "Child should have variable states");

        var childVarDict = childSnapshot.VariableStates[0].Variables;
        Assert.IsTrue(childVarDict.ContainsKey("orderId"), "orderId should be mapped to child");
        Assert.IsTrue(childVarDict.ContainsKey("paymentAmount"), "amount should be mapped as paymentAmount");
        Assert.IsFalse(childVarDict.ContainsKey("amount"), "Original key 'amount' should not exist since propagation is disabled");
        Assert.IsFalse(childVarDict.ContainsKey("secret"), "Unmapped variable 'secret' should not be passed to child");
    }

    [TestMethod]
    public async Task CallActivity_ShouldMapOutputVariables_BackToParent()
    {
        // Arrange — deploy child workflow
        var childStart = new StartEvent("childStart");
        var childTask = new TaskActivity("childTask");
        var childEnd = new EndEvent("childEnd");

        var childWorkflow = new WorkflowDefinition
        {
            WorkflowId = "childProcess3",
            Activities = [childStart, childTask, childEnd],
            SequenceFlows =
            [
                new SequenceFlow("cs1", childStart, childTask),
                new SequenceFlow("cs2", childTask, childEnd)
            ]
        };

        var factory = Cluster.GrainFactory.GetGrain<IWorkflowInstanceFactoryGrain>(0);
        await factory.DeployWorkflow(childWorkflow, "<xml/>");

        // Arrange — parent workflow with output mappings, propagation disabled
        var parentStart = new StartEvent("start");
        var call1 = new CallActivity(
            "call1",
            "childProcess3",
            InputMappings: [],
            OutputMappings: [new VariableMapping("txId", "transactionId")],
            PropagateAllParentVariables: false,
            PropagateAllChildVariables: false);
        var parentTask = new TaskActivity("parentTask");
        var parentEnd = new EndEvent("end");

        var parentWorkflow = new WorkflowDefinition
        {
            WorkflowId = "parentProcess3",
            Activities = [parentStart, call1, parentTask, parentEnd],
            SequenceFlows =
            [
                new SequenceFlow("ps1", parentStart, call1),
                new SequenceFlow("ps2", call1, parentTask),
                new SequenceFlow("ps3", parentTask, parentEnd)
            ]
        };

        await factory.DeployWorkflow(parentWorkflow, "<xml/>");

        var parentInstance = await factory.CreateWorkflowInstanceGrain("parentProcess3");
        var parentInstanceId = parentInstance.GetPrimaryKey();

        // Act — start parent (call activity spawns child)
        await parentInstance.StartWorkflow();

        // Get child instance id
        var parentSnapshot = await QueryService.GetStateSnapshot(parentInstanceId);
        Assert.IsNotNull(parentSnapshot);
        var callActivitySnapshot = parentSnapshot.ActiveActivities.First(a => a.ActivityId == "call1");
        Assert.IsNotNull(callActivitySnapshot.ChildWorkflowInstanceId);
        var childInstanceId = callActivitySnapshot.ChildWorkflowInstanceId.Value;

        // Complete the child task with output variables
        var childInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(childInstanceId);
        dynamic childVars = new ExpandoObject();
        childVars.txId = "TX-123";
        childVars.internalState = "should-not-return";
        await childInstance.CompleteActivity("childTask", (ExpandoObject)childVars);

        // Assert — check parent's variable state after child completion
        var finalSnapshot = await QueryService.GetStateSnapshot(parentInstanceId);
        Assert.IsNotNull(finalSnapshot);

        // Parent should now have parentTask as active (after call1 completed)
        Assert.IsTrue(finalSnapshot.VariableStates.Count > 0, "Parent should have variable states");

        // Find the variable state that contains transactionId
        var parentVarDict = finalSnapshot.VariableStates
            .SelectMany(vs => vs.Variables)
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

        Assert.IsTrue(parentVarDict.ContainsKey("transactionId"), "transactionId should be mapped back to parent");
        Assert.AreEqual("TX-123", parentVarDict["transactionId"]);
        Assert.IsFalse(parentVarDict.ContainsKey("internalState"), "Unmapped variable 'internalState' should not be passed to parent");
    }

    [TestMethod]
    public async Task CallActivity_BoundaryErrorEvent_ShouldRouteToRecoveryPath()
    {
        // Arrange — deploy child workflow
        var childStart = new StartEvent("childStart");
        var childTask = new TaskActivity("childTask");
        var childEnd = new EndEvent("childEnd");

        var childWorkflow = new WorkflowDefinition
        {
            WorkflowId = "childProcess4",
            Activities = [childStart, childTask, childEnd],
            SequenceFlows =
            [
                new SequenceFlow("cs1", childStart, childTask),
                new SequenceFlow("cs2", childTask, childEnd)
            ]
        };

        var factory = Cluster.GrainFactory.GetGrain<IWorkflowInstanceFactoryGrain>(0);
        await factory.DeployWorkflow(childWorkflow, "<xml/>");

        // Arrange — parent workflow with boundary error event on call activity
        var parentStart = new StartEvent("start");
        var call1 = new CallActivity("call1", "childProcess4", [], []);
        var parentEnd = new EndEvent("end");
        var boundaryError = new BoundaryErrorEvent("err1", "call1", null);
        var recoveryTask = new TaskActivity("recoveryTask");
        var recoveryEnd = new EndEvent("recoveryEnd");

        var parentWorkflow = new WorkflowDefinition
        {
            WorkflowId = "parentProcess4",
            Activities = [parentStart, call1, parentEnd, boundaryError, recoveryTask, recoveryEnd],
            SequenceFlows =
            [
                new SequenceFlow("ps1", parentStart, call1),
                new SequenceFlow("ps2", call1, parentEnd),
                new SequenceFlow("ps3", boundaryError, recoveryTask),
                new SequenceFlow("ps4", recoveryTask, recoveryEnd)
            ]
        };

        await factory.DeployWorkflow(parentWorkflow, "<xml/>");

        var parentInstance = await factory.CreateWorkflowInstanceGrain("parentProcess4");
        var parentInstanceId = parentInstance.GetPrimaryKey();

        // Act — start parent, then fail the call activity
        await parentInstance.StartWorkflow();

        await parentInstance.FailActivity("call1", new Exception("child failed"));

        // Assert — boundary error event should have triggered, routing to recovery path
        var snapshot = await QueryService.GetStateSnapshot(parentInstanceId);
        Assert.IsNotNull(snapshot);

        // Recovery task should be in active activities
        Assert.IsTrue(
            snapshot.ActiveActivities.Any(a => a.ActivityId == "recoveryTask"),
            "Recovery task should be active after boundary error event");

        // Boundary event should be in completed activities
        Assert.IsTrue(
            snapshot.CompletedActivities.Any(a => a.ActivityId == "err1"),
            "Boundary error event should be in completed activities");
    }

    [TestMethod]
    public async Task CallActivity_WithNoMappings_ShouldIsolateVariables()
    {
        // Arrange — deploy child workflow
        var childStart = new StartEvent("childStart");
        var childTask = new TaskActivity("childTask");
        var childEnd = new EndEvent("childEnd");

        var childWorkflow = new WorkflowDefinition
        {
            WorkflowId = "childProcess5",
            Activities = [childStart, childTask, childEnd],
            SequenceFlows =
            [
                new SequenceFlow("cs1", childStart, childTask),
                new SequenceFlow("cs2", childTask, childEnd)
            ]
        };

        var factory = Cluster.GrainFactory.GetGrain<IWorkflowInstanceFactoryGrain>(0);
        await factory.DeployWorkflow(childWorkflow, "<xml/>");

        // Arrange — parent workflow with no mappings and propagation disabled
        var parentStart = new StartEvent("start");
        var parentTask = new TaskActivity("parentTask");
        var call1 = new CallActivity(
            "call1",
            "childProcess5",
            InputMappings: [],
            OutputMappings: [],
            PropagateAllParentVariables: false,
            PropagateAllChildVariables: false);
        var parentEnd = new EndEvent("end");

        var parentWorkflow = new WorkflowDefinition
        {
            WorkflowId = "parentProcess5",
            Activities = [parentStart, parentTask, call1, parentEnd],
            SequenceFlows =
            [
                new SequenceFlow("ps1", parentStart, parentTask),
                new SequenceFlow("ps2", parentTask, call1),
                new SequenceFlow("ps3", call1, parentEnd)
            ]
        };

        await factory.DeployWorkflow(parentWorkflow, "<xml/>");

        var parentInstance = await factory.CreateWorkflowInstanceGrain("parentProcess5");
        var parentInstanceId = parentInstance.GetPrimaryKey();

        // Act — start parent and complete parentTask with a variable
        await parentInstance.StartWorkflow();

        dynamic parentVars = new ExpandoObject();
        parentVars.secret = "parent-only";
        await parentInstance.CompleteActivity("parentTask", (ExpandoObject)parentVars);

        // Get child instance id
        var parentSnapshot = await QueryService.GetStateSnapshot(parentInstanceId);
        Assert.IsNotNull(parentSnapshot);
        var callActivitySnapshot = parentSnapshot.ActiveActivities.First(a => a.ActivityId == "call1");
        Assert.IsNotNull(callActivitySnapshot.ChildWorkflowInstanceId);
        var childInstanceId = callActivitySnapshot.ChildWorkflowInstanceId.Value;

        // Assert — child should NOT have parent's "secret" variable
        var childSnapshot = await QueryService.GetStateSnapshot(childInstanceId);
        Assert.IsNotNull(childSnapshot);
        var childVarDict = childSnapshot.VariableStates[0].Variables;
        Assert.IsFalse(childVarDict.ContainsKey("secret"), "Parent's 'secret' should not be passed to isolated child");

        // Complete child task with a child-only variable
        var childInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(childInstanceId);
        dynamic childVars = new ExpandoObject();
        childVars.childSecret = "child-only";
        await childInstance.CompleteActivity("childTask", (ExpandoObject)childVars);

        // Assert — parent should NOT have child's variable, and parent should be completed
        var finalSnapshot = await QueryService.GetStateSnapshot(parentInstanceId);
        Assert.IsNotNull(finalSnapshot);
        Assert.IsTrue(finalSnapshot.IsCompleted, "Parent should be completed after child completes");

        var allParentVars = finalSnapshot.VariableStates
            .SelectMany(vs => vs.Variables)
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        Assert.IsFalse(allParentVars.ContainsKey("childSecret"), "Child's 'childSecret' should not be passed to isolated parent");
    }

    [TestMethod]
    public async Task CallActivity_ShouldPropagateAllVariables_WhenDefaultFlags()
    {
        // Arrange — deploy child workflow
        var childStart = new StartEvent("childStart");
        var childTask = new TaskActivity("childTask");
        var childEnd = new EndEvent("childEnd");

        var childWorkflow = new WorkflowDefinition
        {
            WorkflowId = "childProcess6",
            Activities = [childStart, childTask, childEnd],
            SequenceFlows =
            [
                new SequenceFlow("cs1", childStart, childTask),
                new SequenceFlow("cs2", childTask, childEnd)
            ]
        };

        var factory = Cluster.GrainFactory.GetGrain<IWorkflowInstanceFactoryGrain>(0);
        await factory.DeployWorkflow(childWorkflow, "<xml/>");

        // Arrange — parent workflow with default propagation flags (true, true)
        var parentStart = new StartEvent("start");
        var parentTask = new TaskActivity("parentTask");
        var call1 = new CallActivity("call1", "childProcess6", [], []);
        var parentEnd = new EndEvent("end");

        var parentWorkflow = new WorkflowDefinition
        {
            WorkflowId = "parentProcess6",
            Activities = [parentStart, parentTask, call1, parentEnd],
            SequenceFlows =
            [
                new SequenceFlow("ps1", parentStart, parentTask),
                new SequenceFlow("ps2", parentTask, call1),
                new SequenceFlow("ps3", call1, parentEnd)
            ]
        };

        await factory.DeployWorkflow(parentWorkflow, "<xml/>");

        var parentInstance = await factory.CreateWorkflowInstanceGrain("parentProcess6");
        var parentInstanceId = parentInstance.GetPrimaryKey();

        // Act — start parent and complete parentTask with a variable
        await parentInstance.StartWorkflow();

        dynamic parentVars = new ExpandoObject();
        parentVars.parentVar = "hello";
        await parentInstance.CompleteActivity("parentTask", (ExpandoObject)parentVars);

        // Get child instance id
        var parentSnapshot = await QueryService.GetStateSnapshot(parentInstanceId);
        Assert.IsNotNull(parentSnapshot);
        var callActivitySnapshot = parentSnapshot.ActiveActivities.First(a => a.ActivityId == "call1");
        Assert.IsNotNull(callActivitySnapshot.ChildWorkflowInstanceId);
        var childInstanceId = callActivitySnapshot.ChildWorkflowInstanceId.Value;

        // Assert — child should have parent's "parentVar" variable (propagation enabled)
        var childSnapshot = await QueryService.GetStateSnapshot(childInstanceId);
        Assert.IsNotNull(childSnapshot);
        var childVarDict = childSnapshot.VariableStates[0].Variables;
        Assert.IsTrue(childVarDict.ContainsKey("parentVar"), "Parent's 'parentVar' should propagate to child with default flags");

        // Complete child task with a child variable
        var childInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(childInstanceId);
        dynamic childVars = new ExpandoObject();
        childVars.childVar = "world";
        await childInstance.CompleteActivity("childTask", (ExpandoObject)childVars);

        // Assert — parent should have both parentVar and childVar, and be completed
        var finalSnapshot = await QueryService.GetStateSnapshot(parentInstanceId);
        Assert.IsNotNull(finalSnapshot);
        Assert.IsTrue(finalSnapshot.IsCompleted, "Parent should be completed after child completes");

        var allParentVars = finalSnapshot.VariableStates
            .SelectMany(vs => vs.Variables)
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        Assert.IsTrue(allParentVars.ContainsKey("parentVar"), "Parent should still have 'parentVar'");
        Assert.IsTrue(allParentVars.ContainsKey("childVar"), "Child's 'childVar' should propagate back to parent with default flags");
    }
}
