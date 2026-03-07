using Fleans.Application.Grains;
using Fleans.Application.WorkflowFactory;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using System.Dynamic;

namespace Fleans.Application.Tests;

[TestClass]
public class MessageStartEventTests : WorkflowTestBase
{
    [TestMethod]
    public async Task FireMessageStartEvent_ShouldCreateAndStartWorkflowInstance()
    {
        // Arrange — deploy a workflow with MessageStartEvent
        var messageStart = new MessageStartEvent("msgStart1", "msg1");
        var task = new ScriptTask("task1", "", "csharp");
        var end = new EndEvent("end");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "message-start-workflow",
            Activities = [messageStart, task, end],
            SequenceFlows =
            [
                new SequenceFlow("f1", messageStart, task),
                new SequenceFlow("f2", task, end)
            ],
            Messages = [new MessageDefinition("msg1", "orderReceived", null)]
        };

        var factory = Cluster.GrainFactory.GetGrain<IWorkflowInstanceFactoryGrain>(0);
        await factory.DeployWorkflow(workflow, "<placeholder/>");

        // Act — fire message start event
        var listener = Cluster.GrainFactory.GetGrain<IMessageStartEventListenerGrain>("orderReceived");
        var instanceIds = await listener.FireMessageStartEvent(new ExpandoObject());

        // Assert
        Assert.HasCount(1, instanceIds);
        var snapshot = await QueryService.GetStateSnapshot(instanceIds[0]);
        Assert.IsNotNull(snapshot);
        Assert.IsTrue(snapshot.IsStarted);
    }

    [TestMethod]
    public async Task FireMessageStartEvent_ShouldPropagateVariables()
    {
        // Arrange
        var messageStart = new MessageStartEvent("msgStart1", "msg1");
        var task = new TaskActivity("task1");
        var end = new EndEvent("end");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "message-vars-workflow",
            Activities = [messageStart, task, end],
            SequenceFlows =
            [
                new SequenceFlow("f1", messageStart, task),
                new SequenceFlow("f2", task, end)
            ],
            Messages = [new MessageDefinition("msg1", "orderWithVars", null)]
        };

        var factory = Cluster.GrainFactory.GetGrain<IWorkflowInstanceFactoryGrain>(0);
        await factory.DeployWorkflow(workflow, "<placeholder/>");

        // Act
        dynamic variables = new ExpandoObject();
        variables.orderId = "ORD-123";
        variables.amount = 42;

        var listener = Cluster.GrainFactory.GetGrain<IMessageStartEventListenerGrain>("orderWithVars");
        var instanceIds = await listener.FireMessageStartEvent((ExpandoObject)variables);

        // Assert
        Assert.HasCount(1, instanceIds);
        var snapshot = await QueryService.GetStateSnapshot(instanceIds[0]);
        Assert.IsNotNull(snapshot);

        // The workflow should have started and the message start event should be completed
        Assert.IsTrue(snapshot.IsStarted);
        Assert.IsTrue(snapshot.ActiveActivities.Any(a => a.ActivityId == "task1"),
            "Task1 should be active after message start event completes");
    }

    [TestMethod]
    public async Task FireMessageStartEvent_TwoWorkflows_ShouldCreateBothInstances()
    {
        // Arrange — deploy two workflows listening on the same message
        var factory = Cluster.GrainFactory.GetGrain<IWorkflowInstanceFactoryGrain>(0);

        var workflow1 = new WorkflowDefinition
        {
            WorkflowId = "msg-start-wf1",
            Activities = [new MessageStartEvent("ms1", "msg1"), new EndEvent("end1")],
            SequenceFlows = [new SequenceFlow("f1",
                new MessageStartEvent("ms1", "msg1"), new EndEvent("end1"))],
            Messages = [new MessageDefinition("msg1", "sharedMessage", null)]
        };
        await factory.DeployWorkflow(workflow1, "<placeholder/>");

        var workflow2 = new WorkflowDefinition
        {
            WorkflowId = "msg-start-wf2",
            Activities = [new MessageStartEvent("ms2", "msg2"), new EndEvent("end2")],
            SequenceFlows = [new SequenceFlow("f2",
                new MessageStartEvent("ms2", "msg2"), new EndEvent("end2"))],
            Messages = [new MessageDefinition("msg2", "sharedMessage", null)]
        };
        await factory.DeployWorkflow(workflow2, "<placeholder/>");

        // Act
        var listener = Cluster.GrainFactory.GetGrain<IMessageStartEventListenerGrain>("sharedMessage");
        var instanceIds = await listener.FireMessageStartEvent(new ExpandoObject());

        // Assert
        Assert.HasCount(2, instanceIds);
        Assert.AreNotEqual(instanceIds[0], instanceIds[1]);
    }

    [TestMethod]
    public async Task FireMessageStartEvent_NoRegisteredProcesses_ShouldReturnEmpty()
    {
        // Act — fire on a message name with no registrations
        var listener = Cluster.GrainFactory.GetGrain<IMessageStartEventListenerGrain>("nonExistentMessage");
        var instanceIds = await listener.FireMessageStartEvent(new ExpandoObject());

        // Assert
        Assert.HasCount(0, instanceIds);
    }

    [TestMethod]
    public async Task DeployWorkflow_ShouldAutoRegister_MessageStartEventListener()
    {
        // Arrange & Act — deploy registers the listener automatically
        var messageStart = new MessageStartEvent("msgStart1", "msg1");
        var end = new EndEvent("end");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "auto-register-workflow",
            Activities = [messageStart, end],
            SequenceFlows = [new SequenceFlow("f1", messageStart, end)],
            Messages = [new MessageDefinition("msg1", "autoRegMsg", null)]
        };

        var factory = Cluster.GrainFactory.GetGrain<IWorkflowInstanceFactoryGrain>(0);
        await factory.DeployWorkflow(workflow, "<placeholder/>");

        // Assert — fire should create an instance
        var listener = Cluster.GrainFactory.GetGrain<IMessageStartEventListenerGrain>("autoRegMsg");
        var instanceIds = await listener.FireMessageStartEvent(new ExpandoObject());
        Assert.HasCount(1, instanceIds);
    }

    [TestMethod]
    public async Task Redeployment_WithoutMessageStartEvent_ShouldUnregisterListener()
    {
        // Arrange — deploy v1 with message start event
        var factory = Cluster.GrainFactory.GetGrain<IWorkflowInstanceFactoryGrain>(0);

        var messageStart = new MessageStartEvent("msgStart1", "msg1");
        var end = new EndEvent("end");
        var v1 = new WorkflowDefinition
        {
            WorkflowId = "redeploy-workflow",
            Activities = [messageStart, end],
            SequenceFlows = [new SequenceFlow("f1", messageStart, end)],
            Messages = [new MessageDefinition("msg1", "redeployMsg", null)]
        };
        await factory.DeployWorkflow(v1, "<placeholder/>");

        // Verify v1 registration works
        var listener = Cluster.GrainFactory.GetGrain<IMessageStartEventListenerGrain>("redeployMsg");
        var v1Ids = await listener.FireMessageStartEvent(new ExpandoObject());
        Assert.HasCount(1, v1Ids);

        // Act — deploy v2 without message start event (plain start event instead)
        var plainStart = new StartEvent("start");
        var task = new TaskActivity("task1");
        var end2 = new EndEvent("end2");
        var v2 = new WorkflowDefinition
        {
            WorkflowId = "redeploy-workflow",
            Activities = [plainStart, task, end2],
            SequenceFlows =
            [
                new SequenceFlow("f1", plainStart, task),
                new SequenceFlow("f2", task, end2)
            ]
        };
        await factory.DeployWorkflow(v2, "<placeholder/>");

        // Assert — listener should no longer create instances for this process
        var v2Ids = await listener.FireMessageStartEvent(new ExpandoObject());
        Assert.HasCount(0, v2Ids);
    }

    [TestMethod]
    public async Task CorrelationDelivery_WhenSuccessful_ShouldNotTriggerStartEvent()
    {
        // Arrange — deploy a workflow with message intermediate catch event
        var factory = Cluster.GrainFactory.GetGrain<IWorkflowInstanceFactoryGrain>(0);

        dynamic initVars = new ExpandoObject();
        initVars.orderId = "ORD-999";

        var start = new StartEvent("start");
        var msgCatch = new MessageIntermediateCatchEvent("waitMsg", "msg1");
        var end = new EndEvent("end");
        var correlationWorkflow = new WorkflowDefinition
        {
            WorkflowId = "correlation-priority-wf",
            Activities = [start, msgCatch, end],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, msgCatch),
                new SequenceFlow("f2", msgCatch, end)
            ],
            Messages = [new MessageDefinition("msg1", "priorityMsg", "orderId")]
        };
        await factory.DeployWorkflow(correlationWorkflow, "<placeholder/>");

        // Start an instance with the correlation variable set
        var instance = await factory.CreateWorkflowInstanceGrain("correlation-priority-wf");
        await instance.SetInitialVariables((ExpandoObject)initVars);
        await instance.StartWorkflow();

        // Wait for subscription registration
        await Task.Delay(500);

        // Act — deliver via correlation
        var correlationKey = MessageCorrelationKey.Build("priorityMsg", "ORD-999");
        var correlationGrain = Cluster.GrainFactory.GetGrain<IMessageCorrelationGrain>(correlationKey);
        var delivered = await correlationGrain.DeliverMessage(new ExpandoObject());

        // Assert — correlation delivery should succeed
        Assert.IsTrue(delivered, "Correlation delivery should succeed for a subscribed instance");

        // Verify the instance completed (message was delivered to catch event)
        var instanceId = await instance.GetWorkflowInstanceId();
        var snapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsNotNull(snapshot);
        Assert.IsTrue(snapshot.IsCompleted, "Instance should complete after message delivery");
    }
}
