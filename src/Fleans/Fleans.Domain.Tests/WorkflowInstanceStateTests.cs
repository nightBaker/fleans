using Fleans.Domain.States;
using System.Dynamic;

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
            var entry = new ActivityInstanceEntry(variablesId, "start");

            // Act
            state.StartWith(entry, variablesId);

            // Assert
            var activeActivities = state.GetActiveActivities();
            Assert.HasCount(1, activeActivities);
            Assert.AreEqual("start", activeActivities[0].ActivityId);
            Assert.AreEqual(variablesId, activeActivities[0].ActivityInstanceId);
        }

        [TestMethod]
        public void StartWith_ShouldCreateInitialVariableState()
        {
            // Arrange
            var state = new WorkflowInstanceState();
            var variablesId = Guid.NewGuid();
            var entry = new ActivityInstanceEntry(variablesId, "start");

            // Act
            state.StartWith(entry, variablesId);

            // Assert
            var variableStates = state.GetVariableStates();
            Assert.HasCount(1, variableStates);
            Assert.IsTrue(variableStates.ContainsKey(variablesId));
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
        public void AddActiveActivities_ShouldAddEntries_ToActiveList()
        {
            // Arrange
            var state = new WorkflowInstanceState();
            var entry1 = new ActivityInstanceEntry(Guid.NewGuid(), "task1");
            var entry2 = new ActivityInstanceEntry(Guid.NewGuid(), "task2");

            // Act
            state.AddActiveActivities(new[] { entry1, entry2 });

            // Assert
            var activeActivities = state.GetActiveActivities();
            Assert.HasCount(2, activeActivities);
            Assert.AreEqual("task1", activeActivities[0].ActivityId);
            Assert.AreEqual("task2", activeActivities[1].ActivityId);
        }

        [TestMethod]
        public void RemoveActiveActivities_ShouldRemoveEntries_FromActiveList()
        {
            // Arrange
            var state = new WorkflowInstanceState();
            var entry1 = new ActivityInstanceEntry(Guid.NewGuid(), "task1");
            var entry2 = new ActivityInstanceEntry(Guid.NewGuid(), "task2");
            state.AddActiveActivities(new[] { entry1, entry2 });

            // Act
            state.RemoveActiveActivities(new List<ActivityInstanceEntry> { entry1 });

            // Assert
            var activeActivities = state.GetActiveActivities();
            Assert.HasCount(1, activeActivities);
            Assert.AreEqual(entry2, activeActivities[0]);
        }

        [TestMethod]
        public void AddCompletedActivities_ShouldAddEntries_ToCompletedList()
        {
            // Arrange
            var state = new WorkflowInstanceState();
            var entry = new ActivityInstanceEntry(Guid.NewGuid(), "task");

            // Act
            state.AddCompletedActivities(new[] { entry });

            // Assert
            var completedActivities = state.GetCompletedActivities();
            Assert.HasCount(1, completedActivities);
            Assert.AreEqual("task", completedActivities[0].ActivityId);
        }

        [TestMethod]
        public void GetFirstActive_ShouldReturnEntry_WhenFound()
        {
            // Arrange
            var state = new WorkflowInstanceState();
            var entry = new ActivityInstanceEntry(Guid.NewGuid(), "task1");
            state.AddActiveActivities(new[] { entry });

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
            var entry = new ActivityInstanceEntry(variablesId, "start");
            state.StartWith(entry, variablesId);

            // Act
            var clonedId = state.AddCloneOfVariableState(variablesId);

            // Assert
            Assert.AreNotEqual(variablesId, clonedId);
            var newVariableStates = state.GetVariableStates();
            Assert.IsTrue(newVariableStates.ContainsKey(clonedId));
        }

        [TestMethod]
        public void MergeState_ShouldMergeVariables_IntoExistingState()
        {
            // Arrange
            var state = new WorkflowInstanceState();
            var variablesId = Guid.NewGuid();
            var entry = new ActivityInstanceEntry(variablesId, "start");
            state.StartWith(entry, variablesId);

            dynamic newVariables = new ExpandoObject();
            ((IDictionary<string, object>)newVariables)["key1"] = "value1";
            ((IDictionary<string, object>)newVariables)["key2"] = 42;

            // Act
            state.MergeState(variablesId, newVariables);

            // Assert
            var updatedStates = state.GetVariableStates();
            var mergedState = updatedStates[variablesId];
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
            var conditionStates = state.GetConditionSequenceStates();
            Assert.IsTrue(conditionStates.ContainsKey(activityInstanceId));
            Assert.HasCount(1, conditionStates[activityInstanceId]);
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
            var conditionStates = state.GetConditionSequenceStates();
            Assert.IsTrue(conditionStates[activityInstanceId][0].Result);
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
