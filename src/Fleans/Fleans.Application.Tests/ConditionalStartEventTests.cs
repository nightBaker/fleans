using Fleans.Application.Grains;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Errors;
using Fleans.Domain.Sequences;
using System.Dynamic;

namespace Fleans.Application.Tests;

[TestClass]
public class ConditionalStartEventTests : WorkflowTestBase
{
    private static WorkflowDefinition CreateConditionalStartWorkflow(string processId, string conditionExpression)
    {
        var start = new ConditionalStartEvent("condStart", conditionExpression);
        var task = new ScriptTask("task1", "noop", "csharp");
        var end = new EndEvent("end");

        return new WorkflowDefinition
        {
            WorkflowId = processId,
            Activities = [start, task, end],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, task),
                new SequenceFlow("f2", task, end)
            ]
        };
    }

    private static WorkflowDefinition CreateConditionalStartWorkflowWithTask(string processId, string conditionExpression)
    {
        var start = new ConditionalStartEvent("condStart", conditionExpression);
        var task = new TaskActivity("task1");
        var end = new EndEvent("end");

        return new WorkflowDefinition
        {
            WorkflowId = processId,
            Activities = [start, task, end],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, task),
                new SequenceFlow("f2", task, end)
            ]
        };
    }

    [TestMethod]
    public async Task EvaluateAndStart_MatchingCondition_ShouldCreateInstance()
    {
        // Arrange — deploy a workflow with a conditional start event
        var workflow = CreateConditionalStartWorkflow("cond-start-match", "true");
        var processGrain = Cluster.GrainFactory.GetGrain<IProcessDefinitionGrain>("cond-start-match");
        await processGrain.DeployVersion(workflow, "<placeholder/>");

        // Act — evaluate via listener grain
        var listenerKey = "cond-start-match:condStart";
        var listener = Cluster.GrainFactory.GetGrain<IConditionalStartEventListenerGrain>(listenerKey);
        var variables = new ExpandoObject();
        var instanceId = await listener.EvaluateAndStart(variables);

        // Assert
        Assert.IsNotNull(instanceId, "Should create an instance when condition is true");
        var snapshot = await QueryService.GetStateSnapshot(instanceId.Value);
        Assert.IsNotNull(snapshot);
        Assert.IsTrue(snapshot.IsStarted, "Instance should be started");
    }

    [TestMethod]
    public async Task EvaluateAndStart_NonMatchingCondition_ShouldReturnNull()
    {
        // Arrange
        var workflow = CreateConditionalStartWorkflow("cond-start-nomatch", "false");
        var processGrain = Cluster.GrainFactory.GetGrain<IProcessDefinitionGrain>("cond-start-nomatch");
        await processGrain.DeployVersion(workflow, "<placeholder/>");

        // Act
        var listenerKey = "cond-start-nomatch:condStart";
        var listener = Cluster.GrainFactory.GetGrain<IConditionalStartEventListenerGrain>(listenerKey);
        var variables = new ExpandoObject();
        var instanceId = await listener.EvaluateAndStart(variables);

        // Assert
        Assert.IsNull(instanceId, "Should NOT create an instance when condition is false");
    }

    [TestMethod]
    public async Task EvaluateAndStart_UnregisteredListener_ShouldReturnNull()
    {
        // Act — call listener without deploying any workflow
        var listener = Cluster.GrainFactory.GetGrain<IConditionalStartEventListenerGrain>("nonexistent:condStart");
        var variables = new ExpandoObject();
        var instanceId = await listener.EvaluateAndStart(variables);

        // Assert
        Assert.IsNull(instanceId, "Unregistered listener should return null");
    }

    [TestMethod]
    public async Task DeployWorkflow_ShouldAutoRegisterConditionalStartEventListener()
    {
        // Arrange & Act — deploy a process with conditional start event
        var workflow = CreateConditionalStartWorkflow("cond-start-autoreg", "true");
        var processGrain = Cluster.GrainFactory.GetGrain<IProcessDefinitionGrain>("cond-start-autoreg");
        await processGrain.DeployVersion(workflow, "<placeholder/>");

        // Assert — listener should be registered and able to start instances
        var listenerKey = "cond-start-autoreg:condStart";
        var listener = Cluster.GrainFactory.GetGrain<IConditionalStartEventListenerGrain>(listenerKey);
        var instanceId = await listener.EvaluateAndStart(new ExpandoObject());
        Assert.IsNotNull(instanceId, "Auto-registered listener should create instance");
    }

    [TestMethod]
    public async Task FailActivity_ShouldSetErrorState_WithGenericException()
    {
        // Arrange
        var workflow = CreateConditionalStartWorkflowWithTask("cond-start-fail500", "true");
        var processGrain = Cluster.GrainFactory.GetGrain<IProcessDefinitionGrain>("cond-start-fail500");
        await processGrain.DeployVersion(workflow, "<placeholder/>");

        var listenerKey = "cond-start-fail500:condStart";
        var listener = Cluster.GrainFactory.GetGrain<IConditionalStartEventListenerGrain>(listenerKey);
        var instanceId = await listener.EvaluateAndStart(new ExpandoObject());
        Assert.IsNotNull(instanceId);
        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(instanceId.Value);

        // Act
        await workflowInstance.FailActivity("task1", new Exception("Generic failure"));

        // Assert
        var snapshot = await QueryService.GetStateSnapshot(instanceId.Value);
        Assert.IsNotNull(snapshot);
        var failedTask = snapshot.CompletedActivities.First(a => a.ActivityId == "task1");
        Assert.IsTrue(failedTask.IsCompleted);
        Assert.IsNotNull(failedTask.ErrorState);
        Assert.AreEqual(500, failedTask.ErrorState.Code);
        Assert.AreEqual("Generic failure", failedTask.ErrorState.Message);
    }

    [TestMethod]
    public async Task FailActivity_ShouldSetErrorState_WithBadRequestActivityException()
    {
        // Arrange
        var workflow = CreateConditionalStartWorkflowWithTask("cond-start-fail400", "true");
        var processGrain = Cluster.GrainFactory.GetGrain<IProcessDefinitionGrain>("cond-start-fail400");
        await processGrain.DeployVersion(workflow, "<placeholder/>");

        var listenerKey = "cond-start-fail400:condStart";
        var listener = Cluster.GrainFactory.GetGrain<IConditionalStartEventListenerGrain>(listenerKey);
        var instanceId = await listener.EvaluateAndStart(new ExpandoObject());
        Assert.IsNotNull(instanceId);
        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(instanceId.Value);

        // Act
        await workflowInstance.FailActivity("task1", new BadRequestActivityException("Bad input"));

        // Assert
        var snapshot = await QueryService.GetStateSnapshot(instanceId.Value);
        Assert.IsNotNull(snapshot);
        var failedTask = snapshot.CompletedActivities.First(a => a.ActivityId == "task1");
        Assert.IsNotNull(failedTask.ErrorState);
        Assert.AreEqual(400, failedTask.ErrorState.Code);
        Assert.AreEqual("Bad input", failedTask.ErrorState.Message);
    }
}
