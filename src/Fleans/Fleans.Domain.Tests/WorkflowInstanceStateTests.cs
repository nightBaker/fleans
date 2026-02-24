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
        public void OpenScope_ShouldAddScope_WithCorrectProperties()
        {
            // Arrange
            var state = new WorkflowInstanceState();
            var scopeId = Guid.NewGuid();
            var variablesId = Guid.NewGuid();
            var spInstanceId = Guid.NewGuid();

            // Act
            var scope = state.OpenScope(scopeId, null, variablesId, "sp1", spInstanceId);

            // Assert
            Assert.AreEqual(1, state.Scopes.Count);
            Assert.AreEqual(scopeId, scope.ScopeId);
            Assert.IsNull(scope.ParentScopeId);
            Assert.AreEqual(variablesId, scope.VariablesId);
            Assert.AreEqual("sp1", scope.SubProcessActivityId);
            Assert.AreEqual(spInstanceId, scope.SubProcessActivityInstanceId);
        }

        [TestMethod]
        public void GetScope_ShouldReturnCorrectScope()
        {
            // Arrange
            var state = new WorkflowInstanceState();
            var scopeId = Guid.NewGuid();
            state.OpenScope(scopeId, null, Guid.NewGuid(), "sp1", Guid.NewGuid());

            // Act
            var found = state.GetScope(scopeId);

            // Assert
            Assert.IsNotNull(found);
            Assert.AreEqual(scopeId, found.ScopeId);
        }

        [TestMethod]
        public void CloseScope_ShouldRemoveScope()
        {
            // Arrange
            var state = new WorkflowInstanceState();
            var scopeId = Guid.NewGuid();
            state.OpenScope(scopeId, null, Guid.NewGuid(), "sp1", Guid.NewGuid());

            // Act
            state.CloseScope(scopeId);

            // Assert
            Assert.AreEqual(0, state.Scopes.Count);
        }

        [TestMethod]
        public void CancelScope_ShouldDrainChildrenAndRemoveScope()
        {
            // Arrange
            var state = new WorkflowInstanceState();
            var scopeId = Guid.NewGuid();
            var scope = state.OpenScope(scopeId, null, Guid.NewGuid(), "sp1", Guid.NewGuid());
            var child1 = Guid.NewGuid();
            var child2 = Guid.NewGuid();
            scope.TrackChild(child1);
            scope.TrackChild(child2);

            // Act
            var cancelled = state.CancelScope(scopeId);

            // Assert
            Assert.HasCount(2, cancelled);
            Assert.IsTrue(cancelled.Contains(child1));
            Assert.IsTrue(cancelled.Contains(child2));
            Assert.AreEqual(0, state.Scopes.Count);
        }

        [TestMethod]
        public void CancelScope_ShouldRecursivelyDrainNestedScopes()
        {
            // Arrange
            var state = new WorkflowInstanceState();
            var outerScopeId = Guid.NewGuid();
            var innerScopeId = Guid.NewGuid();
            var outerChild = Guid.NewGuid();
            var innerChild = Guid.NewGuid();

            var outerScope = state.OpenScope(outerScopeId, null, Guid.NewGuid(), "outer-sp", Guid.NewGuid());
            outerScope.TrackChild(outerChild);

            var innerScope = state.OpenScope(innerScopeId, outerScopeId, Guid.NewGuid(), "inner-sp", Guid.NewGuid());
            innerScope.TrackChild(innerChild);

            // Act
            var cancelled = state.CancelScope(outerScopeId);

            // Assert
            Assert.HasCount(2, cancelled);
            Assert.IsTrue(cancelled.Contains(outerChild));
            Assert.IsTrue(cancelled.Contains(innerChild));
            Assert.AreEqual(0, state.Scopes.Count);
        }

        [TestMethod]
        public void CreateChildVariableScope_ShouldCreateScopeWithParentPointer()
        {
            // Arrange
            var state = new WorkflowInstanceState();
            var rootVariablesId = Guid.NewGuid();
            state.StartWith(Guid.NewGuid(), null, new ActivityInstanceEntry(Guid.NewGuid(), "start", Guid.Empty), rootVariablesId);

            // Act
            var childId = state.CreateChildVariableScope(rootVariablesId);

            // Assert
            var child = state.GetVariableState(childId);
            Assert.IsNotNull(child);
            Assert.AreEqual(rootVariablesId, child.ParentVariablesId);
        }
    }
}
