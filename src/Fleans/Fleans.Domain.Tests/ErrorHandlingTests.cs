using System.Collections.Frozen;
using Fleans.Domain.Activities;
using Fleans.Domain.Errors;
using Fleans.Domain.Sequences;
using Orleans.Serialization;
using Orleans.TestingHost;
using System.Dynamic;
using Microsoft.CodeAnalysis.Operations;
using NSubstitute;

namespace Fleans.Domain.Tests
{
    [TestClass]
    public class ErrorHandlingTests
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
        public async Task FailActivity_ShouldSetErrorState_OnActivityInstance()
        {
            // Arrange
            var workflow = CreateSimpleWorkflow();
            var workflowInstance = _cluster.GrainFactory.GetGrain<IWorkflowInstance>(Guid.NewGuid());
            await workflowInstance.SetWorkflow(workflow);
            await workflowInstance.StartWorkflow();

            var exception = new Exception("Test error message");

            // Act
            await workflowInstance.FailActivity("task", exception);

            // Assert
            var snapshot = await workflowInstance.GetStateSnapshot();
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
            var workflowInstance = _cluster.GrainFactory.GetGrain<IWorkflowInstance>(Guid.NewGuid());
            await workflowInstance.SetWorkflow(workflow);
            await workflowInstance.StartWorkflow();

            var activityException = new BadRequestActivityException("Custom activity error");

            // Act
            await workflowInstance.FailActivity("task", activityException);

            // Assert
            var snapshot = await workflowInstance.GetStateSnapshot();
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
            var workflowInstance = _cluster.GrainFactory.GetGrain<IWorkflowInstance>(Guid.NewGuid());
            await workflowInstance.SetWorkflow(workflow);
            await workflowInstance.StartWorkflow();

            var exception = new Exception("Test error");

            // Act
            await workflowInstance.FailActivity("task", exception);

            // Assert
            var snapshot = await workflowInstance.GetStateSnapshot();

            // Activity should be moved from active to completed
            Assert.IsTrue(snapshot.CompletedActivities.Count > 0);
        }

        [TestMethod]
        public async Task FailActivity_ShouldThrowException_WhenActivityNotFound()
        {
            // Arrange
            var workflow = CreateSimpleWorkflow();
            var workflowInstance = _cluster.GrainFactory.GetGrain<IWorkflowInstance>(Guid.NewGuid());
            await workflowInstance.SetWorkflow(workflow);
            await workflowInstance.StartWorkflow();

            var exception = new Exception("Test error");

            // Act & Assert
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await workflowInstance.FailActivity("non-existent-activity", exception);
            });
        }

        [TestMethod]
        public async Task ActivityInstance_Fail_ShouldSetErrorState_AndComplete()
        {
            // Arrange
            var activityInstance = _cluster.GrainFactory.GetGrain<IActivityInstance>(Guid.NewGuid());
            await activityInstance.SetActivity("task", "TaskActivity");
            await activityInstance.Execute();

            var exception = new Exception("Test error");

            // Act
            await activityInstance.Fail(exception);

            // Assert
            Assert.IsTrue(await activityInstance.IsCompleted());
            Assert.IsFalse(await activityInstance.IsExecuting());

            var errorState = await activityInstance.GetErrorState();
            Assert.IsNotNull(errorState);
            Assert.AreEqual(500, errorState.Code);
        }

        [TestMethod]
        public async Task ActivityInstance_Execute_ShouldClearErrorState()
        {
            // Arrange
            var activityInstance = _cluster.GrainFactory.GetGrain<IActivityInstance>(Guid.NewGuid());
            await activityInstance.SetActivity("task", "TaskActivity");
            await activityInstance.Fail(new Exception("Previous error"));

            // Act
            await activityInstance.Execute();

            // Assert
            var errorState = await activityInstance.GetErrorState();
            Assert.IsNull(errorState);
            Assert.IsTrue(await activityInstance.IsExecuting());
            Assert.IsFalse(await activityInstance.IsCompleted());
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
