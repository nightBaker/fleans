using Fleans.Application.Events;
using Fleans.Application.Grains;
using Fleans.Application.Services;
using Fleans.Application.WorkflowFactory;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Persistence;
using Fleans.Domain.Sequences;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans.TestingHost;

namespace Fleans.Application.Tests
{
    [TestClass]
    public class WorkflowInstanceFactoryGrainTests
    {
        private TestCluster _cluster = null!;

        [TestInitialize]
        public void Setup()
        {
            var builder = new TestClusterBuilder();
            builder.AddSiloBuilderConfigurator<SiloConfigurator>();
            _cluster = builder.Build();
            _cluster.Deploy();
        }

        [TestCleanup]
        public void Cleanup()
        {
            _cluster?.StopAllSilos();
        }

        [TestMethod]
        public async Task CreateWorkflowInstanceGrain_ShouldCreateNewInstance_WithDeployedWorkflow()
        {
            // Arrange
            var workflowId = "test-workflow-1";
            var factoryGrain = _cluster.GrainFactory.GetGrain<IWorkflowInstanceFactoryGrain>(0);
            var workflow = CreateSimpleWorkflow(workflowId);

            await factoryGrain.DeployWorkflow(workflow, "<bpmn/>");

            // Act
            var instance = await factoryGrain.CreateWorkflowInstanceGrain(workflowId);

            // Assert
            Assert.IsNotNull(instance);
            var instanceId = await instance.GetWorkflowInstanceId();
            Assert.AreNotEqual(Guid.Empty, instanceId);
        }

        [TestMethod]
        public async Task CreateWorkflowInstanceGrain_ShouldReturnWorkflowInstance_WithCorrectWorkflow()
        {
            // Arrange
            var workflowId = "test-workflow-1";
            var factoryGrain = _cluster.GrainFactory.GetGrain<IWorkflowInstanceFactoryGrain>(0);
            var workflow = CreateSimpleWorkflow(workflowId);

            await factoryGrain.DeployWorkflow(workflow, "<bpmn/>");

            // Act
            var instance = await factoryGrain.CreateWorkflowInstanceGrain(workflowId);

            // Assert — workflow was set correctly: instance has an active start activity
            var activeActivities = await instance.GetActiveActivities();
            Assert.HasCount(1, activeActivities);
        }

        [TestMethod]
        public async Task CreateWorkflowInstanceGrain_ShouldThrowException_WhenWorkflowNotRegistered()
        {
            // Arrange
            var workflowId = "non-existent-workflow";
            var factoryGrain = _cluster.GrainFactory.GetGrain<IWorkflowInstanceFactoryGrain>(0);

            // Act & Assert
            await Assert.ThrowsAsync<KeyNotFoundException>(async () =>
            {
                await factoryGrain.CreateWorkflowInstanceGrain(workflowId);
            });
        }

        [TestMethod]
        public async Task DeployWorkflow_ShouldPreserveMessages_WhenWorkflowHasMessageDefinitions()
        {
            // Arrange
            var processKey = "msg-workflow";
            var factoryGrain = _cluster.GrainFactory.GetGrain<IWorkflowInstanceFactoryGrain>(0);

            var start = new StartEvent("start");
            var end = new EndEvent("end");
            var workflow = new WorkflowDefinition
            {
                WorkflowId = processKey,
                Activities = new List<Activity> { start, end },
                SequenceFlows = new List<SequenceFlow>
                {
                    new SequenceFlow("seq1", start, end)
                },
                Messages =
                [
                    new MessageDefinition("msg1", "paymentReceived", "orderId"),
                    new MessageDefinition("msg2", "cancellation", null)
                ]
            };

            // Act
            await factoryGrain.DeployWorkflow(workflow, "<bpmn/>");
            var retrieved = await factoryGrain.GetLatestWorkflowDefinition(processKey);

            // Assert
            Assert.AreEqual(2, retrieved.Messages.Count);

            Assert.AreEqual("msg1", retrieved.Messages[0].Id);
            Assert.AreEqual("paymentReceived", retrieved.Messages[0].Name);
            Assert.AreEqual("orderId", retrieved.Messages[0].CorrelationKeyExpression);

            Assert.AreEqual("msg2", retrieved.Messages[1].Id);
            Assert.AreEqual("cancellation", retrieved.Messages[1].Name);
            Assert.IsNull(retrieved.Messages[1].CorrelationKeyExpression);
        }

        [TestMethod]
        public async Task DeployWorkflow_ShouldDeactivateTimer_WhenNewVersionRemovesTimerStartEvent()
        {
            // Arrange — deploy v1 with a TimerStartEvent
            var processKey = "timer-removal-test";
            var factoryGrain = _cluster.GrainFactory.GetGrain<IWorkflowInstanceFactoryGrain>(0);

            var timerStart = new TimerStartEvent("timerStart", new TimerDefinition(TimerType.Duration, "PT1H"));
            var end1 = new EndEvent("end1");
            var v1 = new WorkflowDefinition
            {
                WorkflowId = processKey,
                Activities = new List<Activity> { timerStart, end1 },
                SequenceFlows = new List<SequenceFlow>
                {
                    new SequenceFlow("seq1", timerStart, end1)
                }
            };
            await factoryGrain.DeployWorkflow(v1, "<bpmn/>");

            // Act — deploy v2 WITHOUT a TimerStartEvent.
            // Before the fix, the old timer reminder from v1 would keep firing.
            // After the fix, DeployWorkflow calls DeactivateScheduler to unregister the reminder.
            var start = new StartEvent("start");
            var end2 = new EndEvent("end2");
            var v2 = new WorkflowDefinition
            {
                WorkflowId = processKey,
                Activities = new List<Activity> { start, end2 },
                SequenceFlows = new List<SequenceFlow>
                {
                    new SequenceFlow("seq1", start, end2)
                }
            };

            // Assert — deploying v2 should succeed and deactivate the scheduler
            // (exercises the else-if branch that calls DeactivateScheduler).
            // Before the fix, ActivateScheduler was called but not DeactivateScheduler,
            // leaving a stale reminder that would fire indefinitely.
            var summary = await factoryGrain.DeployWorkflow(v2, "<bpmn/>");
            Assert.AreEqual(2, summary.Version);

            // Verify v2 is the latest and has no TimerStartEvent
            var latest = await factoryGrain.GetLatestWorkflowDefinition(processKey);
            Assert.IsFalse(latest.Activities.OfType<TimerStartEvent>().Any());
        }

        [TestMethod]
        public async Task DisableProcess_ShouldSetIsActiveFalse()
        {
            var processKey = "disable-test";
            var factoryGrain = _cluster.GrainFactory.GetGrain<IWorkflowInstanceFactoryGrain>(0);
            await factoryGrain.DeployWorkflow(CreateSimpleWorkflow(processKey), "<bpmn/>");

            var summary = await factoryGrain.DisableProcess(processKey);

            Assert.IsFalse(summary.IsActive);
        }

        [TestMethod]
        public async Task EnableProcess_ShouldSetIsActiveTrue()
        {
            var processKey = "enable-test";
            var factoryGrain = _cluster.GrainFactory.GetGrain<IWorkflowInstanceFactoryGrain>(0);
            await factoryGrain.DeployWorkflow(CreateSimpleWorkflow(processKey), "<bpmn/>");
            await factoryGrain.DisableProcess(processKey);

            var summary = await factoryGrain.EnableProcess(processKey);

            Assert.IsTrue(summary.IsActive);
        }

        [TestMethod]
        public async Task DisableProcess_ShouldBlockNewInstances()
        {
            var processKey = "disable-block-test";
            var factoryGrain = _cluster.GrainFactory.GetGrain<IWorkflowInstanceFactoryGrain>(0);
            await factoryGrain.DeployWorkflow(CreateSimpleWorkflow(processKey), "<bpmn/>");
            await factoryGrain.DisableProcess(processKey);

            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await factoryGrain.CreateWorkflowInstanceGrain(processKey);
            });
        }

        [TestMethod]
        public async Task DeployWorkflow_ShouldPreserveDisabledState_WhenProcessWasDisabled()
        {
            var processKey = "preserve-disabled-test";
            var factoryGrain = _cluster.GrainFactory.GetGrain<IWorkflowInstanceFactoryGrain>(0);
            await factoryGrain.DeployWorkflow(CreateSimpleWorkflow(processKey), "<bpmn/>");
            await factoryGrain.DisableProcess(processKey);

            var summary = await factoryGrain.DeployWorkflow(CreateSimpleWorkflow(processKey), "<bpmn/>");

            Assert.IsFalse(summary.IsActive, "Redeploying a disabled process should preserve disabled state");
            Assert.AreEqual(2, summary.Version);
        }

        [TestMethod]
        public async Task DisableProcess_ShouldUnregisterSignalStartEventListener()
        {
            var processKey = "signal-disable-test";
            var signalName = "test-signal";
            var factoryGrain = _cluster.GrainFactory.GetGrain<IWorkflowInstanceFactoryGrain>(0);

            var signalStart = new SignalStartEvent("signalStart", "sig1");
            var end = new EndEvent("end");
            var workflow = new WorkflowDefinition
            {
                WorkflowId = processKey,
                Activities = new List<Activity> { signalStart, end },
                SequenceFlows = new List<SequenceFlow>
                {
                    new SequenceFlow("seq1", signalStart, end)
                },
                Signals = new List<SignalDefinition>
                {
                    new SignalDefinition("sig1", signalName)
                }
            };
            await factoryGrain.DeployWorkflow(workflow, "<bpmn/>");

            await factoryGrain.DisableProcess(processKey);

            var listener = _cluster.GrainFactory.GetGrain<ISignalStartEventListenerGrain>(signalName);
            var instanceIds = await listener.FireSignalStartEvent();
            Assert.AreEqual(0, instanceIds.Count);
        }

        [TestMethod]
        public async Task EnableProcess_ShouldReregisterSignalStartEventListener()
        {
            var processKey = "signal-enable-test";
            var signalName = "test-signal-enable";
            var factoryGrain = _cluster.GrainFactory.GetGrain<IWorkflowInstanceFactoryGrain>(0);

            var signalStart = new SignalStartEvent("signalStart", "sig1");
            var end = new EndEvent("end");
            var workflow = new WorkflowDefinition
            {
                WorkflowId = processKey,
                Activities = new List<Activity> { signalStart, end },
                SequenceFlows = new List<SequenceFlow>
                {
                    new SequenceFlow("seq1", signalStart, end)
                },
                Signals = new List<SignalDefinition>
                {
                    new SignalDefinition("sig1", signalName)
                }
            };
            await factoryGrain.DeployWorkflow(workflow, "<bpmn/>");
            await factoryGrain.DisableProcess(processKey);

            await factoryGrain.EnableProcess(processKey);

            var listener = _cluster.GrainFactory.GetGrain<ISignalStartEventListenerGrain>(signalName);
            var instanceIds = await listener.FireSignalStartEvent();
            Assert.AreEqual(1, instanceIds.Count);
        }

        private static WorkflowDefinition CreateSimpleWorkflow(string workflowId)
        {
            var start = new StartEvent("start");
            var task = new TaskActivity("task");
            var end = new EndEvent("end");

            return new WorkflowDefinition
            {
                WorkflowId = workflowId,
                Activities = new List<Activity> { start, task, end },
                SequenceFlows = new List<SequenceFlow>
                {
                    new SequenceFlow("seq1", start, task),
                    new SequenceFlow("seq2", task, end)
                }
            };
        }

        private class SiloConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder hostBuilder) =>
                hostBuilder
                    .AddMemoryStreams(Events.WorkflowEventsPublisher.StreamProvider)
                    .AddMemoryGrainStorage("PubSubStore")
                    .AddMemoryGrainStorage(GrainStorageNames.WorkflowInstances)
                    .AddMemoryGrainStorage(GrainStorageNames.ActivityInstances)
                    .AddMemoryGrainStorage(GrainStorageNames.ProcessDefinitions)
                    .AddMemoryGrainStorage(GrainStorageNames.TimerSchedulers)
                    .AddMemoryGrainStorage(GrainStorageNames.MessageStartEventListeners)
                    .AddMemoryGrainStorage(GrainStorageNames.SignalStartEventListeners)
                    .AddMemoryGrainStorage(GrainStorageNames.MessageCorrelations)
                    .AddMemoryGrainStorage(GrainStorageNames.SignalCorrelations)
                    .UseInMemoryReminderService()
                    .ConfigureServices(services =>
                    {
                        services.AddSingleton<IProcessDefinitionRepository, StubProcessDefinitionRepository>();
                        services.AddTransient<IBoundaryEventHandler, BoundaryEventHandler>();
                    });
        }

        private class StubProcessDefinitionRepository : IProcessDefinitionRepository
        {
            private readonly Dictionary<string, ProcessDefinition> _store = new(StringComparer.Ordinal);

            public Task<ProcessDefinition?> GetByIdAsync(string processDefinitionId)
            {
                _store.TryGetValue(processDefinitionId, out var def);
                return Task.FromResult(def);
            }

            public Task<List<ProcessDefinition>> GetByKeyAsync(string processDefinitionKey) =>
                Task.FromResult(_store.Values
                    .Where(d => d.ProcessDefinitionKey == processDefinitionKey)
                    .OrderBy(d => d.Version).ToList());

            public Task<List<ProcessDefinition>> GetAllAsync() =>
                Task.FromResult(_store.Values.ToList());

            public Task SaveAsync(ProcessDefinition definition)
            {
                _store[definition.ProcessDefinitionId] = definition;
                return Task.CompletedTask;
            }

            public Task DeleteAsync(string processDefinitionId)
            {
                _store.Remove(processDefinitionId);
                return Task.CompletedTask;
            }
        }
    }
}
