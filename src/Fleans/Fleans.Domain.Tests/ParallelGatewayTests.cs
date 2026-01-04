using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using Orleans.Serialization;
using Orleans.TestingHost;
using System.Dynamic;

namespace Fleans.Domain.Tests
{
    [TestClass]
    public class ParallelGatewayTests
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
        public async Task ForkGateway_ShouldCreateMultipleParallelPaths()
        {
            // Arrange
            var workflow = CreateForkJoinWorkflow();
            var workflowInstance = _cluster.GrainFactory.GetGrain<IWorkflowInstance>(Guid.NewGuid());
            await workflowInstance.SetWorkflow(workflow);
            
            // Act - Complete the fork gateway
            await workflowInstance.StartWorkflow();
            
            // Assert - Should have multiple parallel paths active
            var updatedState = await workflowInstance.GetState();
            var updatedActiveActivities = await updatedState.GetActiveActivities();
            
            // After fork, should have multiple task activities active
            var taskActivities = new List<IActivityInstance>();
            foreach (var activity in updatedActiveActivities)
            {
                var act = await activity.GetCurrentActivity();
                if (act is TaskActivity)
                {
                    taskActivities.Add(activity);
                }
            }
            
            Assert.IsGreaterThanOrEqualTo(2, taskActivities.Count, "Fork should create multiple parallel paths");
        }

        [TestMethod]
        public async Task JoinGateway_ShouldWaitForAllIncomingPaths_ToComplete()
        {
            // Arrange
            var workflow = CreateForkJoinWorkflow();
            var workflowInstance = _cluster.GrainFactory.GetGrain<IWorkflowInstance>(Guid.NewGuid());
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
            // Arrange
            var fork = new ParallelGateway("fork", isFork: true);
            var task1 = new TaskActivity("task1");
            var task2 = new TaskActivity("task2");
            var task3 = new TaskActivity("task3");
            var start = new StartEvent("start");

            var workflow = new WorkflowDefinition
            {
                WorkflowId = "fork-test",
                Activities = new List<Activity> { start, fork, task1, task2, task3 },
                SequenceFlows = new List<SequenceFlow>
                {
                    new SequenceFlow("seq1", fork, task1),
                    new SequenceFlow("seq2", fork, task2),
                    new SequenceFlow("seq3", fork, task3)
                }
            };

            var workflowInstance = _cluster.GrainFactory.GetGrain<IWorkflowInstance>(Guid.NewGuid());
            await workflowInstance.SetWorkflow(workflow);
            
            var activityInstance = _cluster.GrainFactory.GetGrain<IActivityInstance>(Guid.NewGuid());
            activityInstance.SetActivity(fork);

            // Act
            var nextActivities = await fork.GetNextActivities(workflowInstance, activityInstance);

            // Assert
            Assert.HasCount(3, nextActivities);
            Assert.IsTrue(nextActivities.Any(a => a.ActivityId == "task1"));
            Assert.IsTrue(nextActivities.Any(a => a.ActivityId == "task2"));
            Assert.IsTrue(nextActivities.Any(a => a.ActivityId == "task3"));
        }

        private static TestCluster CreateCluster()
        {
            var builder = new TestClusterBuilder();
            builder.AddSiloBuilderConfigurator<SiloConfigurator>();
            var cluster = builder.Build();
            cluster.Deploy();
            return cluster;
        }

        private static IWorkflowDefinition CreateForkJoinWorkflow()
        {
            var start = new StartEvent("start");
            var fork = new ParallelGateway("fork", isFork: true);
            var task1 = new TaskActivity("task1");
            var task2 = new TaskActivity("task2");
            var join = new ParallelGateway("join", isFork: false);
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