using Fleans.Application.QueryModels;
using Fleans.E2E.Tests.ApiClient;
using Fleans.E2E.Tests.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Fleans.E2E.Tests.Specs;

// Ports tests/manual/24-conditional-event/test-plan.md
[TestClass]
[TestCategory("E2E")]
public class ConditionalEventTests : WorkflowE2ETestBase
{
    [TestMethod]
    public async Task ConditionalIntermediateCatch_FiresWhenAmountExceedsThreshold()
    {
        var (instanceId, _) = await RunConditionalCatchFixtureAsync(amount: 600);

        var state = await ApiClient.WaitForCompletionAsync(
            instanceId,
            timeout: TimeSpan.FromSeconds(20));
        state.AssertCompletedActivities("wait-for-amount", "after-condition", "parallel-task");
        state.AssertVariableEquals("result", "condition-met");
    }

    [TestMethod]
    public async Task ConditionalIntermediateCatch_StaysBlockedWhenConditionFalse()
    {
        var (_, state) = await RunConditionalCatchFixtureAsync(amount: 100);

        Assert.IsFalse(state.IsCompleted, "amount=100 must not satisfy `amount > 500`.");
        // A condition evaluation error would fail (and complete) the watcher's activity,
        // so still being active also proves the condition evaluated cleanly to false.
        CollectionAssert.Contains(state.ActiveActivityIds, "wait-for-amount");
        state.AssertNotCompleted("after-condition");
    }

    [TestMethod]
    public async Task ConditionalStartEvent_CreatesInstanceWhenConditionEvaluatesTrue()
    {
        var xml = BpmnFixtureLoader.Load("24-conditional-event", "conditional-start-event.bpmn");
        var deployed = await ApiClient.DeployAsync(xml);

        var hot = await ApiClient.EvaluateConditionsAsync(
            workflowId: deployed.ProcessDefinitionKey,
            variables: new Dictionary<string, object> { ["temperature"] = 150 });
        Assert.IsNull(hot.Errors, $"Unexpected evaluation errors: {string.Join("; ", hot.Errors ?? [])}");
        Assert.HasCount(1, hot.StartedInstanceIds,
            "temperature=150 should satisfy `temperature > 100` and start exactly one instance.");

        var startedState = await ApiClient.WaitForCompletionAsync(
            hot.StartedInstanceIds[0],
            timeout: TimeSpan.FromSeconds(20));
        startedState.AssertCompletedActivities("condStart", "process-alert", "end");
        startedState.AssertVariableEquals("alert", "temperature-exceeded");

        var cold = await ApiClient.EvaluateConditionsAsync(
            workflowId: deployed.ProcessDefinitionKey,
            variables: new Dictionary<string, object> { ["temperature"] = 50 });
        Assert.IsNull(cold.Errors, $"Unexpected evaluation errors: {string.Join("; ", cold.Errors ?? [])}");
        Assert.IsEmpty(cold.StartedInstanceIds,
            "temperature=50 should NOT satisfy the condition; no instance should be created.");
    }

    /// <summary>
    /// Starts the conditional-catch fixture with the given <c>amount</c> start variable,
    /// waits for the catch to register its watcher, then completes the parallel task.
    /// Watchers are re-evaluated whenever another activity completes, so this completion
    /// is what triggers evaluation of <c>amount &gt; 500</c>.
    /// </summary>
    private async Task<(Guid InstanceId, InstanceStateSnapshot State)> RunConditionalCatchFixtureAsync(int amount)
    {
        var xml = BpmnFixtureLoader.Load("24-conditional-event", "conditional-event-test.bpmn");
        var deployed = await ApiClient.DeployAsync(xml);
        var started = await ApiClient.StartAsync(
            deployed.ProcessDefinitionKey,
            new Dictionary<string, object?> { ["amount"] = amount });

        await ApiClient.WaitForStateAsync(
            started.WorkflowInstanceId,
            s => s.ActiveActivityIds.Contains("wait-for-amount")
                 && s.ActiveActivityIds.Contains("parallel-task"));

        using (var resp = await ApiClient.CompleteActivityAsync(started.WorkflowInstanceId, "parallel-task"))
        {
            Assert.IsTrue(resp.IsSuccessStatusCode,
                $"complete-activity should succeed; got {resp.StatusCode}.");
        }

        var state = await ApiClient.WaitForStateAsync(
            started.WorkflowInstanceId,
            s => s.CompletedActivityIds.Contains("parallel-task"));
        return (started.WorkflowInstanceId, state);
    }
}
