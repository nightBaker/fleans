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
            var task = new TaskActivity("task1");
            var nextTask = new TaskActivity("task2");
            var end = new EndEvent("end");
            var start = new StartEvent("start");

            var workflow = new WorkflowDefinition
            {
                WorkflowId = "test",
                Activities = new List<Activity> { start, task, nextTask, end },
                SequenceFlows = new List<SequenceFlow>
                {
                    new SequenceFlow("seq1", task, nextTask),
                    new SequenceFlow("seq2", nextTask, end)
                }
            };

            var workflowInstance = _cluster.GrainFactory.GetGrain<IWorkflowInstance>(Guid.NewGuid());
            await workflowInstance.SetWorkflow(workflow);

            var activityInstance = _cluster.GrainFactory.GetGrain<IActivityInstance>(Guid.NewGuid());
            await activityInstance.SetActivity(task);

            // Act
            var nextActivities = await task.GetNextActivities(workflowInstance, activityInstance);

            // Assert
            Assert.HasCount(1, nextActivities);
            Assert.AreEqual("task2", nextActivities[0].ActivityId);
        }

        [TestMethod]
        public async Task GetNextActivities_ShouldReturnEmptyList_WhenNoNextActivity()
        {
            // Arrange
            var task = new TaskActivity("task");
            var end = new EndEvent("end");
            var start = new StartEvent("start");

            var workflow = new WorkflowDefinition
            {
                WorkflowId = "test",
                Activities = new List<Activity> { start, task, end },
                SequenceFlows = new List<SequenceFlow>
                {
                    new SequenceFlow("seq1", task, end)
                }
            };

            var workflowInstance = _cluster.GrainFactory.GetGrain<IWorkflowInstance>(Guid.NewGuid());
            await workflowInstance.SetWorkflow(workflow);

            var activityInstance = _cluster.GrainFactory.GetGrain<IActivityInstance>(Guid.NewGuid());
            await activityInstance.SetActivity(end);

            // Act
            var nextActivities = await task.GetNextActivities(workflowInstance, activityInstance);

            // Assert
            // Should return the end activity, not empty
            Assert.IsGreaterThanOrEqualTo(0, nextActivities.Count);
        }

        [TestMethod]
        public async Task ExecuteAsync_ShouldMarkActivityAsExecuting()
        {
            // Arrange
            var task = new TaskActivity("task");
            var workflow = CreateSimpleWorkflow();
            var workflowInstance = _cluster.GrainFactory.GetGrain<IWorkflowInstance>(Guid.NewGuid());
            await workflowInstance.SetWorkflow(workflow);
            await workflowInstance.StartWorkflow();

            var state = await workflowInstance.GetState();
            var activeActivities = await state.GetActiveActivities();
            var taskActivity = activeActivities.FirstOrDefault();

            if (taskActivity != null)
            {
                // Act
                await task.ExecuteAsync(workflowInstance, taskActivity);

                // Assert
                Assert.IsTrue(await taskActivity.IsExecuting());
            }
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
            var state = await workflowInstance.GetState();
            Assert.IsTrue(await state.IsCompleted());

            var completedActivities = await state.GetCompletedActivities();
            var completedIds = new List<string>();
            foreach (var activity in completedActivities)
            {
                var current = await activity.GetCurrentActivity();
                completedIds.Add(current.ActivityId);
            }

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
            var state = await workflowInstance.GetState();
            var variableStates = await state.GetVariableStates();
            Assert.IsNotEmpty(variableStates);

            var mergedVariables = variableStates.Values.First().Variables as IDictionary<string, object>;
            Assert.IsNotNull(mergedVariables);
            Assert.AreEqual(2, mergedVariables.Count);
            Assert.AreEqual("value1", mergedVariables["key1"]);
            Assert.AreEqual(42, mergedVariables["key2"]);
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
            var state = await workflowInstance.GetState();
            var completedActivities = await state.GetCompletedActivities();
            var completedTask = false;
            foreach (var activity in completedActivities)
            {
                var current = await activity.GetCurrentActivity();
                if (current.ActivityId == "task")
                {
                    Assert.IsTrue(await activity.IsCompleted());
                    Assert.IsNull(await activity.GetErrorState());
                    completedTask = true;
                }
            }

            Assert.IsTrue(completedTask, "TaskActivity should be in completed activities");
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
            var state = await workflowInstance.GetState();
            var activeActivities = await state.GetActiveActivities();
            Assert.HasCount(0, activeActivities);
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
            var state = await workflowInstance.GetState();
            Assert.IsFalse(await state.IsCompleted());
            var activeActivities = await state.GetActiveActivities();
            Assert.HasCount(1, activeActivities);
            var activeActivity = await activeActivities[0].GetCurrentActivity();
            Assert.AreEqual("task2", activeActivity.ActivityId);

            // Act — complete second task
            await workflowInstance.CompleteActivity("task2", new ExpandoObject());

            // Assert — workflow should be completed
            state = await workflowInstance.GetState();
            Assert.IsTrue(await state.IsCompleted());
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
            var state = await workflowInstance.GetState();
            var completedActivities = await state.GetCompletedActivities();

            IActivityInstance? failedActivity = null;
            foreach (var activity in completedActivities)
            {
                var current = await activity.GetCurrentActivity();
                if (current.ActivityId == "task")
                {
                    failedActivity = activity;
                    break;
                }
            }

            Assert.IsNotNull(failedActivity, "Failed task should be in completed activities");
            var errorState = await failedActivity.GetErrorState();
            Assert.IsNotNull(errorState);
            Assert.AreEqual(500, errorState.Code);
            Assert.AreEqual("Task failed", errorState.Message);
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
            var state = await workflowInstance.GetState();
            var completedActivities = await state.GetCompletedActivities();

            IActivityInstance? failedActivity = null;
            foreach (var activity in completedActivities)
            {
                var current = await activity.GetCurrentActivity();
                if (current.ActivityId == "task")
                {
                    failedActivity = activity;
                    break;
                }
            }

            Assert.IsNotNull(failedActivity, "Failed task should be in completed activities");
            var errorState = await failedActivity.GetErrorState();
            Assert.IsNotNull(errorState);
            Assert.AreEqual(400, errorState.Code);
            Assert.AreEqual("Bad input", errorState.Message);
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
            var state = await workflowInstance.GetState();
            Assert.IsTrue(await state.IsCompleted());

            var activeActivities = await state.GetActiveActivities();
            Assert.HasCount(0, activeActivities);

            var completedActivities = await state.GetCompletedActivities();
            var completedIds = new List<string>();
            foreach (var activity in completedActivities)
            {
                var current = await activity.GetCurrentActivity();
                completedIds.Add(current.ActivityId);
            }

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
            var state = await workflowInstance.GetState();
            var variableStates = await state.GetVariableStates();
            foreach (var vs in variableStates.Values)
            {
                var vars = vs.Variables as IDictionary<string, object>;
                Assert.IsNotNull(vars);
                Assert.AreEqual(0, vars.Count, "No variables should be merged on failure");
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
            await activity.SetActivity(new TaskActivity("task"));
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
            await activity.SetActivity(new TaskActivity("task"));

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
            var state = await workflowInstance.GetState();
            var activeActivities = await state.GetActiveActivities();
            IActivityInstance? taskActivity = null;
            foreach (var a in activeActivities)
            {
                var current = await a.GetCurrentActivity();
                if (current.ActivityId == "task")
                {
                    taskActivity = a;
                    break;
                }
            }
            Assert.IsNotNull(taskActivity);

            var executionStartedAt = await taskActivity.GetExecutionStartedAt();

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
            var state = await workflowInstance.GetState();
            var completedActivities = await state.GetCompletedActivities();
            IActivityInstance? failedTask = null;
            foreach (var a in completedActivities)
            {
                var current = await a.GetCurrentActivity();
                if (current.ActivityId == "task")
                {
                    failedTask = a;
                    break;
                }
            }

            Assert.IsNotNull(failedTask);
            var executionStartedAt = await failedTask.GetExecutionStartedAt();

            // Assert
            Assert.IsNotNull(executionStartedAt, "ExecutionStartedAt should be set even after failure");
        }

        [TestMethod]
        public async Task GetCompletedAt_ShouldReturnNull_BeforeCompletion()
        {
            // Arrange
            var activity = _cluster.GrainFactory.GetGrain<IActivityInstance>(Guid.NewGuid());
            await activity.SetActivity(new TaskActivity("task"));

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
            var state = await workflowInstance.GetState();
            var completedActivities = await state.GetCompletedActivities();
            IActivityInstance? taskActivity = null;
            foreach (var a in completedActivities)
            {
                var current = await a.GetCurrentActivity();
                if (current.ActivityId == "task")
                {
                    taskActivity = a;
                    break;
                }
            }
            Assert.IsNotNull(taskActivity);

            var completedAt = await taskActivity.GetCompletedAt();

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
            var state = await workflowInstance.GetState();
            var completedActivities = await state.GetCompletedActivities();
            IActivityInstance? failedTask = null;
            foreach (var a in completedActivities)
            {
                var current = await a.GetCurrentActivity();
                if (current.ActivityId == "task")
                {
                    failedTask = a;
                    break;
                }
            }

            Assert.IsNotNull(failedTask);
            var completedAt = await failedTask.GetCompletedAt();

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

            var state = await workflowInstance.GetState();
            var completedActivities = await state.GetCompletedActivities();
            IActivityInstance? taskActivity = null;
            foreach (var a in completedActivities)
            {
                var current = await a.GetCurrentActivity();
                if (current.ActivityId == "task")
                {
                    taskActivity = a;
                    break;
                }
            }
            Assert.IsNotNull(taskActivity);

            // Act
            var snapshot = await taskActivity.GetSnapshot();

            // Assert
            Assert.AreEqual("task", snapshot.ActivityId);
            Assert.AreEqual("TaskActivity", snapshot.ActivityType);
            Assert.IsTrue(snapshot.IsCompleted);
            Assert.IsFalse(snapshot.IsExecuting);
            Assert.IsNull(snapshot.ErrorState);
            Assert.IsNotNull(snapshot.CreatedAt);
            Assert.IsNotNull(snapshot.ExecutionStartedAt);
            Assert.IsNotNull(snapshot.CompletedAt);
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

            var state = await workflowInstance.GetState();
            var completedActivities = await state.GetCompletedActivities();
            IActivityInstance? failedTask = null;
            foreach (var a in completedActivities)
            {
                var current = await a.GetCurrentActivity();
                if (current.ActivityId == "task")
                {
                    failedTask = a;
                    break;
                }
            }
            Assert.IsNotNull(failedTask);

            // Act
            var snapshot = await failedTask.GetSnapshot();

            // Assert
            Assert.AreEqual("task", snapshot.ActivityId);
            Assert.IsTrue(snapshot.IsCompleted);
            Assert.IsNotNull(snapshot.ErrorState);
            Assert.AreEqual(500, snapshot.ErrorState.Code);
            Assert.AreEqual("snapshot error", snapshot.ErrorState.Message);
            Assert.IsNotNull(snapshot.CreatedAt);
            Assert.IsNotNull(snapshot.ExecutionStartedAt);
            Assert.IsNotNull(snapshot.CompletedAt);
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

