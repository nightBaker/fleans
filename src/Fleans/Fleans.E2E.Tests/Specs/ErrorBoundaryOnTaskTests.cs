using Fleans.E2E.Tests.ApiClient;
using Fleans.E2E.Tests.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Fleans.E2E.Tests.Specs;

// Ports tests/manual/11-error-boundary/test-plan.md (Scenario B — error boundary on a script task)
[TestClass]
[TestCategory("E2E")]
public class ErrorBoundaryOnTaskTests : WorkflowE2ETestBase
{
    private const string PlanFolder = "11-error-boundary";
    private const string Fixture = "error-on-script-task.bpmn";

    [TestMethod]
    public async Task ErrorBoundaryOnScriptTask_TaskFails_ErrorPathTaken()
    {
        var deployed = await ApiClient.DeployAsync(BpmnFixtureLoader.Load(PlanFolder, Fixture));
        var started = await ApiClient.StartAsync(
            deployed.ProcessDefinitionKey,
            new Dictionary<string, object?> { ["divisor"] = 0 });

        var state = await ApiClient.WaitForCompletionAsync(started.WorkflowInstanceId);

        Assert.IsFalse(state.IsCancelled, "The instance should complete via the error path, not be cancelled.");
        state.AssertCompletedActivities("errorBoundary", "errorHandler", "errorEnd");
        state.AssertNotCompleted("happyPath", "happyEnd");
        state.AssertVariableEquals("errorHandled", "True");

        var failed = state.CompletedActivities.Single(a => a.ActivityId == "riskyScript");
        Assert.IsNotNull(failed.ErrorState, "The failing script task must carry its error state.");
        Assert.AreEqual("500", failed.ErrorState.Code, "A script exception surfaces as error code 500.");
    }

    [TestMethod]
    public async Task ErrorBoundaryOnScriptTask_TaskSucceeds_BoundaryNotTriggered()
    {
        var deployed = await ApiClient.DeployAsync(BpmnFixtureLoader.Load(PlanFolder, Fixture));
        var started = await ApiClient.StartAsync(
            deployed.ProcessDefinitionKey,
            new Dictionary<string, object?> { ["divisor"] = 2 });

        var state = await ApiClient.WaitForCompletionAsync(started.WorkflowInstanceId);

        state.AssertCompletedActivities("riskyScript", "happyPath", "happyEnd");
        state.AssertNotCompleted("errorBoundary", "errorHandler", "errorEnd");
        state.AssertVariableEquals("quotient", "5");
        Assert.IsNull(state.CompletedActivities.Single(a => a.ActivityId == "riskyScript").ErrorState);
    }
}
