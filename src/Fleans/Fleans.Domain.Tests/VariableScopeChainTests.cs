using Fleans.Domain.States;
using System.Dynamic;

namespace Fleans.Domain.Tests;

[TestClass]
public class VariableScopeChainTests
{
    [TestMethod]
    public void AddChildVariableState_ShouldSetParentVariablesId()
    {
        var state = new WorkflowInstanceState();
        var rootVarsId = Guid.NewGuid();
        var entry = new ActivityInstanceEntry(Guid.NewGuid(), "start", Guid.NewGuid());
        state.StartWith(Guid.NewGuid(), null, entry, rootVarsId);

        var childVarsId = state.AddChildVariableState(rootVarsId);

        var childState = state.GetVariableState(childVarsId);
        Assert.IsNotNull(childState);
        Assert.AreEqual(rootVarsId, childState.ParentVariablesId);
    }

    [TestMethod]
    public void ChildScope_ShouldStartEmpty()
    {
        var state = new WorkflowInstanceState();
        var rootVarsId = Guid.NewGuid();
        var entry = new ActivityInstanceEntry(Guid.NewGuid(), "start", Guid.NewGuid());
        state.StartWith(Guid.NewGuid(), null, entry, rootVarsId);

        dynamic parentVars = new ExpandoObject();
        parentVars.color = "red";
        state.MergeState(rootVarsId, parentVars);

        var childVarsId = state.AddChildVariableState(rootVarsId);

        var childState = state.GetVariableState(childVarsId);
        var childDict = (IDictionary<string, object?>)childState.Variables;
        Assert.AreEqual(0, childDict.Count);
    }

    [TestMethod]
    public void RootScope_ShouldHaveNullParentVariablesId()
    {
        var state = new WorkflowInstanceState();
        var rootVarsId = Guid.NewGuid();
        var entry = new ActivityInstanceEntry(Guid.NewGuid(), "start", Guid.NewGuid());
        state.StartWith(Guid.NewGuid(), null, entry, rootVarsId);

        var rootState = state.GetVariableState(rootVarsId);
        Assert.IsNull(rootState.ParentVariablesId);
    }

    [TestMethod]
    public void AddCloneOfVariableState_ShouldPreserveParentVariablesId()
    {
        var state = new WorkflowInstanceState();
        var rootVarsId = Guid.NewGuid();
        var entry = new ActivityInstanceEntry(Guid.NewGuid(), "start", Guid.NewGuid());
        state.StartWith(Guid.NewGuid(), null, entry, rootVarsId);

        // Create child scope (inside sub-process)
        var childVarsId = state.AddChildVariableState(rootVarsId);

        // Clone the child scope (parallel fork inside sub-process)
        var clonedId = state.AddCloneOfVariableState(childVarsId);

        // Clone should preserve parent pointer
        var clonedState = state.GetVariableState(clonedId);
        Assert.AreEqual(rootVarsId, clonedState.ParentVariablesId,
            "Cloned scope should preserve ParentVariablesId for walk-up");
    }
}
