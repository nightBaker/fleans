using Fleans.E2E.Tests.ApiClient;
using Fleans.E2E.Tests.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Fleans.E2E.Tests.Specs;

// Ports tests/manual/08-timer-events/test-plan.md
[TestClass]
[TestCategory("E2E")]
public class TimerEventTests : WorkflowE2ETestBase
{
    [TestMethod]
    public async Task TimerIntermediateCatch_FiresAfterDelay_WorkflowResumes()
    {
        var xml = BpmnFixtureLoader.Load("08-timer-events", "timer-intermediate-catch.bpmn");
        var deployed = await ApiClient.DeployAsync(xml);
        var started = await ApiClient.StartAsync(deployed.ProcessDefinitionKey);

        await ApiClient.WaitForStateAsync(
            started.WorkflowInstanceId,
            s => s.IsStarted && s.ActiveActivityIds.Contains("waitTimer"));

        // Fixture uses a 5s timer; give it ~30s to fire + drain.
        var state = await ApiClient.WaitForCompletionAsync(
            started.WorkflowInstanceId,
            timeout: TimeSpan.FromSeconds(30));

        state.AssertCompletedActivities("waitTimer", "afterTimer");
        state.AssertVariableEquals("timerFired", "True");
    }

    // tests/manual/08-timer-events/test-plan.md notes a KNOWN BUG:
    //   "Boundary events on IntermediateCatchEvents don't register subscriptions.
    //    The timer boundary will not fire."
    // Spec authored against the fix so it runs once the bug is closed.
    [TestMethod]
    [Ignore("Known bug: boundary events on IntermediateCatchEvent do not register subscriptions; see docs/plans/2026-02-25-manual-test-results.md.")]
    public async Task TimerBoundary_InterruptsBlockingActivity_TimeoutPathTaken()
    {
        var xml = BpmnFixtureLoader.Load("08-timer-events", "timer-boundary.bpmn");
        var deployed = await ApiClient.DeployAsync(xml);
        var started = await ApiClient.StartAsync(deployed.ProcessDefinitionKey);

        var state = await ApiClient.WaitForCompletionAsync(
            started.WorkflowInstanceId,
            timeout: TimeSpan.FromSeconds(30));

        state.AssertCompletedActivities("timeoutPath");
        state.AssertNotCompleted("normalEnd");
        state.AssertVariableEquals("timedOut", "True");
    }
}
