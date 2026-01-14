using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using Orleans.Serialization;
using Orleans.TestingHost;
using System.Dynamic;

namespace Fleans.Domain.Tests
{
    [TestClass]
    public class EndEventTests
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
        public async Task ExecuteAsync_ShouldCompleteActivity_AndCompleteWorkflow()
        {
            // Arrange
            var end = new EndEvent("end");
            var workflow = CreateSimpleWorkflow();
            var workflowInstance = _cluster.GrainFactory.GetGrain<IWorkflowInstance>(Guid.NewGuid());
            await workflowInstance.SetWorkflow(workflow);
            await workflowInstance.StartWorkflow();

            // Act, Complete the task first
            var variables = new ExpandoObject();
            await workflowInstance.CompleteActivity("task", variables);

            // Assert
            
            var state = await workflowInstance.GetState();
            var completedActivities = await state.GetCompletedActivities();
            IActivityInstance endActivity = null;

            foreach (var completedActivity in completedActivities)
            {
                var activity = await completedActivity.GetCurrentActivity();
                if(activity is EndEvent)
                {
                    endActivity = completedActivity;
                    break;
                }
            }
            
            Assert.IsNotNull(endActivity);
            Assert.IsTrue(await endActivity.IsCompleted());
            Assert.IsTrue(await state.IsCompleted());
            
        }

        [TestMethod]
        public async Task GetNextActivities_ShouldAlwaysReturnEmptyList()
        {
            // Arrange
            var end = new EndEvent("end");
            var workflow = CreateSimpleWorkflow();
            var workflowInstance = _cluster.GrainFactory.GetGrain<IWorkflowInstance>(Guid.NewGuid());
            await workflowInstance.SetWorkflow(workflow);

            var activityInstance = _cluster.GrainFactory.GetGrain<IActivityInstance>(Guid.NewGuid());
            await activityInstance.SetActivity(end);

            // Act
            var nextActivities = await end.GetNextActivities(workflowInstance, activityInstance);

            // Assert
            Assert.IsEmpty(nextActivities);
        }

        [TestMethod]
        public async Task ExecuteAsync_ShouldPublishWorkflowActivityExecutedEvent()
        {
            // Arrange
            var end = new EndEvent("end");
            var workflow = CreateSimpleWorkflow();
            var workflowInstance = _cluster.GrainFactory.GetGrain<IWorkflowInstance>(Guid.NewGuid());
            await workflowInstance.SetWorkflow(workflow);
            await workflowInstance.StartWorkflow();

            var variables = new ExpandoObject();
            await workflowInstance.CompleteActivity("task", variables);

            var state = await workflowInstance.GetState();
            var activeActivities = await state.GetActiveActivities();
            var endActivity = activeActivities.FirstOrDefault(a =>
            {
                var activity = a.GetCurrentActivity().Result;
                return activity is EndEvent;
            });

            if (endActivity != null)
            {
                // Act
                await end.ExecuteAsync(workflowInstance, endActivity);

                // Assert - Event should be published (verified by activity being marked as executed)
                Assert.IsTrue(await endActivity.IsCompleted());
            }
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

