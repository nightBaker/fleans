using Fleans.Application.QueryModels;
using Fleans.Domain.Activities;
using Fleans.Domain.Errors;
using Fleans.Domain.Sequences;
using Orleans.Runtime;
using System.Dynamic;

namespace Fleans.Domain.Tests
{
    [TestClass]
    public class ErrorHandlingTests : WorkflowTestBase
    {
        [TestMethod]
        public async Task FailActivity_ShouldSetErrorState_OnActivityInstance()
        {
            // Arrange
            var workflow = CreateSimpleWorkflow();
            var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstance>(Guid.NewGuid());
            await workflowInstance.SetWorkflow(workflow);
            await workflowInstance.StartWorkflow();

            var exception = new Exception("Test error message");

            // Act
            await workflowInstance.FailActivity("task", exception);

            // Assert
            var instanceId = workflowInstance.GetPrimaryKey();
            var snapshot = await QueryService.GetStateSnapshot(instanceId);
            Assert.IsNotNull(snapshot);
            var failedSnapshot = snapshot.CompletedActivities.FirstOrDefault(a => a.ActivityId == "task");
            Assert.IsNotNull(failedSnapshot);
            Assert.IsNotNull(failedSnapshot.ErrorState);
            Assert.AreEqual(500, failedSnapshot.ErrorState.Code);
            Assert.AreEqual("Test error message", failedSnapshot.ErrorState.Message);
        }

        [TestMethod]
        public async Task FailActivity_ShouldUseActivityException_WhenProvided()
        {
            // Arrange
            var workflow = CreateSimpleWorkflow();
            var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstance>(Guid.NewGuid());
            await workflowInstance.SetWorkflow(workflow);
            await workflowInstance.StartWorkflow();

            var activityException = new BadRequestActivityException("Custom activity error");

            // Act
            await workflowInstance.FailActivity("task", activityException);

            // Assert
            var instanceId = workflowInstance.GetPrimaryKey();
            var snapshot = await QueryService.GetStateSnapshot(instanceId);
            Assert.IsNotNull(snapshot);
            var failedSnapshot = snapshot.CompletedActivities.FirstOrDefault(a => a.ActivityId == "task");
            Assert.IsNotNull(failedSnapshot);
            Assert.IsNotNull(failedSnapshot.ErrorState);
            Assert.AreEqual(400, failedSnapshot.ErrorState.Code);
            Assert.AreEqual("Custom activity error", failedSnapshot.ErrorState.Message);
        }

        [TestMethod]
        public async Task FailActivity_ShouldMarkActivityAsCompleted_EvenOnFailure()
        {
            // Arrange
            var workflow = CreateSimpleWorkflow();
            var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstance>(Guid.NewGuid());
            await workflowInstance.SetWorkflow(workflow);
            await workflowInstance.StartWorkflow();

            var exception = new Exception("Test error");

            // Act
            await workflowInstance.FailActivity("task", exception);

            // Assert
            var instanceId = workflowInstance.GetPrimaryKey();
            var snapshot = await QueryService.GetStateSnapshot(instanceId);
            Assert.IsNotNull(snapshot);

            // Activity should be moved from active to completed
            Assert.IsTrue(snapshot.CompletedActivities.Count > 0);
        }

        [TestMethod]
        public async Task FailActivity_ShouldThrowException_WhenActivityNotFound()
        {
            // Arrange
            var workflow = CreateSimpleWorkflow();
            var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstance>(Guid.NewGuid());
            await workflowInstance.SetWorkflow(workflow);
            await workflowInstance.StartWorkflow();

            var exception = new Exception("Test error");

            // Act & Assert
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await workflowInstance.FailActivity("non-existent-activity", exception);
            });
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
