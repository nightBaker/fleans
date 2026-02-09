using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using Orleans.Serialization;
using Orleans.TestingHost;
using System.Dynamic;

namespace Fleans.Domain.Tests
{
    [TestClass]
    public class WorkflowInstanceTests
    {
        private TestCluster _cluster = null!;

        [TestInitialize]
        public void Setup()
        {
            _cluster = CreateCluster();
        }

        [TestCleanup]
        public void Cleanup()
        {
            _cluster?.StopAllSilos();
        }

        [TestMethod]
        public async Task SetWorkflow_ShouldInitializeWorkflow_AndStartWithStartEvent()
        {
            // Arrange
            var workflow = CreateSimpleWorkflow();
            var workflowInstance = _cluster.GrainFactory.GetGrain<IWorkflowInstance>(Guid.NewGuid());

            // Act
            await workflowInstance.SetWorkflow(workflow);
            var state = await workflowInstance.GetState();
            await workflowInstance.StartWorkflow();

            // Assert
            var definition = await workflowInstance.GetWorkflowDefinition();
            Assert.AreEqual(workflow.WorkflowId, definition.WorkflowId);
            Assert.IsTrue(await state.IsStarted());
            
            var completedActivities = await state.GetCompletedActivities();
            Assert.HasCount(1, completedActivities);
            
            var firstActivity = await completedActivities[0].GetCurrentActivity();
            Assert.IsInstanceOfType(firstActivity, typeof(StartEvent));
        }

        [TestMethod]
        public async Task SetWorkflow_ShouldThrowException_WhenWorkflowAlreadySet()
        {
            // Arrange
            var workflow = CreateSimpleWorkflow();
            var workflowInstance = _cluster.GrainFactory.GetGrain<IWorkflowInstance>(Guid.NewGuid());
            await workflowInstance.SetWorkflow(workflow);

            // Act & Assert
            await Assert.ThrowsAsync<ArgumentException>(async () =>
            {
                await workflowInstance.SetWorkflow(workflow);
            });
        }

        [TestMethod]
        public async Task StartWorkflow_ShouldExecuteStartEvent_AndTransitionToNextActivity()
        {
            // Arrange
            var workflow = CreateSimpleWorkflow();
            var workflowInstance = _cluster.GrainFactory.GetGrain<IWorkflowInstance>(Guid.NewGuid());
            await workflowInstance.SetWorkflow(workflow);

            // Act
            await workflowInstance.StartWorkflow();

            // Assert
            var state = await workflowInstance.GetState();
            var activeActivities = await state.GetActiveActivities();
            
            // After start event completes, should transition to task
            var taskActivity = activeActivities.FirstOrDefault();
            if (taskActivity != null)
            {
                var activity = await taskActivity.GetCurrentActivity();
                Assert.IsInstanceOfType(activity, typeof(TaskActivity));
            }
        }

        [TestMethod]
        public async Task CompleteActivity_ShouldMergeVariables_AndContinueExecution()
        {
            // Arrange
            var workflow = CreateSimpleWorkflow();
            var workflowInstance = _cluster.GrainFactory.GetGrain<IWorkflowInstance>(Guid.NewGuid());
            await workflowInstance.SetWorkflow(workflow);
            await workflowInstance.StartWorkflow();

            dynamic variables = new ExpandoObject();
            variables.result = "completed";
            variables.value = 42;

            // Act
            await workflowInstance.CompleteActivity("task", variables);

            // Assert
            var state = await workflowInstance.GetState();
            var variableStates = await state.GetVariableStates();
            
            // Variables should be merged into state
            Assert.IsNotEmpty(variableStates);
        }

        [TestMethod]
        public async Task CompleteActivity_ShouldThrowException_WhenActivityNotFound()
        {
            // Arrange
            var workflow = CreateSimpleWorkflow();
            var workflowInstance = _cluster.GrainFactory.GetGrain<IWorkflowInstance>(Guid.NewGuid());
            await workflowInstance.SetWorkflow(workflow);
            await workflowInstance.StartWorkflow();

            var variables = new ExpandoObject();

            // Act & Assert
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await workflowInstance.CompleteActivity("non-existent-activity", variables);
            });
        }

        [TestMethod]
        public async Task FailActivity_ShouldMarkActivityAsFailed_AndContinueExecution()
        {
            // Arrange
            var workflow = CreateSimpleWorkflow();
            var workflowInstance = _cluster.GrainFactory.GetGrain<IWorkflowInstance>(Guid.NewGuid());
            await workflowInstance.SetWorkflow(workflow);
            await workflowInstance.StartWorkflow();

            var exception = new Exception("Test error");

            // Act
            await workflowInstance.FailActivity("task", exception);

            // Assert
            var state = await workflowInstance.GetState();
            var completedActivities = await state.GetCompletedActivities();
            
            // Activity should be marked as completed (even though it failed)
            Assert.IsNotEmpty(completedActivities);
        }

        [TestMethod]
        public async Task GetCreatedAt_ShouldReturnNull_BeforeSetWorkflow()
        {
            // Arrange
            var workflowInstance = _cluster.GrainFactory.GetGrain<IWorkflowInstance>(Guid.NewGuid());

            // Act
            var createdAt = await workflowInstance.GetCreatedAt();

            // Assert
            Assert.IsNull(createdAt);
        }

        [TestMethod]
        public async Task GetCreatedAt_ShouldReturnTimestamp_AfterSetWorkflow()
        {
            // Arrange
            var workflow = CreateSimpleWorkflow();
            var workflowInstance = _cluster.GrainFactory.GetGrain<IWorkflowInstance>(Guid.NewGuid());

            var before = DateTimeOffset.UtcNow;
            await workflowInstance.SetWorkflow(workflow);
            var after = DateTimeOffset.UtcNow;

            // Act
            var createdAt = await workflowInstance.GetCreatedAt();

            // Assert
            Assert.IsNotNull(createdAt);
            Assert.IsTrue(createdAt >= before, "CreatedAt should be >= test start time");
            Assert.IsTrue(createdAt <= after, "CreatedAt should be <= test end time");
        }

        [TestMethod]
        public async Task GetExecutionStartedAt_ShouldReturnNull_BeforeStartWorkflow()
        {
            // Arrange
            var workflow = CreateSimpleWorkflow();
            var workflowInstance = _cluster.GrainFactory.GetGrain<IWorkflowInstance>(Guid.NewGuid());
            await workflowInstance.SetWorkflow(workflow);

            // Act
            var executionStartedAt = await workflowInstance.GetExecutionStartedAt();

            // Assert
            Assert.IsNull(executionStartedAt);
        }

        [TestMethod]
        public async Task GetExecutionStartedAt_ShouldReturnTimestamp_AfterStartWorkflow()
        {
            // Arrange
            var workflow = CreateSimpleWorkflow();
            var workflowInstance = _cluster.GrainFactory.GetGrain<IWorkflowInstance>(Guid.NewGuid());
            await workflowInstance.SetWorkflow(workflow);

            var before = DateTimeOffset.UtcNow;
            await workflowInstance.StartWorkflow();
            var after = DateTimeOffset.UtcNow;

            // Act
            var executionStartedAt = await workflowInstance.GetExecutionStartedAt();

            // Assert
            Assert.IsNotNull(executionStartedAt);
            Assert.IsTrue(executionStartedAt >= before, "ExecutionStartedAt should be >= test start time");
            Assert.IsTrue(executionStartedAt <= after, "ExecutionStartedAt should be <= test end time");
        }

        [TestMethod]
        public async Task GetCreatedAt_ShouldNotChange_AfterStartWorkflow()
        {
            // Arrange
            var workflow = CreateSimpleWorkflow();
            var workflowInstance = _cluster.GrainFactory.GetGrain<IWorkflowInstance>(Guid.NewGuid());
            await workflowInstance.SetWorkflow(workflow);

            var createdAtBefore = await workflowInstance.GetCreatedAt();
            Assert.IsNotNull(createdAtBefore);

            // Act
            await workflowInstance.StartWorkflow();

            // Assert
            var createdAtAfter = await workflowInstance.GetCreatedAt();
            Assert.AreEqual(createdAtBefore, createdAtAfter, "CreatedAt should not change after StartWorkflow");
        }

        [TestMethod]
        public async Task GetCompletedAt_ShouldReturnNull_BeforeCompletion()
        {
            // Arrange
            var workflow = CreateSimpleWorkflow();
            var workflowInstance = _cluster.GrainFactory.GetGrain<IWorkflowInstance>(Guid.NewGuid());
            await workflowInstance.SetWorkflow(workflow);
            await workflowInstance.StartWorkflow();

            // Act
            var completedAt = await workflowInstance.GetCompletedAt();

            // Assert
            Assert.IsNull(completedAt, "CompletedAt should be null while workflow is still running");
        }

        [TestMethod]
        public async Task GetCompletedAt_ShouldReturnTimestamp_AfterCompletion()
        {
            // Arrange
            var workflow = CreateSimpleWorkflow();
            var workflowInstance = _cluster.GrainFactory.GetGrain<IWorkflowInstance>(Guid.NewGuid());
            await workflowInstance.SetWorkflow(workflow);
            await workflowInstance.StartWorkflow();

            var before = DateTimeOffset.UtcNow;
            await workflowInstance.CompleteActivity("task", new ExpandoObject());
            var after = DateTimeOffset.UtcNow;

            // Act
            var completedAt = await workflowInstance.GetCompletedAt();

            // Assert
            Assert.IsNotNull(completedAt);
            Assert.IsTrue(completedAt >= before, "CompletedAt should be >= test start time");
            Assert.IsTrue(completedAt <= after, "CompletedAt should be <= test end time");
        }

        [TestMethod]
        public async Task GetInstanceInfo_ShouldReturnAllFields()
        {
            // Arrange
            var workflow = CreateSimpleWorkflow();
            var workflowInstance = _cluster.GrainFactory.GetGrain<IWorkflowInstance>(Guid.NewGuid());
            await workflowInstance.SetWorkflow(workflow);
            await workflowInstance.StartWorkflow();
            await workflowInstance.CompleteActivity("task", new ExpandoObject());

            // Act
            var info = await workflowInstance.GetInstanceInfo();

            // Assert
            Assert.IsTrue(info.IsStarted);
            Assert.IsTrue(info.IsCompleted);
            Assert.IsNotNull(info.CreatedAt);
            Assert.IsNotNull(info.ExecutionStartedAt);
            Assert.IsNotNull(info.CompletedAt);
        }

        [TestMethod]
        public async Task GetWorkflowInstanceId_ShouldReturnCorrectGuid()
        {
            // Arrange
            var instanceId = Guid.NewGuid();
            var workflowInstance = _cluster.GrainFactory.GetGrain<IWorkflowInstance>(instanceId);

            // Act
            var result = await workflowInstance.GetWorkflowInstanceId();

            // Assert
            Assert.AreEqual(instanceId, result);
        }

        private static TestCluster CreateCluster()
        {
            var builder = new TestClusterBuilder();
            builder.AddSiloBuilderConfigurator<SiloConfigurator>();
            var cluster = builder.Build();
            cluster.Deploy();
            return cluster;
        }

        private static IWorkflowDefinition CreateSimpleWorkflow()
        {
            var start = new StartEvent("start");
            var task = new TaskActivity("task");
            var end = new EndEvent("end");

            return new WorkflowDefinition
            {
                WorkflowId = "test-workflow",
                Activities = new List<Activity> { start, task, end },
                SequenceFlows = new List<SequenceFlow>
                {
                    new SequenceFlow("seq1", start, task),
                    new SequenceFlow("seq2", task, end)
                }
            };
        }

        class SiloConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder hostBuilder) =>
                hostBuilder.ConfigureServices(services => services.AddSerializer(serializerBuilder =>
                {
                    serializerBuilder.AddNewtonsoftJsonSerializer(
                        isSupported: type => type == typeof(ExpandoObject),
                        new Newtonsoft.Json.JsonSerializerSettings
                        {
                            TypeNameHandling = Newtonsoft.Json.TypeNameHandling.All
                        });
                }));
        }
    }
}

