using Fleans.Application.QueryModels;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using Orleans.Runtime;
using System.Dynamic;

namespace Fleans.Domain.Tests
{
    [TestClass]
    public class ParallelGatewayTests : WorkflowTestBase
    {
        [TestMethod]
        public async Task ForkGateway_ShouldCreateMultipleParallelPaths()
        {
            // Arrange
            var workflow = CreateForkJoinWorkflow();
            var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstance>(Guid.NewGuid());
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
        public async Task JoinGateway_ShouldWaitForAllIncomingPaths_ToComplete()
        {
            // Arrange
            var workflow = CreateForkJoinWorkflow();
            var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstance>(Guid.NewGuid());
            await workflowInstance.SetWorkflow(workflow);
            await workflowInstance.StartWorkflow();

            // This test requires completing the fork and then the parallel tasks
            // The join should only complete when all incoming paths are done

            // Act & Assert
            // Implementation depends on completing activities in sequence
            // This is a placeholder for the full test
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

            var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstance>(Guid.NewGuid());
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
