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

    [TestMethod]
    public void GetMergedVariables_ShouldMergeParentAndChild()
    {
        var state = new WorkflowInstanceState();
        var rootVarsId = Guid.NewGuid();
        var entry = new ActivityInstanceEntry(Guid.NewGuid(), "start", Guid.NewGuid());
        state.StartWith(Guid.NewGuid(), null, entry, rootVarsId);

        dynamic parentVars = new ExpandoObject();
        parentVars.color = "red";
        parentVars.size = 10;
        state.MergeState(rootVarsId, parentVars);

        var childVarsId = state.AddChildVariableState(rootVarsId);
        dynamic childVars = new ExpandoObject();
        childVars.color = "blue"; // shadows parent
        state.MergeState(childVarsId, childVars);

        var merged = state.GetMergedVariables(childVarsId);
        var dict = (IDictionary<string, object?>)merged;
        Assert.AreEqual("blue", dict["color"]);
        Assert.AreEqual(10, dict["size"]);
    }

    [TestMethod]
    public void GetVariable_ShouldWalkUpToParent()
    {
        var state = new WorkflowInstanceState();
        var rootVarsId = Guid.NewGuid();
        var entry = new ActivityInstanceEntry(Guid.NewGuid(), "start", Guid.NewGuid());
        state.StartWith(Guid.NewGuid(), null, entry, rootVarsId);

        dynamic parentVars = new ExpandoObject();
        parentVars.color = "red";
        state.MergeState(rootVarsId, parentVars);

        var childVarsId = state.AddChildVariableState(rootVarsId);

        var result = state.GetVariable(childVarsId, "color");
        Assert.AreEqual("red", result);
    }

    [TestMethod]
    public void GetVariable_ShouldReturnNull_WhenNotFoundAnywhere()
    {
        var state = new WorkflowInstanceState();
        var rootVarsId = Guid.NewGuid();
        var entry = new ActivityInstanceEntry(Guid.NewGuid(), "start", Guid.NewGuid());
        state.StartWith(Guid.NewGuid(), null, entry, rootVarsId);

        var result = state.GetVariable(rootVarsId, "missing");
        Assert.IsNull(result);
    }

    [TestMethod]
    public void GetMergedVariables_ShouldWalkThreeLevels()
    {
        var state = new WorkflowInstanceState();
        var rootVarsId = Guid.NewGuid();
        var entry = new ActivityInstanceEntry(Guid.NewGuid(), "start", Guid.NewGuid());
        state.StartWith(Guid.NewGuid(), null, entry, rootVarsId);

        dynamic rootVars = new ExpandoObject();
        rootVars.a = 1;
        state.MergeState(rootVarsId, rootVars);

        var midVarsId = state.AddChildVariableState(rootVarsId);
        dynamic midVars = new ExpandoObject();
        midVars.b = 2;
        state.MergeState(midVarsId, midVars);

        var leafVarsId = state.AddChildVariableState(midVarsId);
        dynamic leafVars = new ExpandoObject();
        leafVars.c = 3;
        state.MergeState(leafVarsId, leafVars);

        var merged = state.GetMergedVariables(leafVarsId);
        var dict = (IDictionary<string, object?>)merged;
        Assert.AreEqual(1, dict["a"]);
        Assert.AreEqual(2, dict["b"]);
        Assert.AreEqual(3, dict["c"]);
    }
}
