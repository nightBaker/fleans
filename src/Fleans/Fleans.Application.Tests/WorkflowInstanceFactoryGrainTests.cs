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
        public async Task CreateWorkflowInstanceGrain_ShouldCreateNewInstance_WithRegisteredWorkflow()
        {
            // Arrange
            var workflowId = "test-workflow-1";
            var factoryGrain = _cluster.GrainFactory.GetGrain<IWorkflowInstanceFactoryGrain>(0);
            var workflow = CreateSimpleWorkflow(workflowId);
            
            await factoryGrain.RegisterWorkflow(workflow);

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
            
            await factoryGrain.RegisterWorkflow(workflow);

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
        public async Task RegisterWorkflow_ShouldStoreWorkflow_ForLaterRetrieval()
        {
            // Arrange
            var workflowId = "test-workflow-2";
            var factoryGrain = _cluster.GrainFactory.GetGrain<IWorkflowInstanceFactoryGrain>(0);
            var workflow = CreateSimpleWorkflow(workflowId);

            // Act
            await factoryGrain.RegisterWorkflow(workflow);

            // Assert
            var isRegistered = await factoryGrain.IsWorkflowRegistered(workflowId);
            Assert.IsTrue(isRegistered);
        }

        [TestMethod]
        public async Task RegisterWorkflow_ShouldThrowException_WhenWorkflowIdIsNull()
        {
            // Arrange
            var factoryGrain = _cluster.GrainFactory.GetGrain<IWorkflowInstanceFactoryGrain>(0);
            var workflow = new WorkflowDefinition
            {
                WorkflowId = null!,
                Activities = new List<Domain.Activities.Activity>(),
                SequenceFlows = new List<SequenceFlow>()
            };

            // Act & Assert
            await Assert.ThrowsAsync<ArgumentException>(async () =>
            {
                await factoryGrain.RegisterWorkflow(workflow);
            });
        }

        [TestMethod]
        public async Task RegisterWorkflow_ShouldThrowException_WhenWorkflowIsNull()
        {
            // Arrange
            var factoryGrain = _cluster.GrainFactory.GetGrain<IWorkflowInstanceFactoryGrain>(0);

            // Act & Assert
            await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            {
                await factoryGrain.RegisterWorkflow(null!);
            });
        }

        [TestMethod]
        public async Task RegisterWorkflow_ShouldThrowException_WhenWorkflowAlreadyRegistered()
        {
            // Arrange
            var workflowId = "duplicate-workflow";
            var factoryGrain = _cluster.GrainFactory.GetGrain<IWorkflowInstanceFactoryGrain>(0);
            var workflow = CreateSimpleWorkflow(workflowId);
            
            await factoryGrain.RegisterWorkflow(workflow);

            // Act & Assert
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await factoryGrain.RegisterWorkflow(workflow);
            });
        }

        [TestMethod]
        public async Task IsWorkflowRegistered_ShouldReturnFalse_WhenWorkflowNotRegistered()
        {
            // Arrange
            var workflowId = "non-existent";
            var factoryGrain = _cluster.GrainFactory.GetGrain<IWorkflowInstanceFactoryGrain>(0);

            // Act
            var isRegistered = await factoryGrain.IsWorkflowRegistered(workflowId);

            // Assert
            Assert.IsFalse(isRegistered);
        }

        private static IWorkflowDefinition CreateSimpleWorkflow(string workflowId)
        {
            var start = new StartEvent("start");
            var task = new TaskActivity("task");
            var end = new EndEvent("end");

            var workflow = new WorkflowDefinition
            {
                WorkflowId = workflowId,
                Activities = new List<Activity> { start, task, end },
                SequenceFlows = new List<SequenceFlow>
                {
                    new SequenceFlow("seq1", start, task),
                    new SequenceFlow("seq2", task, end)
                }
            };

            return workflow;
        }

        private class SiloConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder hostBuilder) =>
                hostBuilder
                    .AddMemoryGrainStorage("workflowInstances")
                    .AddMemoryGrainStorage("activityInstances")
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

