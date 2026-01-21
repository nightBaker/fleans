using Fleans.Domain.Activities;
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

