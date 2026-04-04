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
            state.StartWith(Guid.NewGuid(), "test-process", entry, variablesId);

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
            state.StartWith(Guid.NewGuid(), "test-process", entry, variablesId);

            // Assert
            Assert.AreEqual(1, state.VariableStates.Count);
            Assert.IsTrue(state.VariableStates.Any(v => v.Id == variablesId));
        }

        [TestMethod]
        public void StartWith_ShouldSetIdAndProcessDefinitionIdAndCreatedAt()
        {
            // Arrange
            var state = new WorkflowInstanceState();
            var id = Guid.NewGuid();
            var variablesId = Guid.NewGuid();
            var entry = new ActivityInstanceEntry(variablesId, "start", id);

            // Act
            state.StartWith(id, "my-process", entry, variablesId);

            // Assert
            Assert.AreEqual(id, state.Id);
            Assert.AreEqual("my-process", state.ProcessDefinitionId);
            Assert.IsNotNull(state.CreatedAt);
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
            Assert.IsNotNull(state.ExecutionStartedAt);
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
            Assert.IsNotNull(state.CompletedAt);
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
            state.StartWith(Guid.NewGuid(), "test-process", entry, variablesId);

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
            state.StartWith(Guid.NewGuid(), "test-process", entry, variablesId);

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

        [TestMethod]
        public void GetEntry_ShouldReturnEntry_ByActivityInstanceId()
        {
            // Arrange
            var state = new WorkflowInstanceState();
            var id1 = Guid.NewGuid();
            var id2 = Guid.NewGuid();
            var entry1 = new ActivityInstanceEntry(id1, "task1", Guid.Empty);
            var entry2 = new ActivityInstanceEntry(id2, "task2", Guid.Empty);
            state.AddEntries(new[] { entry1, entry2 });

            // Act
            var result = state.GetEntry(id2);

            // Assert
            Assert.AreEqual("task2", result.ActivityId);
            Assert.AreEqual(id2, result.ActivityInstanceId);
        }

        [TestMethod]
        public void FindEntry_ShouldReturnNull_WhenNotFound()
        {
            // Arrange
            var state = new WorkflowInstanceState();

            // Act
            var result = state.FindEntry(Guid.NewGuid());

            // Assert
            Assert.IsNull(result);
        }

        [TestMethod]
        public void MarkEntryCompleted_ShouldRemoveFromActiveSet_ButKeepInDictionary()
        {
            // Arrange
            var state = new WorkflowInstanceState();
            var id = Guid.NewGuid();
            var entry = new ActivityInstanceEntry(id, "task1", Guid.Empty);
            state.AddEntries(new[] { entry });

            Assert.IsTrue(state.HasActiveEntry(id));

            // Act
            entry.Complete();
            state.MarkEntryCompleted(id);

            // Assert — not active, but still findable
            Assert.IsFalse(state.HasActiveEntry(id));
            Assert.IsNotNull(state.FindEntry(id));
            Assert.AreEqual("task1", state.GetEntry(id).ActivityId);
        }

        [TestMethod]
        public void GetActiveActivities_AfterCompletion_ExcludesCompletedEntry()
        {
            // Arrange
            var state = new WorkflowInstanceState();
            var id1 = Guid.NewGuid();
            var id2 = Guid.NewGuid();
            var id3 = Guid.NewGuid();
            var entry1 = new ActivityInstanceEntry(id1, "task1", Guid.Empty);
            var entry2 = new ActivityInstanceEntry(id2, "task2", Guid.Empty);
            var entry3 = new ActivityInstanceEntry(id3, "task3", Guid.Empty);
            state.AddEntries(new[] { entry1, entry2, entry3 });

            // Act — complete entry2
            entry2.Complete();
            state.MarkEntryCompleted(id2);

            // Assert
            var active = state.GetActiveActivities().ToList();
            Assert.AreEqual(2, active.Count);
            Assert.IsTrue(active.Any(e => e.ActivityId == "task1"));
            Assert.IsTrue(active.Any(e => e.ActivityId == "task3"));
            Assert.IsFalse(active.Any(e => e.ActivityId == "task2"));
        }

        [TestMethod]
        public void IncrementalCacheUpdate_AddEntries_UpdatesBothCaches()
        {
            // Arrange — trigger cache build via a query
            var state = new WorkflowInstanceState();
            var id1 = Guid.NewGuid();
            var entry1 = new ActivityInstanceEntry(id1, "task1", Guid.Empty);
            state.AddEntries(new[] { entry1 });

            // Force cache build
            Assert.AreEqual(1, state.GetActiveActivities().Count());

            // Act — add more entries after cache is built
            var id2 = Guid.NewGuid();
            var entry2 = new ActivityInstanceEntry(id2, "task2", Guid.Empty);
            state.AddEntries(new[] { entry2 });

            // Assert — new entry visible in both active list and dictionary lookup
            Assert.AreEqual(2, state.GetActiveActivities().Count());
            Assert.IsTrue(state.HasActiveEntry(id2));
            Assert.AreEqual("task2", state.GetEntry(id2).ActivityId);
        }

        [TestMethod]
        public void LazyCache_RebuildAfterDeserialization_WorksCorrectly()
        {
            // Arrange — simulate deserialization by using StartWith (populates Entries list)
            // then querying, which forces lazy cache rebuild
            var state = new WorkflowInstanceState();
            var id = Guid.NewGuid();
            var entry = new ActivityInstanceEntry(id, "start", Guid.Empty);
            state.StartWith(Guid.NewGuid(), "test-process", entry, id);

            // Act — first query forces lazy cache rebuild
            var hasEntry = state.HasActiveEntry(id);
            var found = state.FindEntry(id);
            var active = state.GetActiveActivities().ToList();

            // Assert
            Assert.IsTrue(hasEntry);
            Assert.IsNotNull(found);
            Assert.AreEqual(1, active.Count);
            Assert.AreEqual("start", active[0].ActivityId);
        }

        [TestMethod]
        public void HasActiveChildrenInScope_ShouldReturnTrue_WhenActiveChildrenExist()
        {
            // Arrange
            var state = new WorkflowInstanceState();
            var scopeId = Guid.NewGuid();
            var entry = new ActivityInstanceEntry(Guid.NewGuid(), "task1", Guid.Empty, scopeId: scopeId);
            state.AddEntries(new[] { entry });

            // Act & Assert
            Assert.IsTrue(state.HasActiveChildrenInScope(scopeId));
        }

        [TestMethod]
        public void HasActiveChildrenInScope_ShouldReturnFalse_AfterCompletion()
        {
            // Arrange
            var state = new WorkflowInstanceState();
            var scopeId = Guid.NewGuid();
            var id = Guid.NewGuid();
            var entry = new ActivityInstanceEntry(id, "task1", Guid.Empty, scopeId: scopeId);
            state.AddEntries(new[] { entry });

            // Act
            entry.Complete();
            state.MarkEntryCompleted(id);

            // Assert
            Assert.IsFalse(state.HasActiveChildrenInScope(scopeId));
        }

        [TestMethod]
        public void GetEntriesByIdCache_ReturnsUsableDictionary()
        {
            // Arrange
            var state = new WorkflowInstanceState();
            var id1 = Guid.NewGuid();
            var id2 = Guid.NewGuid();
            state.AddEntries(new[]
            {
                new ActivityInstanceEntry(id1, "task1", Guid.Empty),
                new ActivityInstanceEntry(id2, "task2", Guid.Empty)
            });

            // Act
            var cache = state.GetEntriesByIdCache();

            // Assert
            Assert.AreEqual(2, cache.Count);
            Assert.IsTrue(cache.ContainsKey(id1));
            Assert.IsTrue(cache.ContainsKey(id2));
        }
    }
}
