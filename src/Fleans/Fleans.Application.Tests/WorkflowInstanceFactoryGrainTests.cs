using Fleans.Application.QueryModels;
using Fleans.Application.WorkflowFactory;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Events;
using Fleans.Domain.Persistence;
using Fleans.Domain.Sequences;
using Fleans.Persistence.Events;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans.TestingHost;
using Fleans.Persistence;

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
                    .AddCustomStorageBasedLogConsistencyProviderAsDefault()
                    .AddMemoryGrainStorage(GrainStorageNames.ProcessDefinitions)
                    .AddMemoryGrainStorage(GrainStorageNames.UserTasks)
                    .ConfigureServices(services =>
                    {
                        services.AddDbContextFactory<FleanCommandDbContext>(options =>
                            options.UseSqlite("DataSource=file::memory:?cache=shared"));
                        services.AddSingleton<IWorkflowStateProjection, EfCoreWorkflowStateProjection>();
                        services.AddSingleton<EfCoreEventStore>();
                        services.AddSingleton<IEventStore>(sp => sp.GetRequiredService<EfCoreEventStore>());
                        services.AddSingleton<IProcessDefinitionRepository, StubProcessDefinitionRepository>();
                        services.AddSingleton<IWorkflowQueryService, StubWorkflowQueryService>();

                        // Ensure DB schema is created
                        var sp = services.BuildServiceProvider();
                        using var db = sp.GetRequiredService<IDbContextFactory<FleanCommandDbContext>>().CreateDbContext();
                        db.Database.EnsureCreated();
                    });
        }

        private class StubWorkflowQueryService : IWorkflowQueryService
        {
            public Task<InstanceStateSnapshot?> GetStateSnapshot(Guid workflowInstanceId) => Task.FromResult<InstanceStateSnapshot?>(null);
            public Task<IReadOnlyList<ProcessDefinitionSummary>> GetAllProcessDefinitions() => Task.FromResult<IReadOnlyList<ProcessDefinitionSummary>>([]);
            public Task<PagedResult<ProcessDefinitionSummary>> GetAllProcessDefinitions(PageRequest page) => Task.FromResult(new PagedResult<ProcessDefinitionSummary>([], 0, page.Page, page.PageSize));
            public Task<PagedResult<WorkflowInstanceInfo>> GetInstancesByKey(string processDefinitionKey, PageRequest page) => Task.FromResult(new PagedResult<WorkflowInstanceInfo>([], 0, page.Page, page.PageSize));
            public Task<PagedResult<WorkflowInstanceInfo>> GetInstancesByKeyAndVersion(string key, int version, PageRequest page) => Task.FromResult(new PagedResult<WorkflowInstanceInfo>([], 0, page.Page, page.PageSize));
            public Task<string?> GetBpmnXml(Guid instanceId) => Task.FromResult<string?>(null);
            public Task<string?> GetBpmnXmlByKey(string processDefinitionKey) => Task.FromResult<string?>(null);
            public Task<string?> GetBpmnXmlByKeyAndVersion(string key, int version) => Task.FromResult<string?>(null);
            public Task<IReadOnlyList<DTOs.UserTaskResponse>> GetPendingUserTasks(string? assignee = null, string? candidateGroup = null) => Task.FromResult<IReadOnlyList<DTOs.UserTaskResponse>>([]);
            public Task<PagedResult<DTOs.UserTaskResponse>> GetPendingUserTasks(string? assignee, string? candidateGroup, PageRequest page) => Task.FromResult(new PagedResult<DTOs.UserTaskResponse>([], 0, page.Page, page.PageSize));
            public Task<DTOs.UserTaskResponse?> GetUserTask(Guid activityInstanceId) => Task.FromResult<DTOs.UserTaskResponse?>(null);
            public Task<IReadOnlyList<Domain.States.UserTaskState>> GetActiveUserTasksForWorkflow(Guid workflowInstanceId) => Task.FromResult<IReadOnlyList<Domain.States.UserTaskState>>([]);
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

            public Task UpdateAsync(ProcessDefinition definition)
            {
                if (!_store.ContainsKey(definition.ProcessDefinitionId))
                    throw new InvalidOperationException(
                        $"Process definition '{definition.ProcessDefinitionId}' not found.");
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
