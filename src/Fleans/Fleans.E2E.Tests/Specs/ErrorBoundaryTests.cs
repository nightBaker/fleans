using Fleans.E2E.Tests.ApiClient;
using Fleans.E2E.Tests.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Fleans.E2E.Tests.Specs;

// Ports tests/manual/11-error-boundary/test-plan.md
[TestClass]
[TestCategory("E2E")]
public class ErrorBoundaryTests : WorkflowE2ETestBase
{
    [TestMethod]
    [Ignore("Known bug: child process errors don't propagate to parent error boundary on CallActivity; CallActivity stays Running indefinitely. See docs/plans/2026-02-25-manual-test-results.md.")]
    public async Task ChildErrorPropagatesThroughCallActivityBoundary_ParentCompletesViaErrorPath()
    {
        // Deploy child first
        var childXml = BpmnFixtureLoader.Load("11-error-boundary", "child-that-fails.bpmn");
        await ApiClient.DeployAsync(childXml);

        var parentXml = BpmnFixtureLoader.Load("11-error-boundary", "error-on-call-activity.bpmn");
        var parent = await ApiClient.DeployAsync(parentXml);
        var started = await ApiClient.StartAsync(parent.ProcessDefinitionKey);

        var state = await ApiClient.WaitForCompletionAsync(
            started.WorkflowInstanceId,
            timeout: TimeSpan.FromSeconds(30));

        Assert.IsFalse(state.IsCancelled, "Parent should NOT be cancelled.");
        Assert.IsTrue(state.IsCompleted, "Parent should be Completed (via error path), not stuck Running.");
        state.AssertCompletedActivities("errorHandler");
        state.AssertNotCompleted("happyEnd");
        state.AssertVariableEquals("errorHandled", "True");
    }
}
