using System.Dynamic;
using Fleans.Application.Grains;
using Fleans.Domain.Events;

namespace Fleans.Application.Tests;

/// <summary>
/// Fleans:Variables:MaxSizeBytes (#867): an oversized variables document is rejected before the
/// operation applies anything; the instance stays as it was and accepts a smaller one afterwards.
/// </summary>
[TestClass]
public class VariablesSizeLimitTests : WorkflowTestBase
{
    private static ExpandoObject VariablesOfSize(int jsonBytes)
    {
        var vars = new ExpandoObject();
        // {"blob":"…"} adds 11 bytes around the string.
        ((IDictionary<string, object?>)vars)["blob"] = new string('x', jsonBytes - 11);
        return vars;
    }

    [TestMethod]
    public async Task CompleteActivity_OverTheLimit_IsRejectedAndLeavesTheActivityActive()
    {
        // Arrange
        var instanceId = Guid.NewGuid();
        var grain = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(instanceId);
        await grain.StartWorkflow(CreateSimpleWorkflow("variables-size-limit"));

        // Act
        var ex = await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => grain.CompleteActivity("task", VariablesOfSize(TestMaxVariablesSizeBytes + 1)));

        // Assert — nothing applied; the same activity completes with variables at the limit
        StringAssert.Contains(ex.Message, "Fleans:Variables:MaxSizeBytes");
        var state = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsNotNull(state);
        Assert.IsTrue(state.ActiveActivities.Any(a => a.ActivityId == "task"));
        Assert.IsFalse(state.CompletedActivities.Any(a => a.ActivityId == "task"));

        await grain.CompleteActivity("task", VariablesOfSize(TestMaxVariablesSizeBytes));
        state = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsNotNull(state);
        Assert.IsTrue(state.CompletedActivities.Any(a => a.ActivityId == "task"));
    }

    [TestMethod]
    public async Task StartWorkflow_WithVariablesOverTheLimit_IsRejectedAndStartsNothing()
    {
        // Arrange
        var instanceId = Guid.NewGuid();
        var grain = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(instanceId);
        var workflow = CreateSimpleWorkflow("variables-size-limit");

        // Act
        await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => grain.StartWorkflow(workflow, variables: VariablesOfSize(TestMaxVariablesSizeBytes + 1)));

        // Assert — nothing journaled, and a start within the limit still works
        Assert.IsEmpty(await GetSiloService<IEventStore>().ReadEventsAsync(instanceId, afterVersion: 0));
        await grain.StartWorkflow(workflow);
        var state = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsNotNull(state);
        Assert.IsTrue(state.IsStarted);
        Assert.IsFalse(state.VariableStates.Any(v => v.Variables.ContainsKey("blob")));
    }
}
