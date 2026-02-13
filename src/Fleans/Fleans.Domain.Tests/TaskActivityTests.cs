using Fleans.Domain.Activities;
using Fleans.Domain.Errors;
using Fleans.Domain.Sequences;
using Orleans.Serialization;
using Orleans.TestingHost;
using System.Dynamic;

namespace Fleans.Domain.Tests
{
    [TestClass]
    public class TaskActivityTests
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
        public async Task GetNextActivities_ShouldReturnSingleNextActivity()
        {
            // Arrange
            var task1 = new TaskActivity("task1");
            var task2 = new TaskActivity("task2");
            var end = new EndEvent("end");
            var start = new StartEvent("start");

            var workflow = new WorkflowDefinition
            {
                WorkflowId = "test",
                Activities = new List<Activity> { start, task1, task2, end },
                SequenceFlows = new List<SequenceFlow>
                {
                    new SequenceFlow("seq0", start, task1),
                    new SequenceFlow("seq1", task1, task2),
                    new SequenceFlow("seq2", task2, end)
                }
            };

            var workflowInstance = _cluster.GrainFactory.GetGrain<IWorkflowInstance>(Guid.NewGuid());
            await workflowInstance.SetWorkflow(workflow);
            await workflowInstance.StartWorkflow();

            // Act — complete task1 so it transitions to task2
            await workflowInstance.CompleteActivity("task1", new ExpandoObject());

            // Assert — task2 should now be the active activity
            var snapshot = await workflowInstance.GetStateSnapshot();
            Assert.IsFalse(snapshot.IsCompleted);
            Assert.HasCount(1, snapshot.ActiveActivities);
            Assert.AreEqual("task2", snapshot.ActiveActivities[0].ActivityId);
        }

        [TestMethod]
        public async Task GetNextActivities_ShouldReturnEmptyList_WhenNoNextActivity()
        {
            // Arrange
            var workflow = CreateSimpleWorkflow();
            var workflowInstance = _cluster.GrainFactory.GetGrain<IWorkflowInstance>(Guid.NewGuid());
            await workflowInstance.SetWorkflow(workflow);
            await workflowInstance.StartWorkflow();

            // Act — complete task so end event fires and workflow completes
            await workflowInstance.CompleteActivity("task", new ExpandoObject());

            // Assert — workflow should be completed (end event has no next activities)
            var snapshot = await workflowInstance.GetStateSnapshot();
            Assert.IsTrue(snapshot.IsCompleted);
            Assert.HasCount(0, snapshot.ActiveActivities);
        }

        [TestMethod]
        public async Task ExecuteAsync_ShouldMarkActivityAsExecuting()
        {
            // Arrange
            var workflow = CreateSimpleWorkflow();
            var workflowInstance = _cluster.GrainFactory.GetGrain<IWorkflowInstance>(Guid.NewGuid());
            await workflowInstance.SetWorkflow(workflow);

            // Act
            await workflowInstance.StartWorkflow();

            // Assert — after starting, the task should be active
            var snapshot = await workflowInstance.GetStateSnapshot();
            Assert.HasCount(1, snapshot.ActiveActivities);
            Assert.AreEqual("task", snapshot.ActiveActivities[0].ActivityId);
            Assert.AreEqual("TaskActivity", snapshot.ActiveActivities[0].ActivityType);
        }

        [TestMethod]
        public async Task CompleteTask_ShouldTransitionToEndEvent_AndCompleteWorkflow()
        {
            // Arrange
            var workflow = CreateSimpleWorkflow();
            var workflowInstance = _cluster.GrainFactory.GetGrain<IWorkflowInstance>(Guid.NewGuid());
            await workflowInstance.SetWorkflow(workflow);
            await workflowInstance.StartWorkflow();

            dynamic variables = new ExpandoObject();
            variables.result = "done";

            // Act
            await workflowInstance.CompleteActivity("task", (ExpandoObject)variables);

            // Assert
            var snapshot = await workflowInstance.GetStateSnapshot();
            Assert.IsTrue(snapshot.IsCompleted);

            var completedIds = snapshot.CompletedActivityIds;
            CollectionAssert.Contains(completedIds, "start");
            CollectionAssert.Contains(completedIds, "task");
            CollectionAssert.Contains(completedIds, "end");
        }

        [TestMethod]
        public async Task CompleteTask_ShouldMergeVariablesIntoState()
        {
            // Arrange
            var workflow = CreateSimpleWorkflow();
            var workflowInstance = _cluster.GrainFactory.GetGrain<IWorkflowInstance>(Guid.NewGuid());
            await workflowInstance.SetWorkflow(workflow);
            await workflowInstance.StartWorkflow();

            dynamic variables = new ExpandoObject();
            variables.key1 = "value1";
            variables.key2 = 42;

            // Act
            await workflowInstance.CompleteActivity("task", (ExpandoObject)variables);

            // Assert
            var snapshot = await workflowInstance.GetStateSnapshot();
            var variableStates = snapshot.VariableStates;
            Assert.IsTrue(variableStates.Count > 0);

            var vars = variableStates.First().Variables;
            Assert.AreEqual(2, vars.Count);
            Assert.AreEqual("value1", vars["key1"]);
            Assert.AreEqual("42", vars["key2"]);
        }

        [TestMethod]
        public async Task CompleteTask_ShouldMarkActivityInstanceAsCompleted()
        {
            // Arrange
            var workflow = CreateSimpleWorkflow();
            var workflowInstance = _cluster.GrainFactory.GetGrain<IWorkflowInstance>(Guid.NewGuid());
            await workflowInstance.SetWorkflow(workflow);
            await workflowInstance.StartWorkflow();

            // Act
            await workflowInstance.CompleteActivity("task", new ExpandoObject());

            // Assert
            var snapshot = await workflowInstance.GetStateSnapshot();
            var completedTask = snapshot.CompletedActivities.FirstOrDefault(a => a.ActivityId == "task");
            Assert.IsNotNull(completedTask);
            Assert.IsTrue(completedTask.IsCompleted);
            Assert.IsNull(completedTask.ErrorState);
        }

        [TestMethod]
        public async Task CompleteTask_ShouldHaveNoActiveActivities_AfterWorkflowCompletes()
        {
            // Arrange
            var workflow = CreateSimpleWorkflow();
            var workflowInstance = _cluster.GrainFactory.GetGrain<IWorkflowInstance>(Guid.NewGuid());
            await workflowInstance.SetWorkflow(workflow);
            await workflowInstance.StartWorkflow();

            // Act
            await workflowInstance.CompleteActivity("task", new ExpandoObject());

            // Assert
            var snapshot = await workflowInstance.GetStateSnapshot();
            Assert.HasCount(0, snapshot.ActiveActivities);
        }

        [TestMethod]
        public async Task CompleteTask_WithMultipleTasks_ShouldExecuteInSequence()
        {
            // Arrange
            var task1 = new TaskActivity("task1");
            var task2 = new TaskActivity("task2");
            var start = new StartEvent("start");
            var end = new EndEvent("end");

            var workflow = new WorkflowDefinition
            {
                WorkflowId = "test-workflow",
                Activities = new List<Activity> { start, task1, task2, end },
                SequenceFlows = new List<SequenceFlow>
                {
                    new SequenceFlow("seq1", start, task1),
                    new SequenceFlow("seq2", task1, task2),
                    new SequenceFlow("seq3", task2, end)
                }
            };

            var workflowInstance = _cluster.GrainFactory.GetGrain<IWorkflowInstance>(Guid.NewGuid());
            await workflowInstance.SetWorkflow(workflow);
            await workflowInstance.StartWorkflow();

            // Act — complete first task
            await workflowInstance.CompleteActivity("task1", new ExpandoObject());

            // Assert — second task should now be active
            var snapshot = await workflowInstance.GetStateSnapshot();
            Assert.IsFalse(snapshot.IsCompleted);
            Assert.HasCount(1, snapshot.ActiveActivities);
            Assert.AreEqual("task2", snapshot.ActiveActivities[0].ActivityId);

            // Act — complete second task
            await workflowInstance.CompleteActivity("task2", new ExpandoObject());

            // Assert — workflow should be completed
            snapshot = await workflowInstance.GetStateSnapshot();
            Assert.IsTrue(snapshot.IsCompleted);
        }

        [TestMethod]
        public async Task FailTask_ShouldSetErrorState_WithCode500()
        {
            // Arrange
            var workflow = CreateSimpleWorkflow();
            var workflowInstance = _cluster.GrainFactory.GetGrain<IWorkflowInstance>(Guid.NewGuid());
            await workflowInstance.SetWorkflow(workflow);
            await workflowInstance.StartWorkflow();

            // Act
            await workflowInstance.FailActivity("task", new Exception("Task failed"));

            // Assert
            var snapshot = await workflowInstance.GetStateSnapshot();
            var failedSnapshot = snapshot.CompletedActivities.First(a => a.ActivityId == "task");
            Assert.IsNotNull(failedSnapshot.ErrorState);
            Assert.AreEqual(500, failedSnapshot.ErrorState.Code);
            Assert.AreEqual("Task failed", failedSnapshot.ErrorState.Message);
        }

        [TestMethod]
        public async Task FailTask_WithActivityException_ShouldSetCustomErrorCode()
        {
            // Arrange
            var workflow = CreateSimpleWorkflow();
            var workflowInstance = _cluster.GrainFactory.GetGrain<IWorkflowInstance>(Guid.NewGuid());
            await workflowInstance.SetWorkflow(workflow);
            await workflowInstance.StartWorkflow();

            // Act
            await workflowInstance.FailActivity("task", new BadRequestActivityException("Bad input"));

            // Assert
            var snapshot = await workflowInstance.GetStateSnapshot();
            var failedSnapshot = snapshot.CompletedActivities.First(a => a.ActivityId == "task");
            Assert.IsNotNull(failedSnapshot.ErrorState);
            Assert.AreEqual(400, failedSnapshot.ErrorState.Code);
            Assert.AreEqual("Bad input", failedSnapshot.ErrorState.Message);
        }

        [TestMethod]
        public async Task FailTask_ShouldMarkAsCompleted_AndTransitionToNextActivity()
        {
            // Arrange
            var workflow = CreateSimpleWorkflow();
            var workflowInstance = _cluster.GrainFactory.GetGrain<IWorkflowInstance>(Guid.NewGuid());
            await workflowInstance.SetWorkflow(workflow);
            await workflowInstance.StartWorkflow();

            // Act
            await workflowInstance.FailActivity("task", new Exception("Task error"));

            // Assert
            var snapshot = await workflowInstance.GetStateSnapshot();
            Assert.IsTrue(snapshot.IsCompleted);
            Assert.HasCount(0, snapshot.ActiveActivities);

            var completedIds = snapshot.CompletedActivityIds;
            CollectionAssert.Contains(completedIds, "task");
            CollectionAssert.Contains(completedIds, "end");
        }

        [TestMethod]
        public async Task FailTask_ShouldNotMergeVariables()
        {
            // Arrange
            var workflow = CreateSimpleWorkflow();
            var workflowInstance = _cluster.GrainFactory.GetGrain<IWorkflowInstance>(Guid.NewGuid());
            await workflowInstance.SetWorkflow(workflow);
            await workflowInstance.StartWorkflow();

            // Act
            await workflowInstance.FailActivity("task", new Exception("Task error"));

            // Assert
            var snapshot = await workflowInstance.GetStateSnapshot();
            foreach (var vs in snapshot.VariableStates)
            {
                Assert.AreEqual(0, vs.Variables.Count, "No variables should be merged on failure");
            }
        }

        [TestMethod]
        public async Task GetCreatedAt_ShouldReturnNull_BeforeSetActivity()
        {
            // Arrange
            var activity = _cluster.GrainFactory.GetGrain<IActivityInstance>(Guid.NewGuid());
            await activity.SetVariablesId(Guid.NewGuid());

            // Act — grain exists but SetActivity not yet called, so we can only check via snapshot
            // Note: calling GetCreatedAt before SetActivity would fail since _currentActivity is null for GetSnapshot,
            // but GetCreatedAt itself works fine
            var createdAt = await activity.GetCreatedAt();

            // Assert
            Assert.IsNull(createdAt);
        }

        [TestMethod]
        public async Task GetCreatedAt_ShouldReturnTimestamp_AfterSetActivity()
        {
            // Arrange
            var before = DateTimeOffset.UtcNow;
            var activity = _cluster.GrainFactory.GetGrain<IActivityInstance>(Guid.NewGuid());
            await activity.SetActivity("task", "TaskActivity");
            var after = DateTimeOffset.UtcNow;

            // Act
            var createdAt = await activity.GetCreatedAt();

            // Assert
            Assert.IsNotNull(createdAt);
            Assert.IsTrue(createdAt >= before, "CreatedAt should be >= test start time");
            Assert.IsTrue(createdAt <= after, "CreatedAt should be <= test end time");
        }

        [TestMethod]
        public async Task GetExecutionStartedAt_ShouldReturnNull_BeforeExecution()
        {
            // Arrange
            var activity = _cluster.GrainFactory.GetGrain<IActivityInstance>(Guid.NewGuid());
            await activity.SetActivity("task", "TaskActivity");

            // Act
            var executionStartedAt = await activity.GetExecutionStartedAt();

            // Assert
            Assert.IsNull(executionStartedAt);
        }

        [TestMethod]
        public async Task GetExecutionStartedAt_ShouldReturnTimestamp_AfterExecution()
        {
            // Arrange
            var workflow = CreateSimpleWorkflow();
            var workflowInstance = _cluster.GrainFactory.GetGrain<IWorkflowInstance>(Guid.NewGuid());
            await workflowInstance.SetWorkflow(workflow);

            var before = DateTimeOffset.UtcNow;
            await workflowInstance.StartWorkflow();
            var after = DateTimeOffset.UtcNow;

            // Act — task is now active (executed but waiting for completion)
            var snapshot = await workflowInstance.GetStateSnapshot();
            var taskSnapshot = snapshot.ActiveActivities.First(a => a.ActivityId == "task");
            var executionStartedAt = taskSnapshot.ExecutionStartedAt;

            // Assert
            Assert.IsNotNull(executionStartedAt);
            Assert.IsTrue(executionStartedAt >= before, "ExecutionStartedAt should be >= test start time");
            Assert.IsTrue(executionStartedAt <= after, "ExecutionStartedAt should be <= test end time");
        }

        [TestMethod]
        public async Task GetExecutionStartedAt_ShouldReturnTimestamp_AfterFailure()
        {
            // Arrange
            var workflow = CreateSimpleWorkflow();
            var workflowInstance = _cluster.GrainFactory.GetGrain<IWorkflowInstance>(Guid.NewGuid());
            await workflowInstance.SetWorkflow(workflow);
            await workflowInstance.StartWorkflow();

            await workflowInstance.FailActivity("task", new Exception("fail"));

            // Act
            var snapshot = await workflowInstance.GetStateSnapshot();
            var failedTask = snapshot.CompletedActivities.First(a => a.ActivityId == "task");
            var executionStartedAt = failedTask.ExecutionStartedAt;

            // Assert
            Assert.IsNotNull(executionStartedAt, "ExecutionStartedAt should be set even after failure");
        }

        [TestMethod]
        public async Task GetCompletedAt_ShouldReturnNull_BeforeCompletion()
        {
            // Arrange
            var activity = _cluster.GrainFactory.GetGrain<IActivityInstance>(Guid.NewGuid());
            await activity.SetActivity("task", "TaskActivity");

            // Act
            var completedAt = await activity.GetCompletedAt();

            // Assert
            Assert.IsNull(completedAt);
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
            var snapshot = await workflowInstance.GetStateSnapshot();
            var taskSnapshot = snapshot.CompletedActivities.First(a => a.ActivityId == "task");
            var completedAt = taskSnapshot.CompletedAt;

            // Assert
            Assert.IsNotNull(completedAt);
            Assert.IsTrue(completedAt >= before, "CompletedAt should be >= test start time");
            Assert.IsTrue(completedAt <= after, "CompletedAt should be <= test end time");
        }

        [TestMethod]
        public async Task GetCompletedAt_ShouldReturnTimestamp_AfterFailure()
        {
            // Arrange
            var workflow = CreateSimpleWorkflow();
            var workflowInstance = _cluster.GrainFactory.GetGrain<IWorkflowInstance>(Guid.NewGuid());
            await workflowInstance.SetWorkflow(workflow);
            await workflowInstance.StartWorkflow();

            await workflowInstance.FailActivity("task", new Exception("fail"));

            // Act
            var snapshot = await workflowInstance.GetStateSnapshot();
            var failedTask = snapshot.CompletedActivities.First(a => a.ActivityId == "task");
            var completedAt = failedTask.CompletedAt;

            // Assert
            Assert.IsNotNull(completedAt, "CompletedAt should be set after failure (Fail calls Complete)");
        }

        [TestMethod]
        public async Task GetSnapshot_ShouldReturnAllFieldsInOneCall()
        {
            // Arrange
            var workflow = CreateSimpleWorkflow();
            var workflowInstance = _cluster.GrainFactory.GetGrain<IWorkflowInstance>(Guid.NewGuid());
            await workflowInstance.SetWorkflow(workflow);
            await workflowInstance.StartWorkflow();

            await workflowInstance.CompleteActivity("task", new ExpandoObject());

            var snapshot = await workflowInstance.GetStateSnapshot();
            var taskSnapshot = snapshot.CompletedActivities.First(a => a.ActivityId == "task");

            // Assert
            Assert.AreEqual("task", taskSnapshot.ActivityId);
            Assert.AreEqual("TaskActivity", taskSnapshot.ActivityType);
            Assert.IsTrue(taskSnapshot.IsCompleted);
            Assert.IsFalse(taskSnapshot.IsExecuting);
            Assert.IsNull(taskSnapshot.ErrorState);
            Assert.IsNotNull(taskSnapshot.CreatedAt);
            Assert.IsNotNull(taskSnapshot.ExecutionStartedAt);
            Assert.IsNotNull(taskSnapshot.CompletedAt);
        }

        [TestMethod]
        public async Task GetSnapshot_ShouldIncludeErrorState_AfterFailure()
        {
            // Arrange
            var workflow = CreateSimpleWorkflow();
            var workflowInstance = _cluster.GrainFactory.GetGrain<IWorkflowInstance>(Guid.NewGuid());
            await workflowInstance.SetWorkflow(workflow);
            await workflowInstance.StartWorkflow();

            await workflowInstance.FailActivity("task", new Exception("snapshot error"));

            var snapshot = await workflowInstance.GetStateSnapshot();
            var taskSnapshot = snapshot.CompletedActivities.First(a => a.ActivityId == "task");

            // Assert
            Assert.AreEqual("task", taskSnapshot.ActivityId);
            Assert.IsTrue(taskSnapshot.IsCompleted);
            Assert.IsNotNull(taskSnapshot.ErrorState);
            Assert.AreEqual(500, taskSnapshot.ErrorState.Code);
            Assert.AreEqual("snapshot error", taskSnapshot.ErrorState.Message);
            Assert.IsNotNull(taskSnapshot.CreatedAt);
            Assert.IsNotNull(taskSnapshot.ExecutionStartedAt);
            Assert.IsNotNull(taskSnapshot.CompletedAt);
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
                hostBuilder
                    .AddMemoryGrainStorage("workflowInstances")
                    .AddMemoryGrainStorage("activityInstances")
                    .ConfigureServices(services => services.AddSerializer(serializerBuilder =>
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
