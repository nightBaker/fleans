using Fleans.Application.Grains;
using Fleans.Application.QueryModels;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Errors;
using Fleans.Domain.Sequences;
using Orleans.Runtime;
using System.Dynamic;

namespace Fleans.Application.Tests;

[TestClass]
public class ScriptTaskTests : WorkflowTestBase
{
    [TestMethod]
    public async Task GetNextActivities_ShouldReturnNextActivity()
    {
        // Arrange
        var workflow = CreateSimpleWorkflow();
        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        dynamic variables = new ExpandoObject();
        variables.x = 10;

        // Act — complete script task so it transitions to end event
        await workflowInstance.CompleteActivity("script1", (ExpandoObject)variables);

        // Assert — end event should have been reached (workflow completes)
        var instanceId = workflowInstance.GetPrimaryKey();
        var snapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsNotNull(snapshot);
        Assert.IsTrue(snapshot.IsCompleted);
        CollectionAssert.Contains(snapshot.CompletedActivityIds, "end");
    }

    [TestMethod]
    public async Task ExecuteAsync_ShouldMarkActivityAsExecuting()
    {
        // Arrange
        var workflow = CreateSimpleWorkflow();
        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);

        // Act
        await workflowInstance.StartWorkflow();

        // Assert — after starting, the script task should be active/executing
        var instanceId = workflowInstance.GetPrimaryKey();
        var snapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsNotNull(snapshot);
        Assert.HasCount(1, snapshot.ActiveActivities);
        Assert.AreEqual("script1", snapshot.ActiveActivities[0].ActivityId);
        Assert.AreEqual("ScriptTask", snapshot.ActiveActivities[0].ActivityType);
    }

    [TestMethod]
    public async Task CompleteScriptTask_ShouldTransitionToEndEvent_AndCompleteWorkflow()
    {
        // Arrange
        var workflow = CreateSimpleWorkflow();
        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        dynamic variables = new ExpandoObject();
        variables.x = 10;

        // Act
        await workflowInstance.CompleteActivity("script1", (ExpandoObject)variables);

        // Assert
        var instanceId = workflowInstance.GetPrimaryKey();
        var snapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsNotNull(snapshot);
        Assert.IsTrue(snapshot.IsCompleted);

        var completedActivityIds = snapshot.CompletedActivityIds;
        CollectionAssert.Contains(completedActivityIds, "start");
        CollectionAssert.Contains(completedActivityIds, "script1");
        CollectionAssert.Contains(completedActivityIds, "end");
    }

    [TestMethod]
    public async Task CompleteScriptTask_ShouldMergeVariablesIntoState()
    {
        // Arrange
        var workflow = CreateSimpleWorkflow();
        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        dynamic variables = new ExpandoObject();
        variables.result = 42;
        variables.message = "done";

        // Act
        await workflowInstance.CompleteActivity("script1", (ExpandoObject)variables);

        // Assert
        var instanceId = workflowInstance.GetPrimaryKey();
        var snapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsNotNull(snapshot);
        var variableStates = snapshot.VariableStates;
        Assert.IsTrue(variableStates.Count > 0);

        var vars = variableStates.First().Variables;
        Assert.AreEqual(2, vars.Count);
        Assert.AreEqual("42", vars["result"]);
        Assert.AreEqual("done", vars["message"]);
    }

    [TestMethod]
    public async Task CompleteScriptTask_ShouldMarkActivityInstanceAsCompleted()
    {
        // Arrange
        var workflow = CreateSimpleWorkflow();
        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        // Act
        await workflowInstance.CompleteActivity("script1", new ExpandoObject());

        // Assert — script task should appear in completed activities
        var instanceId = workflowInstance.GetPrimaryKey();
        var snapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsNotNull(snapshot);
        var completedScriptTask = snapshot.CompletedActivities.FirstOrDefault(a => a.ActivityId == "script1");

        Assert.IsNotNull(completedScriptTask, "ScriptTask should be in completed activities");
        Assert.IsTrue(completedScriptTask.IsCompleted);
        Assert.IsNull(completedScriptTask.ErrorState);
    }

    [TestMethod]
    public async Task CompleteScriptTask_ShouldHaveNoActiveActivities_AfterWorkflowCompletes()
    {
        // Arrange
        var workflow = CreateSimpleWorkflow();
        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        // Act
        await workflowInstance.CompleteActivity("script1", new ExpandoObject());

        // Assert
        var instanceId = workflowInstance.GetPrimaryKey();
        var snapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsNotNull(snapshot);
        Assert.HasCount(0, snapshot.ActiveActivities);
    }

    [TestMethod]
    public async Task CompleteScriptTask_WithMultipleScriptTasks_ShouldExecuteInSequence()
    {
        // Arrange
        var script1 = new ScriptTask("script1", "_context.step = 1");
        var script2 = new ScriptTask("script2", "_context.step = 2");
        var start = new StartEvent("start");
        var end = new EndEvent("end");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "test-workflow",
            Activities = new List<Activity> { start, script1, script2, end },
            SequenceFlows = new List<SequenceFlow>
            {
                new SequenceFlow("seq1", start, script1),
                new SequenceFlow("seq2", script1, script2),
                new SequenceFlow("seq3", script2, end)
            }
        };

        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        dynamic vars1 = new ExpandoObject();
        vars1.step = 1;

        // Act — complete first script task
        await workflowInstance.CompleteActivity("script1", (ExpandoObject)vars1);

        // Assert — second script task should now be active
        var instanceId = workflowInstance.GetPrimaryKey();
        var snapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsNotNull(snapshot);
        Assert.IsFalse(snapshot.IsCompleted);
        Assert.HasCount(1, snapshot.ActiveActivities);
        Assert.AreEqual("ScriptTask", snapshot.ActiveActivities[0].ActivityType);
        Assert.AreEqual("script2", snapshot.ActiveActivities[0].ActivityId);

        // Act — complete second script task
        dynamic vars2 = new ExpandoObject();
        vars2.step = 2;
        await workflowInstance.CompleteActivity("script2", (ExpandoObject)vars2);

        // Assert — workflow should be completed
        snapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsNotNull(snapshot);
        Assert.IsTrue(snapshot.IsCompleted);
    }

    [TestMethod]
    public async Task FailScriptTask_ShouldSetErrorState_WithCode500()
    {
        // Arrange
        var workflow = CreateSimpleWorkflow();
        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        // Act
        await workflowInstance.FailActivity("script1", new Exception("Script execution failed"));

        // Assert
        var instanceId = workflowInstance.GetPrimaryKey();
        var snapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsNotNull(snapshot);
        var failedSnapshot = snapshot.CompletedActivities.First(a => a.ActivityId == "script1");

        Assert.IsNotNull(failedSnapshot.ErrorState);
        Assert.AreEqual(500, failedSnapshot.ErrorState!.Code);
        Assert.AreEqual("Script execution failed", failedSnapshot.ErrorState.Message);
    }

    [TestMethod]
    public async Task FailScriptTask_WithActivityException_ShouldSetCustomErrorCode()
    {
        // Arrange
        var workflow = CreateSimpleWorkflow();
        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        // Act
        await workflowInstance.FailActivity("script1", new BadRequestActivityException("Invalid script input"));

        // Assert
        var instanceId = workflowInstance.GetPrimaryKey();
        var snapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsNotNull(snapshot);
        var failedSnapshot = snapshot.CompletedActivities.First(a => a.ActivityId == "script1");

        Assert.IsNotNull(failedSnapshot.ErrorState);
        Assert.AreEqual(400, failedSnapshot.ErrorState!.Code);
        Assert.AreEqual("Invalid script input", failedSnapshot.ErrorState.Message);
    }

    [TestMethod]
    public async Task FailScriptTask_ShouldMarkAsCompleted_AndTransitionToNextActivity()
    {
        // Arrange
        var workflow = CreateSimpleWorkflow();
        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        // Act
        await workflowInstance.FailActivity("script1", new Exception("Script error"));

        // Assert — workflow should complete (failed activity transitions to end event)
        var instanceId = workflowInstance.GetPrimaryKey();
        var snapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsNotNull(snapshot);
        Assert.IsTrue(snapshot.IsCompleted);
        Assert.HasCount(0, snapshot.ActiveActivities);

        var completedIds = snapshot.CompletedActivityIds;
        CollectionAssert.Contains(completedIds, "script1");
        CollectionAssert.Contains(completedIds, "end");
    }

    [TestMethod]
    public async Task FailScriptTask_ShouldNotMergeVariables()
    {
        // Arrange
        var workflow = CreateSimpleWorkflow();
        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        // Act
        await workflowInstance.FailActivity("script1", new Exception("Script error"));

        // Assert — variables should not have been merged (FailActivity doesn't take variables)
        var instanceId = workflowInstance.GetPrimaryKey();
        var snapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsNotNull(snapshot);
        foreach (var vs in snapshot.VariableStates)
        {
            Assert.AreEqual(0, vs.Variables.Count, "No variables should be merged on failure");
        }
    }

    private static IWorkflowDefinition CreateSimpleWorkflow()
    {
        var start = new StartEvent("start");
        var script = new ScriptTask("script1", "_context.x = 10");
        var end = new EndEvent("end");

        return new WorkflowDefinition
        {
            WorkflowId = "test-workflow",
            Activities = new List<Activity> { start, script, end },
            SequenceFlows = new List<SequenceFlow>
            {
                new SequenceFlow("seq1", start, script),
                new SequenceFlow("seq2", script, end)
            }
        };
    }
}
