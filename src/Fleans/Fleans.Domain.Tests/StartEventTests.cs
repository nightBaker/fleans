using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using Orleans.Serialization;
using Orleans.TestingHost;
using System.Dynamic;

namespace Fleans.Domain.Tests
{
    [TestClass]
    public class StartEventTests
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
        public async Task ExecuteAsync_ShouldCompleteActivity_AndStartWorkflow()
        {
            // Arrange
            var start = new StartEvent("start");
            var workflow = CreateSimpleWorkflow();
            var workflowInstance = _cluster.GrainFactory.GetGrain<IWorkflowInstance>(Guid.NewGuid());
            await workflowInstance.SetWorkflow(workflow);

            var state = await workflowInstance.GetState();
            var activeActivities = await state.GetActiveActivities();
            var startActivity = activeActivities.First();

            // Act
            await workflowInstance.StartWorkflow();

            // Assert
            Assert.IsTrue(await startActivity.IsCompleted());
            Assert.IsTrue(await state.IsStarted());
        }

        [TestMethod]
        public async Task GetNextActivities_ShouldReturnFirstActivity_AfterStart()
        {
            // Arrange
            var start = new StartEvent("start");
            var task = new TaskActivity("task");
            var end = new EndEvent("end");

            var workflow = new WorkflowDefinition
            {
                WorkflowId = "test",
                Activities = new List<Activity> { start, task, end },
                SequenceFlows = new List<SequenceFlow>
                {
                    new SequenceFlow("seq1", start, task),
                    new SequenceFlow("seq2", task, end)
                }
            };

            var workflowInstance = _cluster.GrainFactory.GetGrain<IWorkflowInstance>(Guid.NewGuid());
            await workflowInstance.SetWorkflow(workflow);

            var activityInstance = _cluster.GrainFactory.GetGrain<IActivityInstance>(Guid.NewGuid());
            activityInstance.SetActivity(start);

            // Act
            var nextActivities = await start.GetNextActivities(workflowInstance, activityInstance);

            // Assert
            Assert.HasCount(1, nextActivities);
            Assert.AreEqual("task", nextActivities[0].ActivityId);
        }

        [TestMethod]
        public async Task GetNextActivities_ShouldReturnEmptyList_WhenNoSequenceFlow()
        {
            // Arrange
            var start = new StartEvent("start");

            var workflow = new WorkflowDefinition
            {
                WorkflowId = "test",
                Activities = new List<Activity> { start },
                SequenceFlows = new List<SequenceFlow>()
            };

            var workflowInstance = _cluster.GrainFactory.GetGrain<IWorkflowInstance>(Guid.NewGuid());
            await workflowInstance.SetWorkflow(workflow);

            var activityInstance = _cluster.GrainFactory.GetGrain<IActivityInstance>(Guid.NewGuid());
            activityInstance.SetActivity(start);

            // Act
            var nextActivities = await start.GetNextActivities(workflowInstance, activityInstance);

            // Assert
            Assert.IsEmpty(nextActivities);
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

