using Fleans.Application.Grains;
using Fleans.Application.WorkflowFactory;
using Fleans.Domain;
using Fleans.Domain.Activities;
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

    [TestMethod]
    public async Task FireSignalStartEvent_ShouldCreateAndStartWorkflowInstance()
    {
        // Arrange
        var workflow = CreateSignalStartWorkflow("signal-start-workflow", "orderSignal");

        var factory = Cluster.GrainFactory.GetGrain<IWorkflowInstanceFactoryGrain>(0);
        await factory.DeployWorkflow(workflow, "<placeholder/>");

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
        var factory = Cluster.GrainFactory.GetGrain<IWorkflowInstanceFactoryGrain>(0);

        var workflow1 = new WorkflowDefinition
        {
            WorkflowId = "sig-start-wf1",
            Activities = [new SignalStartEvent("ss1", "sig1"), new EndEvent("end1")],
            SequenceFlows = [new SequenceFlow("f1",
                new SignalStartEvent("ss1", "sig1"), new EndEvent("end1"))],
            Signals = [new SignalDefinition("sig1", "sharedSignal")]
        };
        await factory.DeployWorkflow(workflow1, "<placeholder/>");

        var workflow2 = new WorkflowDefinition
        {
            WorkflowId = "sig-start-wf2",
            Activities = [new SignalStartEvent("ss2", "sig2"), new EndEvent("end2")],
            SequenceFlows = [new SequenceFlow("f2",
                new SignalStartEvent("ss2", "sig2"), new EndEvent("end2"))],
            Signals = [new SignalDefinition("sig2", "sharedSignal")]
        };
        await factory.DeployWorkflow(workflow2, "<placeholder/>");

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

        var factory = Cluster.GrainFactory.GetGrain<IWorkflowInstanceFactoryGrain>(0);
        await factory.DeployWorkflow(workflow, "<placeholder/>");

        // Assert — fire should create an instance without manual registration
        var listener = Cluster.GrainFactory.GetGrain<ISignalStartEventListenerGrain>("autoRegSignal");
        var instanceIds = await listener.FireSignalStartEvent();
        Assert.HasCount(1, instanceIds);
    }

    [TestMethod]
    public async Task Redeployment_ShouldUnregisterRemovedSignals()
    {
        // Arrange — deploy v1 with signal start event
        var factory = Cluster.GrainFactory.GetGrain<IWorkflowInstanceFactoryGrain>(0);

        var v1 = CreateSignalStartWorkflow("redeploy-signal-wf", "redeploySignal");
        await factory.DeployWorkflow(v1, "<placeholder/>");

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
        await factory.DeployWorkflow(v2, "<placeholder/>");

        // Assert — listener should no longer create instances for this process
        var v2Ids = await listener.FireSignalStartEvent();
        Assert.HasCount(0, v2Ids);
    }

    [TestMethod]
    public async Task SendSignal_ShouldFanOut_ToBothRunningAndStartEvents()
    {
        var factory = Cluster.GrainFactory.GetGrain<IWorkflowInstanceFactoryGrain>(0);

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
        await factory.DeployWorkflow(workflowA, "<placeholder/>");

        // Start an instance of Workflow A and wait for it to reach the catch event
        var instanceA = await factory.CreateWorkflowInstanceGrain("fanout-catch-wf");
        await instanceA.StartWorkflow();
        await Task.Delay(500);

        // Workflow B: SignalStartEvent -> ScriptTask -> EndEvent
        // (this one gets created by signal)
        var workflowB = CreateSignalStartWorkflow("fanout-start-wf", "fanoutSignal");
        await factory.DeployWorkflow(workflowB, "<placeholder/>");

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
}
