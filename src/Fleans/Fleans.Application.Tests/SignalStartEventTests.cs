using Fleans.Application.Grains;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Errors;
using Fleans.Domain.Sequences;

namespace Fleans.Application.Tests;

[TestClass]
public class SignalStartEventTests : WorkflowTestBase
{
    private static WorkflowDefinition CreateSignalStartWorkflow(string processId, string signalName)
    {
        var signalDef = new SignalDefinition("sig1", signalName);
        var start = new SignalStartEvent("sigStart", "sig1");
        var task = new ScriptTask("task1", "", "csharp");
        var end = new EndEvent("end");

        var flow1 = new SequenceFlow("flow1", start, task);
        var flow2 = new SequenceFlow("flow2", task, end);

        return new WorkflowDefinition
        {
            WorkflowId = processId,
            Activities = [start, task, end],
            SequenceFlows = [flow1, flow2],
            Signals = [signalDef]
        };
    }

    private static WorkflowDefinition CreateSignalStartWorkflowWithTask(string processId, string signalName)
    {
        var signalDef = new SignalDefinition("sig1", signalName);
        var start = new SignalStartEvent("sigStart", "sig1");
        var task = new TaskActivity("task1");
        var end = new EndEvent("end");

        var flow1 = new SequenceFlow("flow1", start, task);
        var flow2 = new SequenceFlow("flow2", task, end);

        return new WorkflowDefinition
        {
            WorkflowId = processId,
            Activities = [start, task, end],
            SequenceFlows = [flow1, flow2],
            Signals = [signalDef]
        };
    }

    [TestMethod]
    public async Task FireSignalStartEvent_ShouldCreateAndStartWorkflowInstance()
    {
        // Arrange
        var workflow = CreateSignalStartWorkflow("signal-start-workflow", "orderSignal");

        var processGrain = Cluster.GrainFactory.GetGrain<IProcessDefinitionGrain>("signal-start-workflow");
        await processGrain.DeployVersion(workflow, "<placeholder/>");

        // Act
        var listener = Cluster.GrainFactory.GetGrain<ISignalStartEventListenerGrain>("orderSignal");
        var instanceIds = await listener.FireSignalStartEvent();

        // Assert
        Assert.HasCount(1, instanceIds);
        var snapshot = await QueryService.GetStateSnapshot(instanceIds[0]);
        Assert.IsNotNull(snapshot);
        Assert.IsTrue(snapshot.IsStarted);
    }

    [TestMethod]
    public async Task FireSignalStartEvent_TwoWorkflows_ShouldCreateBothInstances()
    {
        // Arrange
        var start1 = new SignalStartEvent("ss1", "sig1");
        var end1 = new EndEvent("end1");
        var workflow1 = new WorkflowDefinition
        {
            WorkflowId = "sig-start-wf1",
            Activities = [start1, end1],
            SequenceFlows = [new SequenceFlow("f1", start1, end1)],
            Signals = [new SignalDefinition("sig1", "sharedSignal")]
        };
        await Cluster.GrainFactory.GetGrain<IProcessDefinitionGrain>("sig-start-wf1").DeployVersion(workflow1, "<placeholder/>");

        var start2 = new SignalStartEvent("ss2", "sig2");
        var end2 = new EndEvent("end2");
        var workflow2 = new WorkflowDefinition
        {
            WorkflowId = "sig-start-wf2",
            Activities = [start2, end2],
            SequenceFlows = [new SequenceFlow("f2", start2, end2)],
            Signals = [new SignalDefinition("sig2", "sharedSignal")]
        };
        await Cluster.GrainFactory.GetGrain<IProcessDefinitionGrain>("sig-start-wf2").DeployVersion(workflow2, "<placeholder/>");

        // Act
        var listener = Cluster.GrainFactory.GetGrain<ISignalStartEventListenerGrain>("sharedSignal");
        var instanceIds = await listener.FireSignalStartEvent();

        // Assert
        Assert.HasCount(2, instanceIds);
        Assert.AreNotEqual(instanceIds[0], instanceIds[1]);
    }

    [TestMethod]
    public async Task FireSignalStartEvent_NoRegisteredProcesses_ShouldReturnEmptyList()
    {
        // Act
        var listener = Cluster.GrainFactory.GetGrain<ISignalStartEventListenerGrain>("nonExistentSignal");
        var instanceIds = await listener.FireSignalStartEvent();

        // Assert
        Assert.HasCount(0, instanceIds);
    }

    [TestMethod]
    public async Task DeployWorkflow_ShouldAutoRegisterSignalStartEventListener()
    {
        // Arrange & Act
        var workflow = CreateSignalStartWorkflow("auto-register-signal-wf", "autoRegSignal");

        var processGrain = Cluster.GrainFactory.GetGrain<IProcessDefinitionGrain>("auto-register-signal-wf");
        await processGrain.DeployVersion(workflow, "<placeholder/>");

        // Assert — fire should create an instance without manual registration
        var listener = Cluster.GrainFactory.GetGrain<ISignalStartEventListenerGrain>("autoRegSignal");
        var instanceIds = await listener.FireSignalStartEvent();
        Assert.HasCount(1, instanceIds);
    }

    [TestMethod]
    public async Task Redeployment_ShouldUnregisterRemovedSignals()
    {
        // Arrange — deploy v1 with signal start event
        var processGrain = Cluster.GrainFactory.GetGrain<IProcessDefinitionGrain>("redeploy-signal-wf");

        var v1 = CreateSignalStartWorkflow("redeploy-signal-wf", "redeploySignal");
        await processGrain.DeployVersion(v1, "<placeholder/>");

        // Verify v1 registration works
        var listener = Cluster.GrainFactory.GetGrain<ISignalStartEventListenerGrain>("redeploySignal");
        var v1Ids = await listener.FireSignalStartEvent();
        Assert.HasCount(1, v1Ids);

        // Act — deploy v2 without signal start event (plain start event instead)
        var plainStart = new StartEvent("start");
        var task = new TaskActivity("task1");
        var end = new EndEvent("end2");
        var v2 = new WorkflowDefinition
        {
            WorkflowId = "redeploy-signal-wf",
            Activities = [plainStart, task, end],
            SequenceFlows =
            [
                new SequenceFlow("f1", plainStart, task),
                new SequenceFlow("f2", task, end)
            ]
        };
        await processGrain.DeployVersion(v2, "<placeholder/>");

        // Assert — listener should no longer create instances for this process
        var v2Ids = await listener.FireSignalStartEvent();
        Assert.HasCount(0, v2Ids);
    }

    [TestMethod]
    public async Task SendSignal_ShouldFanOut_ToBothRunningAndStartEvents()
    {
        // Workflow A: StartEvent -> SignalIntermediateCatchEvent -> EndEvent
        // (this one gets started manually and waits for signal)
        var sigDefA = new SignalDefinition("sigA", "fanoutSignal");
        var startA = new StartEvent("startA");
        var catchA = new SignalIntermediateCatchEvent("catchSig", "sigA");
        var endA = new EndEvent("endA");
        var workflowA = new WorkflowDefinition
        {
            WorkflowId = "fanout-catch-wf",
            Activities = [startA, catchA, endA],
            SequenceFlows =
            [
                new SequenceFlow("fA1", startA, catchA),
                new SequenceFlow("fA2", catchA, endA)
            ],
            Signals = [sigDefA]
        };
        var processGrainA = Cluster.GrainFactory.GetGrain<IProcessDefinitionGrain>("fanout-catch-wf");
        await processGrainA.DeployVersion(workflowA, "<placeholder/>");

        // Start an instance of Workflow A and wait for it to reach the catch event
        var instanceA = await processGrainA.CreateInstance();
        await instanceA.StartWorkflow();
        await Task.Delay(500);

        // Workflow B: SignalStartEvent -> ScriptTask -> EndEvent
        // (this one gets created by signal)
        var workflowB = CreateSignalStartWorkflow("fanout-start-wf", "fanoutSignal");
        await Cluster.GrainFactory.GetGrain<IProcessDefinitionGrain>("fanout-start-wf").DeployVersion(workflowB, "<placeholder/>");

        // Act — simulate fan-out: broadcast to running instances + fire start event
        var signalCorrelation = Cluster.GrainFactory.GetGrain<ISignalCorrelationGrain>("fanoutSignal");
        var deliveredCount = await signalCorrelation.BroadcastSignal();

        var startListener = Cluster.GrainFactory.GetGrain<ISignalStartEventListenerGrain>("fanoutSignal");
        var newInstanceIds = await startListener.FireSignalStartEvent();

        // Assert — running instance A should have completed
        var instanceAId = await instanceA.GetWorkflowInstanceId();
        var snapshotA = await QueryService.GetStateSnapshot(instanceAId);
        Assert.IsNotNull(snapshotA);
        Assert.IsTrue(snapshotA.IsCompleted, "Workflow A should complete after signal delivery");
        Assert.IsTrue(deliveredCount >= 1, "Signal should be delivered to at least one running instance");

        // Assert — new instance from Workflow B should be created and started
        Assert.HasCount(1, newInstanceIds);
        var snapshotB = await QueryService.GetStateSnapshot(newInstanceIds[0]);
        Assert.IsNotNull(snapshotB);
        Assert.IsTrue(snapshotB.IsStarted, "Workflow B should be started via signal start event");
    }

    [TestMethod]
    public async Task FailActivity_ShouldSetErrorState_WithGenericException()
    {
        // Arrange
        var workflow = CreateSignalStartWorkflowWithTask("sig-fail-500-wf", "fail500Signal");
        var processGrain = Cluster.GrainFactory.GetGrain<IProcessDefinitionGrain>("sig-fail-500-wf");
        await processGrain.DeployVersion(workflow, "<placeholder/>");

        var listener = Cluster.GrainFactory.GetGrain<ISignalStartEventListenerGrain>("fail500Signal");
        var instanceIds = await listener.FireSignalStartEvent();
        Assert.HasCount(1, instanceIds);
        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(instanceIds[0]);

        // Act
        await workflowInstance.FailActivity("task1", new Exception("Generic failure"));

        // Assert
        var snapshot = await QueryService.GetStateSnapshot(instanceIds[0]);
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
        var workflow = CreateSignalStartWorkflowWithTask("sig-fail-400-wf", "fail400Signal");
        var processGrain = Cluster.GrainFactory.GetGrain<IProcessDefinitionGrain>("sig-fail-400-wf");
        await processGrain.DeployVersion(workflow, "<placeholder/>");

        var listener = Cluster.GrainFactory.GetGrain<ISignalStartEventListenerGrain>("fail400Signal");
        var instanceIds = await listener.FireSignalStartEvent();
        Assert.HasCount(1, instanceIds);
        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(instanceIds[0]);

        // Act
        await workflowInstance.FailActivity("task1", new BadRequestActivityException("Bad input"));

        // Assert
        var snapshot = await QueryService.GetStateSnapshot(instanceIds[0]);
        Assert.IsNotNull(snapshot);
        var failedTask = snapshot.CompletedActivities.First(a => a.ActivityId == "task1");
        Assert.IsNotNull(failedTask.ErrorState);
        Assert.AreEqual(400, failedTask.ErrorState.Code);
        Assert.AreEqual("Bad input", failedTask.ErrorState.Message);
    }

    [TestMethod]
    public async Task FailActivity_ShouldTransitionToNextActivity()
    {
        // Arrange
        var workflow = CreateSignalStartWorkflowWithTask("sig-fail-transition-wf", "failTransSignal");
        var processGrain = Cluster.GrainFactory.GetGrain<IProcessDefinitionGrain>("sig-fail-transition-wf");
        await processGrain.DeployVersion(workflow, "<placeholder/>");

        var listener = Cluster.GrainFactory.GetGrain<ISignalStartEventListenerGrain>("failTransSignal");
        var instanceIds = await listener.FireSignalStartEvent();
        Assert.HasCount(1, instanceIds);
        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(instanceIds[0]);

        // Act
        await workflowInstance.FailActivity("task1", new Exception("fail"));

        // Assert — failed activity should still transition to next activity
        var snapshot = await QueryService.GetStateSnapshot(instanceIds[0]);
        Assert.IsNotNull(snapshot);
        Assert.IsTrue(snapshot.CompletedActivities.Any(a => a.ActivityId == "task1"),
            "Failed task should be in completed activities");
        Assert.IsTrue(snapshot.CompletedActivities.Count > 1,
            "Failed activity should transition — end event should also be completed");
    }

    [TestMethod]
    public async Task FailActivity_ShouldNotMergeVariables()
    {
        // Arrange
        var workflow = CreateSignalStartWorkflowWithTask("sig-fail-novar-wf", "failNoVarSignal");
        var processGrain = Cluster.GrainFactory.GetGrain<IProcessDefinitionGrain>("sig-fail-novar-wf");
        await processGrain.DeployVersion(workflow, "<placeholder/>");

        var listener = Cluster.GrainFactory.GetGrain<ISignalStartEventListenerGrain>("failNoVarSignal");
        var instanceIds = await listener.FireSignalStartEvent();
        Assert.HasCount(1, instanceIds);
        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(instanceIds[0]);

        // Act
        await workflowInstance.FailActivity("task1", new Exception("fail"));

        // Assert — no variables should be merged from the failed activity
        var snapshot = await QueryService.GetStateSnapshot(instanceIds[0]);
        Assert.IsNotNull(snapshot);
        // The only variable state should be the root scope with no merged variables from the failed task
        var rootScope = snapshot.VariableStates.FirstOrDefault();
        if (rootScope != null)
            Assert.AreEqual(0, rootScope.Variables.Count, "No variables should be merged on failure");
    }
}
