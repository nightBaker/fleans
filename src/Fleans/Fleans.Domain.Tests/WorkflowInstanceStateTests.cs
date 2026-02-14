using Fleans.Domain.States;
using System.Dynamic;
using System.Linq;

namespace Fleans.Domain.Tests
{
    [TestClass]
    public class WorkflowInstanceStateTests
    {
        [TestMethod]
        public void StartWith_ShouldAddEntry_ToActiveActivities()
        {
            // Arrange
            var state = new WorkflowInstanceState();
            var variablesId = Guid.NewGuid();
            var entry = new ActivityInstanceEntry(variablesId, "start", Guid.Empty);

            // Act
            state.StartWith(entry, variablesId);

            // Assert
            var activeActivities = state.GetActiveActivities();
            Assert.AreEqual(1, activeActivities.Count());
            Assert.AreEqual("start", activeActivities.First().ActivityId);
            Assert.AreEqual(variablesId, activeActivities.First().ActivityInstanceId);
        }

        [TestMethod]
        public void StartWith_ShouldCreateInitialVariableState()
        {
            // Arrange
            var state = new WorkflowInstanceState();
            var variablesId = Guid.NewGuid();
            var entry = new ActivityInstanceEntry(variablesId, "start", Guid.Empty);

            // Act
            state.StartWith(entry, variablesId);

            // Assert
            Assert.AreEqual(1, state.VariableStates.Count);
            Assert.IsTrue(state.VariableStates.Any(v => v.Id == variablesId));
        }

        [TestMethod]
        public void Start_ShouldMarkWorkflowAsStarted()
        {
            // Arrange
            var state = new WorkflowInstanceState();

            // Act
            state.Start();

            // Assert
            Assert.IsTrue(state.IsStarted);
        }

        [TestMethod]
        public void Start_ShouldThrowException_WhenAlreadyStarted()
        {
            // Arrange
            var state = new WorkflowInstanceState();
            state.Start();

            // Act & Assert
            Assert.ThrowsExactly<InvalidOperationException>(() =>
            {
                state.Start();
            });
        }

        [TestMethod]
        public void Complete_ShouldMarkWorkflowAsCompleted()
        {
            // Arrange
            var state = new WorkflowInstanceState();

            // Act
            state.Complete();

            // Assert
            Assert.IsTrue(state.IsCompleted);
        }

        [TestMethod]
        public void AddEntries_ShouldAddEntries_ToActiveList()
        {
            // Arrange
            var state = new WorkflowInstanceState();
            var entry1 = new ActivityInstanceEntry(Guid.NewGuid(), "task1", Guid.Empty);
            var entry2 = new ActivityInstanceEntry(Guid.NewGuid(), "task2", Guid.Empty);

            // Act
            state.AddEntries(new[] { entry1, entry2 });

            // Assert
            var activeActivities = state.GetActiveActivities();
            Assert.AreEqual(2, activeActivities.Count());
            Assert.AreEqual("task1", activeActivities.First().ActivityId);
            Assert.AreEqual("task2", activeActivities.ElementAt(1).ActivityId);
        }

        [TestMethod]
        public void CompleteEntries_ShouldMoveEntries_FromActiveToCompleted()
        {
            // Arrange
            var state = new WorkflowInstanceState();
            var entry1 = new ActivityInstanceEntry(Guid.NewGuid(), "task1", Guid.Empty);
            var entry2 = new ActivityInstanceEntry(Guid.NewGuid(), "task2", Guid.Empty);
            state.AddEntries(new[] { entry1, entry2 });

            // Act
            state.CompleteEntries(new List<ActivityInstanceEntry> { entry1 });

            // Assert
            var activeActivities = state.GetActiveActivities();
            Assert.AreEqual(1, activeActivities.Count());
            Assert.AreEqual(entry2.ActivityId, activeActivities.First().ActivityId);

            var completedActivities = state.GetCompletedActivities();
            Assert.AreEqual(1, completedActivities.Count());
            Assert.AreEqual(entry1.ActivityId, completedActivities.First().ActivityId);
        }

        [TestMethod]
        public void CompleteEntries_ShouldMarkActiveEntry_AsCompleted()
        {
            // Arrange
            var state = new WorkflowInstanceState();
            var entry = new ActivityInstanceEntry(Guid.NewGuid(), "task", Guid.Empty);
            state.AddEntries(new[] { entry });

            // Act
            state.CompleteEntries(new List<ActivityInstanceEntry> { entry });

            // Assert
            var completedActivities = state.GetCompletedActivities();
            Assert.AreEqual(1, completedActivities.Count());
            Assert.AreEqual("task", completedActivities.First().ActivityId);
        }

        [TestMethod]
        public void GetFirstActive_ShouldReturnEntry_WhenFound()
        {
            // Arrange
            var state = new WorkflowInstanceState();
            var entry = new ActivityInstanceEntry(Guid.NewGuid(), "task1", Guid.Empty);
            state.AddEntries(new[] { entry });

            // Act
            var result = state.GetFirstActive("task1");

            // Assert
            Assert.IsNotNull(result);
            Assert.AreEqual("task1", result.ActivityId);
            Assert.AreEqual(entry.ActivityInstanceId, result.ActivityInstanceId);
        }

        [TestMethod]
        public void GetFirstActive_ShouldReturnNull_WhenNotFound()
        {
            // Arrange
            var state = new WorkflowInstanceState();

            // Act
            var result = state.GetFirstActive("non-existent");

            // Assert
            Assert.IsNull(result);
        }

        [TestMethod]
        public void AddCloneOfVariableState_ShouldCreateNewVariableState_WithClonedData()
        {
            // Arrange
            var state = new WorkflowInstanceState();
            var variablesId = Guid.NewGuid();
            var entry = new ActivityInstanceEntry(variablesId, "start", Guid.Empty);
            state.StartWith(entry, variablesId);

            // Act
            var clonedId = state.AddCloneOfVariableState(variablesId);

            // Assert
            Assert.AreNotEqual(variablesId, clonedId);
            Assert.IsTrue(state.VariableStates.Any(v => v.Id == clonedId));
        }

        [TestMethod]
        public void MergeState_ShouldMergeVariables_IntoExistingState()
        {
            // Arrange
            var state = new WorkflowInstanceState();
            var variablesId = Guid.NewGuid();
            var entry = new ActivityInstanceEntry(variablesId, "start", Guid.Empty);
            state.StartWith(entry, variablesId);

            dynamic newVariables = new ExpandoObject();
            ((IDictionary<string, object>)newVariables)["key1"] = "value1";
            ((IDictionary<string, object>)newVariables)["key2"] = 42;

            // Act
            state.MergeState(variablesId, newVariables);

            // Assert
            var mergedState = state.GetVariableState(variablesId);
            Assert.IsNotNull(mergedState);
        }

        [TestMethod]
        public void AddConditionSequenceStates_ShouldStoreSequences()
        {
            // Arrange
            var state = new WorkflowInstanceState();
            var activityInstanceId = Guid.NewGuid();

            // Act
            state.AddConditionSequenceStates(activityInstanceId, new[] { "seq1" });

            // Assert
            Assert.IsTrue(state.ConditionSequenceStates.Any(c => c.GatewayActivityInstanceId == activityInstanceId));
            Assert.AreEqual(1, state.GetConditionSequenceStatesForGateway(activityInstanceId).Count());
        }

        [TestMethod]
        public void SetConditionSequenceResult_ShouldUpdateResult()
        {
            // Arrange
            var state = new WorkflowInstanceState();
            var activityInstanceId = Guid.NewGuid();
            state.AddConditionSequenceStates(activityInstanceId, new[] { "seq1" });

            // Act
            state.SetConditionSequenceResult(activityInstanceId, "seq1", true);

            // Assert
            var conditionStates = state.GetConditionSequenceStatesForGateway(activityInstanceId).ToArray();
            Assert.IsTrue(conditionStates[0].Result);
        }

        [TestMethod]
        public void SetConditionSequenceResult_ShouldThrow_WhenSequenceNotFound()
        {
            // Arrange
            var state = new WorkflowInstanceState();
            var activityInstanceId = Guid.NewGuid();
            state.AddConditionSequenceStates(activityInstanceId, new[] { "seq1" });

            // Act & Assert
            Assert.ThrowsExactly<InvalidOperationException>(() =>
            {
                state.SetConditionSequenceResult(activityInstanceId, "non-existent", true);
            });
        }
    }
}
