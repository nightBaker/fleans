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
            await activity1.SetActivity(new TaskActivity("task1"));
            await activity2.SetActivity(new TaskActivity("task2"));

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
            await activity1.SetActivity(new TaskActivity("task1"));
            await activity2.SetActivity(new TaskActivity("task2"));
            
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
            await activity.SetActivity(new TaskActivity("task"));
            await activity.Complete();

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
            await activity.SetActivity(new TaskActivity("task1"));
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

        [TestMethod]
        public async Task GetStateSnapshot_ShouldReturnActiveAndCompletedActivitySnapshots()
        {
            // Arrange
            var state = _cluster.GrainFactory.GetGrain<IWorkflowInstanceState>(Guid.NewGuid());

            var activeActivity = _cluster.GrainFactory.GetGrain<IActivityInstance>(Guid.NewGuid());
            await activeActivity.SetActivity(new TaskActivity("task1"));
            await activeActivity.SetVariablesId(Guid.NewGuid());

            var completedActivity = _cluster.GrainFactory.GetGrain<IActivityInstance>(Guid.NewGuid());
            await completedActivity.SetActivity(new TaskActivity("task2"));
            await completedActivity.SetVariablesId(Guid.NewGuid());
            await completedActivity.Complete();

            await state.AddActiveActivities(new[] { activeActivity });
            await state.AddCompletedActivities(new[] { completedActivity });
            await state.Start();

            // Act
            var snapshot = await state.GetStateSnapshot();

            // Assert
            Assert.HasCount(1, snapshot.ActiveActivities);
            Assert.HasCount(1, snapshot.CompletedActivities);
            Assert.HasCount(1, snapshot.ActiveActivityIds);
            Assert.HasCount(1, snapshot.CompletedActivityIds);

            Assert.AreEqual("task1", snapshot.ActiveActivities[0].ActivityId);
            Assert.AreEqual("TaskActivity", snapshot.ActiveActivities[0].ActivityType);
            Assert.IsFalse(snapshot.ActiveActivities[0].IsCompleted);

            Assert.AreEqual("task2", snapshot.CompletedActivities[0].ActivityId);
            Assert.IsTrue(snapshot.CompletedActivities[0].IsCompleted);
            Assert.IsNotNull(snapshot.CompletedActivities[0].CompletedAt);

            Assert.AreEqual("task1", snapshot.ActiveActivityIds[0]);
            Assert.AreEqual("task2", snapshot.CompletedActivityIds[0]);

            Assert.IsTrue(snapshot.IsStarted);
            Assert.IsFalse(snapshot.IsCompleted);
        }

        [TestMethod]
        public async Task GetStateSnapshot_ShouldSerializeVariablesToStringDictionary()
        {
            // Arrange
            var state = _cluster.GrainFactory.GetGrain<IWorkflowInstanceState>(Guid.NewGuid());
            await state.StartWith(new StartEvent("start"));

            var variableStates = await state.GetVariableStates();
            var variablesId = variableStates.Keys.First();

            dynamic vars = new ExpandoObject();
            ((IDictionary<string, object>)vars)["name"] = "test";
            ((IDictionary<string, object>)vars)["count"] = 42;
            await state.MergeState(variablesId, vars);

            // Act
            var snapshot = await state.GetStateSnapshot();

            // Assert
            Assert.HasCount(1, snapshot.VariableStates);
            var vsSnapshot = snapshot.VariableStates[0];
            Assert.AreEqual(variablesId, vsSnapshot.VariablesId);
            Assert.AreEqual(2, vsSnapshot.Variables.Count);
            Assert.AreEqual("test", vsSnapshot.Variables["name"]);
            Assert.AreEqual("42", vsSnapshot.Variables["count"]);
        }

        [TestMethod]
        public async Task GetStateSnapshot_ShouldReturnConditionSequences()
        {
            // Arrange
            var state = _cluster.GrainFactory.GetGrain<IWorkflowInstanceState>(Guid.NewGuid());
            var source = new TaskActivity("task1");
            var targetTrue = new TaskActivity("task2");
            var targetFalse = new TaskActivity("task3");

            var activityInstanceId = Guid.NewGuid();
            var sequences = new[]
            {
                new ConditionalSequenceFlow("seq1", source, targetTrue, "x > 0"),
                new ConditionalSequenceFlow("seq2", source, targetFalse, "x <= 0")
            };

            await state.AddConditionSequenceStates(activityInstanceId, sequences);
            await state.SetCondigitionSequencesResult(activityInstanceId, "seq1", true);
            await state.SetCondigitionSequencesResult(activityInstanceId, "seq2", false);

            // Act
            var snapshot = await state.GetStateSnapshot();

            // Assert
            Assert.HasCount(2, snapshot.ConditionSequences);

            var seq1 = snapshot.ConditionSequences.First(c => c.SequenceFlowId == "seq1");
            Assert.AreEqual("x > 0", seq1.Condition);
            Assert.AreEqual("task1", seq1.SourceActivityId);
            Assert.AreEqual("task2", seq1.TargetActivityId);
            Assert.IsTrue(seq1.Result);

            var seq2 = snapshot.ConditionSequences.First(c => c.SequenceFlowId == "seq2");
            Assert.AreEqual("x <= 0", seq2.Condition);
            Assert.AreEqual("task1", seq2.SourceActivityId);
            Assert.AreEqual("task3", seq2.TargetActivityId);
            Assert.IsFalse(seq2.Result);
        }

        [TestMethod]
        public async Task GetStateSnapshot_WithEmptyState_ShouldReturnEmptyLists()
        {
            // Arrange
            var state = _cluster.GrainFactory.GetGrain<IWorkflowInstanceState>(Guid.NewGuid());

            // Act
            var snapshot = await state.GetStateSnapshot();

            // Assert
            Assert.HasCount(0, snapshot.ActiveActivities);
            Assert.HasCount(0, snapshot.CompletedActivities);
            Assert.HasCount(0, snapshot.ActiveActivityIds);
            Assert.HasCount(0, snapshot.CompletedActivityIds);
            Assert.HasCount(0, snapshot.VariableStates);
            Assert.HasCount(0, snapshot.ConditionSequences);
            Assert.IsFalse(snapshot.IsStarted);
            Assert.IsFalse(snapshot.IsCompleted);
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

