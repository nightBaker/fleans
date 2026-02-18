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
            var definition = await instance.GetWorkflowDefinition();

            // Assert
            Assert.AreEqual(workflowId, definition.WorkflowId);
            Assert.AreEqual(workflow.Activities.Count, definition.Activities.Count);
            Assert.AreEqual(workflow.SequenceFlows.Count, definition.SequenceFlows.Count);
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
                    .AddMemoryGrainStorage(GrainStorageNames.WorkflowInstances)
                    .AddMemoryGrainStorage(GrainStorageNames.ActivityInstances)
                    .AddMemoryGrainStorage(GrainStorageNames.ProcessDefinitions)
                    .ConfigureServices(services =>
                    {
                        services.AddSingleton<IProcessDefinitionRepository, StubProcessDefinitionRepository>();
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
