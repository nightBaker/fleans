using Fleans.Application;
using Fleans.Application.Events;
using Fleans.Application.Grains;
using Fleans.Application.Placement;
using Fleans.Application.QueryModels;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Events;
using Fleans.Domain.Persistence;
using Fleans.Domain.Sequences;
using Fleans.Worker.Placement;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans.EventSourcing.CustomStorage;
using Orleans.TestingHost;

namespace Fleans.Application.Tests
{
    [TestClass]
    public class ProcessDefinitionGrainTests
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
        public async Task GetStartableDefinition_ShouldReturnTheLatestVersion_WhenTheProcessIsActive()
        {
            // Arrange
            var workflowId = "test-workflow-1";
            var processGrain = _cluster.GrainFactory.GetGrain<IProcessDefinitionGrain>(workflowId);
            await processGrain.DeployVersion(CreateSimpleWorkflow(workflowId), "<bpmn/>");
            var latest = await processGrain.DeployVersion(CreateSimpleWorkflow(workflowId), "<bpmn/>");

            // Act
            var definition = await processGrain.GetStartableDefinition();

            // Assert
            Assert.IsNotNull(definition);
            Assert.AreEqual(latest.ProcessDefinitionId, definition.ProcessDefinitionId);
        }

        [TestMethod]
        public async Task GetStartableDefinition_ShouldReturnTheNamedVersion()
        {
            // Arrange
            var workflowId = "test-workflow-versions";
            var processGrain = _cluster.GrainFactory.GetGrain<IProcessDefinitionGrain>(workflowId);
            var v1 = await processGrain.DeployVersion(CreateSimpleWorkflow(workflowId), "<bpmn/>");
            await processGrain.DeployVersion(CreateSimpleWorkflow(workflowId), "<bpmn/>");

            // Act
            var definition = await processGrain.GetStartableDefinition(v1.ProcessDefinitionId);

            // Assert
            Assert.IsNotNull(definition);
            Assert.AreEqual(v1.ProcessDefinitionId, definition.ProcessDefinitionId);
            await Assert.ThrowsAsync<KeyNotFoundException>(
                () => processGrain.GetStartableDefinition($"{workflowId}:9:20260101T000000.0000000Z"));
        }

        [TestMethod]
        public async Task GetStartableDefinition_ShouldReturnNull_WhenTheProcessIsDisabled()
        {
            // Arrange — the one process-disabled check every start path uses (#863)
            var workflowId = "test-workflow-disabled";
            var processGrain = _cluster.GrainFactory.GetGrain<IProcessDefinitionGrain>(workflowId);
            var v1 = await processGrain.DeployVersion(CreateSimpleWorkflow(workflowId), "<bpmn/>");
            await processGrain.Disable();

            // Act & Assert — the latest version and any named version alike
            Assert.IsNull(await processGrain.GetStartableDefinition());
            Assert.IsNull(await processGrain.GetStartableDefinition(v1.ProcessDefinitionId));

            await processGrain.Enable();
            Assert.IsNotNull(await processGrain.GetStartableDefinition());
        }

        [TestMethod]
        public async Task GetStartableDefinition_ShouldThrow_WhenWorkflowNotRegistered()
        {
            // Arrange
            var processGrain = _cluster.GrainFactory.GetGrain<IProcessDefinitionGrain>("non-existent-workflow");

            // Act & Assert
            await Assert.ThrowsAsync<KeyNotFoundException>(() => processGrain.GetStartableDefinition());
        }

        [TestMethod]
        public async Task DeployVersion_ShouldPreserveMessages_WhenWorkflowHasMessageDefinitions()
        {
            // Arrange
            var processKey = "msg-workflow";
            var processGrain = _cluster.GrainFactory.GetGrain<IProcessDefinitionGrain>(processKey);

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
            await processGrain.DeployVersion(workflow, "<bpmn/>");
            var retrieved = await processGrain.GetLatestDefinition();

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
        public async Task DeployVersion_ShouldPreserveEveryDefinitionMember_AndStampProcessDefinitionId()
        {
            // Arrange
            var processKey = "esc-workflow";
            var processGrain = _cluster.GrainFactory.GetGrain<IProcessDefinitionGrain>(processKey);
            var workflow = CreateSimpleWorkflow(processKey) with
            {
                Signals = [new SignalDefinition("sig1", "orderShipped")],
                Escalations = [new EscalationDefinition("esc1", "LATE", "Late delivery")]
            };

            // Act
            var summary = await processGrain.DeployVersion(workflow, "<bpmn/>");
            var retrieved = await processGrain.GetLatestDefinition();

            // Assert
            Assert.AreEqual(summary.ProcessDefinitionId, retrieved.ProcessDefinitionId);
            Assert.AreEqual("sig1", retrieved.Signals.Single().Id);
            var escalation = retrieved.Escalations.Single();
            Assert.AreEqual("esc1", escalation.Id);
            Assert.AreEqual("LATE", escalation.EscalationCode);
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
                    .Configure<Orleans.Configuration.SiloOptions>(o =>
                        o.SiloName = "combined-test-" + Guid.NewGuid().ToString("N"))
                    .AddPlacementDirector<CorePlacementStrategy, CorePlacementDirector>()
                    .AddWorkerPlacementFilter()
                    .AddCustomStorageBasedLogConsistencyProviderAsDefault()
                    // Instances publish InstanceAdvanced to the read-model projector (#861).
                    .AddMemoryStreams(WorkflowEventsPublisher.StreamProvider)
                    .AddMemoryGrainStorage("PubSubStore")
                    .AddMemoryGrainStorage(GrainStorageNames.ProcessDefinitions)
                    .AddMemoryGrainStorage(GrainStorageNames.UserTasks)
                    .ConfigureServices(services =>
                    {
                        services.AddSingleton<IProcessDefinitionRepository, StubProcessDefinitionRepository>();
                        services.AddSingleton<IWorkflowQueryService, StubWorkflowQueryService>();
                        services.AddSingleton<IEventStore, InMemoryEventStore>();
                        services.AddSingleton<IWorkflowStateProjection, InMemoryWorkflowStateProjection>();
                        services.AddApplication();
                    });
        }

        private class StubWorkflowQueryService : IWorkflowQueryService
        {
            public Task<InstanceStateSnapshot?> GetStateSnapshot(Guid workflowInstanceId) => Task.FromResult<InstanceStateSnapshot?>(null);
            public Task<int> GetMaxVersionByKeyAsync(string processDefinitionKey) => Task.FromResult(0);
            public Task<PagedResult<ProcessDefinitionSummary>> GetAllProcessDefinitions(PageRequest page) => Task.FromResult(new PagedResult<ProcessDefinitionSummary>([], 0, page.Page, page.PageSize));
            public Task<PagedResult<ProcessDefinitionGroup>> GetProcessDefinitionGroups(PageRequest page) => Task.FromResult(new PagedResult<ProcessDefinitionGroup>([], 0, page.Page, page.PageSize));
            public Task<PagedResult<WorkflowInstanceInfo>> GetInstancesByKey(string processDefinitionKey, PageRequest page, string? businessId = null) => Task.FromResult(new PagedResult<WorkflowInstanceInfo>([], 0, page.Page, page.PageSize));
            public Task<PagedResult<WorkflowInstanceInfo>> GetInstancesByKeyAndVersion(string key, int version, PageRequest page, string? businessId = null) => Task.FromResult(new PagedResult<WorkflowInstanceInfo>([], 0, page.Page, page.PageSize));
            public Task<string?> GetBpmnXml(Guid instanceId) => Task.FromResult<string?>(null);
            public Task<string?> GetBpmnXmlByKey(string processDefinitionKey) => Task.FromResult<string?>(null);
            public Task<string?> GetBpmnXmlByKeyAndVersion(string key, int version) => Task.FromResult<string?>(null);
            public Task<IReadOnlyList<DTOs.UserTaskResponse>> GetPendingUserTasks(string? assignee = null, string? candidateGroup = null) => Task.FromResult<IReadOnlyList<DTOs.UserTaskResponse>>([]);
            public Task<PagedResult<DTOs.UserTaskResponse>> GetPendingUserTasks(string? assignee, string? candidateGroup, PageRequest page) => Task.FromResult(new PagedResult<DTOs.UserTaskResponse>([], 0, page.Page, page.PageSize));
            public Task<DTOs.UserTaskResponse?> GetUserTask(Guid activityInstanceId) => Task.FromResult<DTOs.UserTaskResponse?>(null);
            public Task<bool> UserTaskExists(Guid activityInstanceId) => Task.FromResult(false);
            public Task<IReadOnlyList<Domain.States.UserTaskState>> GetActiveUserTasksForWorkflow(Guid workflowInstanceId) => Task.FromResult<IReadOnlyList<Domain.States.UserTaskState>>([]);
            public Task<RegisteredEventsSnapshot> GetRegisteredEventsAsync(CancellationToken ct = default) =>
                Task.FromResult(new RegisteredEventsSnapshot([], [], [], [], []));
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
