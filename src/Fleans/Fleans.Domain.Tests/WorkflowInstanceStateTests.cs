using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using Fleans.Domain.States;
using Orleans.Serialization;
using Orleans.TestingHost;
using System.Dynamic;

namespace Fleans.Domain.Tests
{
    [TestClass]
    public class WorkflowInstanceStateTests
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
        public async Task StartWith_ShouldAddStartActivity_ToActiveActivities()
        {
            // Arrange
            var stateId = Guid.NewGuid();
            var state = _cluster.GrainFactory.GetGrain<IWorkflowInstanceState>(stateId);
            var startActivity = new StartEvent("start");

            // Act
            await state.StartWith(startActivity);

            // Assert
            var activeActivities = await state.GetActiveActivities();
            Assert.HasCount(1, activeActivities);
            
            var activity = await activeActivities[0].GetCurrentActivity();
            Assert.AreEqual("start", activity.ActivityId);
        }

        [TestMethod]
        public async Task StartWith_ShouldCreateInitialVariableState()
        {
            // Arrange
            var stateId = Guid.NewGuid();
            var state = _cluster.GrainFactory.GetGrain<IWorkflowInstanceState>(stateId);
            var startActivity = new StartEvent("start");

            // Act
            await state.StartWith(startActivity);

            // Assert
            var variableStates = await state.GetVariableStates();
            Assert.HasCount(1, variableStates);
        }

        [TestMethod]
        public async Task Start_ShouldMarkWorkflowAsStarted()
        {
            // Arrange
            var stateId = Guid.NewGuid();
            var state = _cluster.GrainFactory.GetGrain<IWorkflowInstanceState>(stateId);

            // Act
            await state.Start();

            // Assert
            Assert.IsTrue(await state.IsStarted());
        }

        [TestMethod]
        public async Task Start_ShouldThrowException_WhenAlreadyStarted()
        {
            // Arrange
            var stateId = Guid.NewGuid();
            var state = _cluster.GrainFactory.GetGrain<IWorkflowInstanceState>(stateId);
            await state.Start();

            // Act & Assert
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await state.Start();
            });
        }

        [TestMethod]
        public async Task Complete_ShouldMarkWorkflowAsCompleted()
        {
            // Arrange
            var stateId = Guid.NewGuid();
            var state = _cluster.GrainFactory.GetGrain<IWorkflowInstanceState>(stateId);
            var startActivity = new StartEvent("start");
            await state.StartWith(startActivity);

            // Act
            await state.Complete();

            // Assert
            Assert.IsTrue(await state.IsCompleted());
        }

        [TestMethod]
        public async Task AddActiveActivities_ShouldAddActivities_ToActiveList()
        {
            // Arrange
            var stateId = Guid.NewGuid();
            var state = _cluster.GrainFactory.GetGrain<IWorkflowInstanceState>(stateId);
            var activity1 = _cluster.GrainFactory.GetGrain<IActivityInstance>(Guid.NewGuid());
            var activity2 = _cluster.GrainFactory.GetGrain<IActivityInstance>(Guid.NewGuid());
            activity1.SetActivity(new TaskActivity("task1"));
            activity2.SetActivity(new TaskActivity("task2"));

            // Act
            await state.AddActiveActivities(new[] { activity1, activity2 });

            // Assert
            var activeActivities = await state.GetActiveActivities();
            Assert.HasCount(2, activeActivities);
        }

        [TestMethod]
        public async Task RemoveActiveActivities_ShouldRemoveActivities_FromActiveList()
        {
            // Arrange
            var stateId = Guid.NewGuid();
            var state = _cluster.GrainFactory.GetGrain<IWorkflowInstanceState>(stateId);
            var activity1 = _cluster.GrainFactory.GetGrain<IActivityInstance>(Guid.NewGuid());
            var activity2 = _cluster.GrainFactory.GetGrain<IActivityInstance>(Guid.NewGuid());
            activity1.SetActivity(new TaskActivity("task1"));
            activity2.SetActivity(new TaskActivity("task2"));
            
            await state.AddActiveActivities(new[] { activity1, activity2 });

            // Act
            await state.RemoveActiveActivities(new List<IActivityInstance> { activity1 });

            // Assert
            var activeActivities = await state.GetActiveActivities();
            Assert.HasCount(1, activeActivities);
            Assert.AreEqual(activity2, activeActivities[0]);
        }

        [TestMethod]
        public async Task AddCompletedActivities_ShouldAddActivities_ToCompletedList()
        {
            // Arrange
            var stateId = Guid.NewGuid();
            var state = _cluster.GrainFactory.GetGrain<IWorkflowInstanceState>(stateId);
            var activity = _cluster.GrainFactory.GetGrain<IActivityInstance>(Guid.NewGuid());
            activity.SetActivity(new TaskActivity("task"));
            activity.Complete();

            // Act
            await state.AddCompletedActivities(new[] { activity });

            // Assert
            var completedActivities = await state.GetCompletedActivities();
            Assert.HasCount(1, completedActivities);
        }

        [TestMethod]
        public async Task GetFirstActive_ShouldReturnActivity_WhenFound()
        {
            // Arrange
            var stateId = Guid.NewGuid();
            var state = _cluster.GrainFactory.GetGrain<IWorkflowInstanceState>(stateId);
            var activity = _cluster.GrainFactory.GetGrain<IActivityInstance>(Guid.NewGuid());
            activity.SetActivity(new TaskActivity("task1"));
            await state.AddActiveActivities(new[] { activity });

            // Act
            var result = await state.GetFirstActive("task1");

            // Assert
            Assert.IsNotNull(result);
            var foundActivity = await result.GetCurrentActivity();
            Assert.AreEqual("task1", foundActivity.ActivityId);
        }

        [TestMethod]
        public async Task GetFirstActive_ShouldReturnNull_WhenNotFound()
        {
            // Arrange
            var stateId = Guid.NewGuid();
            var state = _cluster.GrainFactory.GetGrain<IWorkflowInstanceState>(stateId);

            // Act
            var result = await state.GetFirstActive("non-existent");

            // Assert
            Assert.IsNull(result);
        }

        [TestMethod]
        public async Task AddCloneOfVariableState_ShouldCreateNewVariableState_WithClonedData()
        {
            // Arrange
            var stateId = Guid.NewGuid();
            var state = _cluster.GrainFactory.GetGrain<IWorkflowInstanceState>(stateId);
            var startActivity = new StartEvent("start");
            await state.StartWith(startActivity);
            
            var variableStates = await state.GetVariableStates();
            var originalId = variableStates.Keys.First();

            // Act
            var clonedId = await state.AddCloneOfVariableState(originalId);

            // Assert
            Assert.AreNotEqual(originalId, clonedId);
            var newVariableStates = await state.GetVariableStates();
            Assert.IsTrue(newVariableStates.ContainsKey(clonedId));
        }

        [TestMethod]
        public async Task MergeState_ShouldMergeVariables_IntoExistingState()
        {
            // Arrange
            var stateId = Guid.NewGuid();
            var state = _cluster.GrainFactory.GetGrain<IWorkflowInstanceState>(stateId);
            var startActivity = new StartEvent("start");
            await state.StartWith(startActivity);
            
            var variableStates = await state.GetVariableStates();
            var variablesId = variableStates.Keys.First();

            dynamic newVariables = new ExpandoObject();
            ((IDictionary<string, object>)newVariables)["key1"] = "value1";
            ((IDictionary<string, object>)newVariables)["key2"] = 42;

            // Act
            await state.MergeState(variablesId, newVariables);

            // Assert
            var updatedStates = await state.GetVariableStates();
            var mergedState = updatedStates[variablesId];
            Assert.IsNotNull(mergedState);
        }

        private static TestCluster CreateCluster()
        {
            var builder = new TestClusterBuilder();
            builder.AddSiloBuilderConfigurator<SiloConfigurator>();
            var cluster = builder.Build();
            cluster.Deploy();
            return cluster;
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

