using System.Dynamic;
using Fleans.Application.QueryModels;
using Fleans.Domain.States;
using Newtonsoft.Json;

namespace Fleans.Application.Tests;

/// <summary>
/// A running instance's snapshot is built from the grain's live variables, a completed one's from
/// the read model, where variables went through JSON. Both must show a value the same way.
/// </summary>
[TestClass]
public class InstanceStateSnapshotsTests
{
    private static WorkflowInstanceState StateWith(ExpandoObject variables, out Guid variablesId)
    {
        var state = new WorkflowInstanceState();
        variablesId = Guid.NewGuid();
        state.Initialize(Guid.NewGuid(), null, variablesId, DateTimeOffset.UtcNow);
        state.MergeState(variablesId, variables);
        return state;
    }

    [TestMethod]
    public void From_LiveAndStoredVariables_FormatTheSame()
    {
        // Arrange — live values as a script leaves them, and the same values as the read model reads them back
        var order = new ExpandoObject();
        ((IDictionary<string, object?>)order)["id"] = 7;
        var live = new ExpandoObject();
        ((IDictionary<string, object?>)live)["numbers"] = new[] { 1, 2, 3 };
        ((IDictionary<string, object?>)live)["order"] = order;
        var stored = JsonConvert.DeserializeObject<ExpandoObject>(JsonConvert.SerializeObject(live))!;

        var running = StateWith(live, out var runningScope);
        var completed = StateWith(stored, out var completedScope);

        // Act
        var runningVars = InstanceStateSnapshots.From(running, null, stateVersion: 1, isFromReadModel: false).VariableStates
            .Single(v => v.VariablesId == runningScope).Variables;
        var completedVars = InstanceStateSnapshots.From(completed, null, stateVersion: 1, isFromReadModel: true).VariableStates
            .Single(v => v.VariablesId == completedScope).Variables;

        // Assert
        Assert.AreEqual("[1,2,3]", runningVars["numbers"]);
        Assert.AreEqual("{\"id\":7}", runningVars["order"]);
        CollectionAssert.AreEquivalent(runningVars.ToList(), completedVars.ToList());
    }
}
