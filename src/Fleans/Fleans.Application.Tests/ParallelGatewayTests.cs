using Fleans.Application.Grains;
using Fleans.Application.QueryModels;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using Orleans.Runtime;
using System.Dynamic;

namespace Fleans.Application.Tests
{
    // TODO: Add tests for: 3+ parallel branches joining, failed branch reaching join,
    // variable merging at join point
    [TestClass]
    public class ParallelGatewayTests : WorkflowTestBase
    {
        [TestMethod]
        public async Task ForkGateway_ShouldCreateMultipleParallelPaths()
        {
            // Arrange
            var workflow = CreateForkJoinWorkflow();
            var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
            await workflowInstance.SetWorkflow(workflow);

            // Act - Complete the fork gateway
            await workflowInstance.StartWorkflow();

            // Assert - Should have multiple parallel paths active
            var instanceId = workflowInstance.GetPrimaryKey();
            var snapshot = await QueryService.GetStateSnapshot(instanceId);
            Assert.IsNotNull(snapshot);

            // After fork, should have multiple task activities active
            var taskActivities = snapshot.ActiveActivities.Where(a => a.ActivityType == "TaskActivity").ToList();

            Assert.IsGreaterThanOrEqualTo(2, taskActivities.Count, "Fork should create multiple parallel paths");
        }

        [TestMethod]
        public async Task JoinGateway_ShouldNotComplete_WhenOnlyOnePathDone()
        {
            // Arrange
            var workflow = CreateForkJoinWorkflow();
            var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
            await workflowInstance.SetWorkflow(workflow);
            await workflowInstance.StartWorkflow();

            // Act — complete only task1
            await workflowInstance.CompleteActivity("task1", new ExpandoObject());

            // Assert — join should NOT have completed, task2 is still active
            var instanceId = workflowInstance.GetPrimaryKey();
            var snapshot = await QueryService.GetStateSnapshot(instanceId);
            Assert.IsNotNull(snapshot);
            Assert.IsFalse(snapshot.IsCompleted, "Workflow should NOT be completed — task2 still pending");
            Assert.IsTrue(snapshot.ActiveActivities.Any(a => a.ActivityId == "task2"),
                "task2 should still be active");
            Assert.IsFalse(snapshot.CompletedActivities.Any(a => a.ActivityId == "end"),
                "End event should NOT have been reached");
        }

        [TestMethod]
        public async Task JoinGateway_ShouldComplete_WhenAllPathsDone()
        {
            // Arrange
            var workflow = CreateForkJoinWorkflow();
            var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
            await workflowInstance.SetWorkflow(workflow);
            await workflowInstance.StartWorkflow();

            // Act — complete both tasks
            await workflowInstance.CompleteActivity("task1", new ExpandoObject());
            await workflowInstance.CompleteActivity("task2", new ExpandoObject());

            // Assert — join should have completed, workflow finished
            var instanceId = workflowInstance.GetPrimaryKey();
            var snapshot = await QueryService.GetStateSnapshot(instanceId);
            Assert.IsNotNull(snapshot);
            Assert.IsTrue(snapshot.IsCompleted, "Workflow should be completed after both paths done");
            Assert.IsTrue(snapshot.CompletedActivities.Any(a => a.ActivityId == "end"),
                "End event should have been reached");
        }

        [TestMethod]
        public async Task GetNextActivities_ShouldReturnAllOutgoingFlows_ForForkGateway()
        {
            // Arrange — workflow: start -> fork -> task1/task2/task3
            var start = new StartEvent("start");
            var fork = new ParallelGateway("fork", IsFork: true);
            var task1 = new TaskActivity("task1");
            var task2 = new TaskActivity("task2");
            var task3 = new TaskActivity("task3");

            var workflow = new WorkflowDefinition
            {
                WorkflowId = "fork-test",
                Activities = new List<Activity> { start, fork, task1, task2, task3 },
                SequenceFlows = new List<SequenceFlow>
                {
                    new SequenceFlow("seq0", start, fork),
                    new SequenceFlow("seq1", fork, task1),
                    new SequenceFlow("seq2", fork, task2),
                    new SequenceFlow("seq3", fork, task3)
                }
            };

            var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
            await workflowInstance.SetWorkflow(workflow);

            // Act — start the workflow; start event auto-transitions to fork, fork fans out
            await workflowInstance.StartWorkflow();

            // Assert — all 3 tasks should be active after the fork
            var instanceId = workflowInstance.GetPrimaryKey();
            var snapshot = await QueryService.GetStateSnapshot(instanceId);
            Assert.IsNotNull(snapshot);
            var activeTaskActivities = snapshot.ActiveActivities.Where(a => a.ActivityType == "TaskActivity").ToList();

            Assert.HasCount(3, activeTaskActivities);
            Assert.IsTrue(activeTaskActivities.Any(a => a.ActivityId == "task1"));
            Assert.IsTrue(activeTaskActivities.Any(a => a.ActivityId == "task2"));
            Assert.IsTrue(activeTaskActivities.Any(a => a.ActivityId == "task3"));
        }

        private static IWorkflowDefinition CreateForkJoinWorkflow()
        {
            var start = new StartEvent("start");
            var fork = new ParallelGateway("fork", IsFork: true);
            var task1 = new TaskActivity("task1");
            var task2 = new TaskActivity("task2");
            var join = new ParallelGateway("join", IsFork: false);
            var end = new EndEvent("end");

            return new WorkflowDefinition
            {
                WorkflowId = "fork-join-workflow",
                Activities = new List<Activity> { start, fork, task1, task2, join, end },
                SequenceFlows = new List<SequenceFlow>
                {
                    new SequenceFlow("seq1", start, fork),
                    new SequenceFlow("seq2", fork, task1),
                    new SequenceFlow("seq3", fork, task2),
                    new SequenceFlow("seq4", task1, join),
                    new SequenceFlow("seq5", task2, join),
                    new SequenceFlow("seq6", join, end)
                }
            };
        }
    }
}
