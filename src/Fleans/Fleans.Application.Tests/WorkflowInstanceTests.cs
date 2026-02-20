using Fleans.Application.Grains;
using Fleans.Application.QueryModels;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Errors;
using Fleans.Domain.Sequences;
using Orleans.Runtime;
using System.Dynamic;

namespace Fleans.Application.Tests
{
    [TestClass]
    public class WorkflowInstanceTests : WorkflowTestBase
    {
        [TestMethod]
        public async Task SetWorkflow_ShouldInitializeWorkflow_AndStartWithStartEvent()
        {
            // Arrange
            var workflow = CreateSimpleWorkflow();
            var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());

            // Act
            await workflowInstance.SetWorkflow(workflow);
            await workflowInstance.StartWorkflow();

            // Assert
            var definition = await workflowInstance.GetWorkflowDefinition();
            Assert.AreEqual(workflow.WorkflowId, definition.WorkflowId);

            var instanceId = workflowInstance.GetPrimaryKey();
            var snapshot = await QueryService.GetStateSnapshot(instanceId);
            Assert.IsNotNull(snapshot);
            Assert.IsTrue(snapshot.IsStarted);
            Assert.HasCount(1, snapshot.CompletedActivities);
            Assert.AreEqual("StartEvent", snapshot.CompletedActivities[0].ActivityType);
        }

        [TestMethod]
        public async Task SetWorkflow_ShouldThrowException_WhenWorkflowAlreadySet()
        {
            // Arrange
            var workflow = CreateSimpleWorkflow();
            var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
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
            var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
            await workflowInstance.SetWorkflow(workflow);

            // Act
            await workflowInstance.StartWorkflow();

            // Assert
            var instanceId = workflowInstance.GetPrimaryKey();
            var snapshot = await QueryService.GetStateSnapshot(instanceId);
            Assert.IsNotNull(snapshot);

            // After start event completes, should transition to task
            Assert.IsTrue(snapshot.ActiveActivities.Count > 0);
            Assert.AreEqual("TaskActivity", snapshot.ActiveActivities[0].ActivityType);
        }

        [TestMethod]
        public async Task CompleteActivity_ShouldMergeVariables_AndContinueExecution()
        {
            // Arrange
            var workflow = CreateSimpleWorkflow();
            var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
            await workflowInstance.SetWorkflow(workflow);
            await workflowInstance.StartWorkflow();

            dynamic variables = new ExpandoObject();
            variables.result = "completed";
            variables.value = 42;

            // Act
            await workflowInstance.CompleteActivity("task", variables);

            // Assert
            var instanceId = workflowInstance.GetPrimaryKey();
            var snapshot = await QueryService.GetStateSnapshot(instanceId);
            Assert.IsNotNull(snapshot);

            // Variables should be merged into state
            Assert.IsTrue(snapshot.VariableStates.Count > 0);
        }

        [TestMethod]
        public async Task CompleteActivity_ShouldThrowException_WhenActivityNotFound()
        {
            // Arrange
            var workflow = CreateSimpleWorkflow();
            var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
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
            var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
            await workflowInstance.SetWorkflow(workflow);
            await workflowInstance.StartWorkflow();

            var exception = new Exception("Test error");

            // Act
            await workflowInstance.FailActivity("task", exception);

            // Assert
            var instanceId = workflowInstance.GetPrimaryKey();
            var snapshot = await QueryService.GetStateSnapshot(instanceId);
            Assert.IsNotNull(snapshot);

            // Activity should be marked as completed (even though it failed)
            Assert.IsTrue(snapshot.CompletedActivities.Count > 0);
        }

        [TestMethod]
        public async Task GetCreatedAt_ShouldReturnNull_BeforeSetWorkflow()
        {
            // Arrange
            var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());

            // Act
            var instanceId = workflowInstance.GetPrimaryKey();
            var snapshot = await QueryService.GetStateSnapshot(instanceId);

            // Assert
            // Before SetWorkflow, snapshot may be null or CreatedAt may be null
            Assert.IsTrue(snapshot == null || snapshot.CreatedAt == null);
        }

        [TestMethod]
        public async Task GetCreatedAt_ShouldReturnTimestamp_AfterSetWorkflow()
        {
            // Arrange
            var workflow = CreateSimpleWorkflow();
            var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());

            var before = DateTimeOffset.UtcNow;
            await workflowInstance.SetWorkflow(workflow);
            var after = DateTimeOffset.UtcNow;

            // Act
            var instanceId = workflowInstance.GetPrimaryKey();
            var snapshot = await QueryService.GetStateSnapshot(instanceId);
            Assert.IsNotNull(snapshot);
            var createdAt = snapshot.CreatedAt;

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
            var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
            await workflowInstance.SetWorkflow(workflow);

            // Act
            var instanceId = workflowInstance.GetPrimaryKey();
            var snapshot = await QueryService.GetStateSnapshot(instanceId);
            Assert.IsNotNull(snapshot);
            var executionStartedAt = snapshot.ExecutionStartedAt;

            // Assert
            Assert.IsNull(executionStartedAt);
        }

        [TestMethod]
        public async Task GetExecutionStartedAt_ShouldReturnTimestamp_AfterStartWorkflow()
        {
            // Arrange
            var workflow = CreateSimpleWorkflow();
            var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
            await workflowInstance.SetWorkflow(workflow);

            var before = DateTimeOffset.UtcNow;
            await workflowInstance.StartWorkflow();
            var after = DateTimeOffset.UtcNow;

            // Act
            var instanceId = workflowInstance.GetPrimaryKey();
            var snapshot = await QueryService.GetStateSnapshot(instanceId);
            Assert.IsNotNull(snapshot);
            var executionStartedAt = snapshot.ExecutionStartedAt;

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
            var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
            await workflowInstance.SetWorkflow(workflow);

            var instanceId = workflowInstance.GetPrimaryKey();
            var snapshotBefore = await QueryService.GetStateSnapshot(instanceId);
            Assert.IsNotNull(snapshotBefore);
            var createdAtBefore = snapshotBefore.CreatedAt;
            Assert.IsNotNull(createdAtBefore);

            // Act
            await workflowInstance.StartWorkflow();

            // Assert
            var snapshotAfter = await QueryService.GetStateSnapshot(instanceId);
            Assert.IsNotNull(snapshotAfter);
            var createdAtAfter = snapshotAfter.CreatedAt;
            Assert.AreEqual(createdAtBefore, createdAtAfter, "CreatedAt should not change after StartWorkflow");
        }

        [TestMethod]
        public async Task GetCompletedAt_ShouldReturnNull_BeforeCompletion()
        {
            // Arrange
            var workflow = CreateSimpleWorkflow();
            var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
            await workflowInstance.SetWorkflow(workflow);
            await workflowInstance.StartWorkflow();

            // Act
            var instanceId = workflowInstance.GetPrimaryKey();
            var snapshot = await QueryService.GetStateSnapshot(instanceId);
            Assert.IsNotNull(snapshot);
            var completedAt = snapshot.CompletedAt;

            // Assert
            Assert.IsNull(completedAt, "CompletedAt should be null while workflow is still running");
        }

        [TestMethod]
        public async Task GetCompletedAt_ShouldReturnTimestamp_AfterCompletion()
        {
            // Arrange
            var workflow = CreateSimpleWorkflow();
            var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
            await workflowInstance.SetWorkflow(workflow);
            await workflowInstance.StartWorkflow();

            var before = DateTimeOffset.UtcNow;
            await workflowInstance.CompleteActivity("task", new ExpandoObject());
            var after = DateTimeOffset.UtcNow;

            // Act
            var instanceId = workflowInstance.GetPrimaryKey();
            var snapshot = await QueryService.GetStateSnapshot(instanceId);
            Assert.IsNotNull(snapshot);
            var completedAt = snapshot.CompletedAt;

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
            var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
            await workflowInstance.SetWorkflow(workflow);
            await workflowInstance.StartWorkflow();
            await workflowInstance.CompleteActivity("task", new ExpandoObject());

            // Act
            var instanceId = workflowInstance.GetPrimaryKey();
            var snapshot = await QueryService.GetStateSnapshot(instanceId);
            Assert.IsNotNull(snapshot);

            // Assert
            Assert.IsTrue(snapshot.IsStarted);
            Assert.IsTrue(snapshot.IsCompleted);
            Assert.IsNotNull(snapshot.CreatedAt);
            Assert.IsNotNull(snapshot.ExecutionStartedAt);
            Assert.IsNotNull(snapshot.CompletedAt);
        }

        [TestMethod]
        public async Task ActivitySnapshot_ShouldHaveExecutionStartedAt_AfterExecution()
        {
            // Arrange
            var workflow = CreateSimpleWorkflow();
            var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
            await workflowInstance.SetWorkflow(workflow);

            var before = DateTimeOffset.UtcNow;
            await workflowInstance.StartWorkflow();
            var after = DateTimeOffset.UtcNow;

            // Act
            var instanceId = workflowInstance.GetPrimaryKey();
            var snapshot = await QueryService.GetStateSnapshot(instanceId);
            Assert.IsNotNull(snapshot);
            var taskSnapshot = snapshot.ActiveActivities.First(a => a.ActivityId == "task");
            var executionStartedAt = taskSnapshot.ExecutionStartedAt;

            // Assert
            Assert.IsNotNull(executionStartedAt);
            Assert.IsTrue(executionStartedAt >= before, "ExecutionStartedAt should be >= test start time");
            Assert.IsTrue(executionStartedAt <= after, "ExecutionStartedAt should be <= test end time");
        }

        [TestMethod]
        public async Task ActivitySnapshot_ShouldHaveExecutionStartedAt_AfterFailure()
        {
            // Arrange
            var workflow = CreateSimpleWorkflow();
            var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
            await workflowInstance.SetWorkflow(workflow);
            await workflowInstance.StartWorkflow();

            await workflowInstance.FailActivity("task", new Exception("fail"));

            // Act
            var instanceId = workflowInstance.GetPrimaryKey();
            var snapshot = await QueryService.GetStateSnapshot(instanceId);
            Assert.IsNotNull(snapshot);
            var failedTask = snapshot.CompletedActivities.First(a => a.ActivityId == "task");

            // Assert
            Assert.IsNotNull(failedTask.ExecutionStartedAt, "ExecutionStartedAt should be set even after failure");
        }

        [TestMethod]
        public async Task ActivitySnapshot_ShouldHaveCompletedAt_AfterCompletion()
        {
            // Arrange
            var workflow = CreateSimpleWorkflow();
            var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
            await workflowInstance.SetWorkflow(workflow);
            await workflowInstance.StartWorkflow();

            var before = DateTimeOffset.UtcNow;
            await workflowInstance.CompleteActivity("task", new ExpandoObject());
            var after = DateTimeOffset.UtcNow;

            // Act
            var instanceId = workflowInstance.GetPrimaryKey();
            var snapshot = await QueryService.GetStateSnapshot(instanceId);
            Assert.IsNotNull(snapshot);
            var taskSnapshot = snapshot.CompletedActivities.First(a => a.ActivityId == "task");
            var completedAt = taskSnapshot.CompletedAt;

            // Assert
            Assert.IsNotNull(completedAt);
            Assert.IsTrue(completedAt >= before, "CompletedAt should be >= test start time");
            Assert.IsTrue(completedAt <= after, "CompletedAt should be <= test end time");
        }

        [TestMethod]
        public async Task ActivitySnapshot_ShouldHaveCompletedAt_AfterFailure()
        {
            // Arrange
            var workflow = CreateSimpleWorkflow();
            var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
            await workflowInstance.SetWorkflow(workflow);
            await workflowInstance.StartWorkflow();

            await workflowInstance.FailActivity("task", new Exception("fail"));

            // Act
            var instanceId = workflowInstance.GetPrimaryKey();
            var snapshot = await QueryService.GetStateSnapshot(instanceId);
            Assert.IsNotNull(snapshot);
            var failedTask = snapshot.CompletedActivities.First(a => a.ActivityId == "task");

            // Assert
            Assert.IsNotNull(failedTask.CompletedAt, "CompletedAt should be set after failure");
        }

        [TestMethod]
        public async Task ActivitySnapshot_ShouldReturnAllFields_AfterCompletion()
        {
            // Arrange
            var workflow = CreateSimpleWorkflow();
            var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
            await workflowInstance.SetWorkflow(workflow);
            await workflowInstance.StartWorkflow();
            await workflowInstance.CompleteActivity("task", new ExpandoObject());

            var instanceId = workflowInstance.GetPrimaryKey();
            var snapshot = await QueryService.GetStateSnapshot(instanceId);
            Assert.IsNotNull(snapshot);
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
        public async Task ActivitySnapshot_ShouldIncludeErrorState_AfterFailure()
        {
            // Arrange
            var workflow = CreateSimpleWorkflow();
            var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
            await workflowInstance.SetWorkflow(workflow);
            await workflowInstance.StartWorkflow();

            await workflowInstance.FailActivity("task", new Exception("snapshot error"));

            var instanceId = workflowInstance.GetPrimaryKey();
            var snapshot = await QueryService.GetStateSnapshot(instanceId);
            Assert.IsNotNull(snapshot);
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

        [TestMethod]
        public async Task ActivitySnapshot_ShouldHaveCustomErrorCode_WhenFailedWithActivityException()
        {
            // Arrange
            var workflow = CreateSimpleWorkflow();
            var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
            await workflowInstance.SetWorkflow(workflow);
            await workflowInstance.StartWorkflow();

            await workflowInstance.FailActivity("task", new BadRequestActivityException("Bad input"));

            // Act
            var instanceId = workflowInstance.GetPrimaryKey();
            var snapshot = await QueryService.GetStateSnapshot(instanceId);
            Assert.IsNotNull(snapshot);
            var taskSnapshot = snapshot.CompletedActivities.First(a => a.ActivityId == "task");

            // Assert
            Assert.IsNotNull(taskSnapshot.ErrorState);
            Assert.AreEqual(400, taskSnapshot.ErrorState.Code);
            Assert.AreEqual("Bad input", taskSnapshot.ErrorState.Message);
        }

        [TestMethod]
        public async Task SetWorkflow_ShouldAcceptTimerStartEvent_AsStartActivity()
        {
            // Arrange
            var timerDef = new TimerDefinition(TimerType.Duration, "PT5M");
            var timerStart = new TimerStartEvent("timerStart1", timerDef);
            var end = new EndEvent("end");

            var workflow = new WorkflowDefinition
            {
                WorkflowId = "timer-start-workflow",
                Activities = [timerStart, end],
                SequenceFlows = [new SequenceFlow("f1", timerStart, end)]
            };

            var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());

            // Act & Assert — should not throw
            await workflowInstance.SetWorkflow(workflow);
            await workflowInstance.StartWorkflow();

            var instanceId = workflowInstance.GetPrimaryKey();
            var snapshot = await QueryService.GetStateSnapshot(instanceId);
            Assert.IsNotNull(snapshot);
            Assert.IsTrue(snapshot.IsCompleted);
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
    }
}
