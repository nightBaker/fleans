using System.Net;
using Fleans.E2E.Tests.ApiClient;
using Fleans.E2E.Tests.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Fleans.E2E.Tests.Specs;

// Ports tests/manual/83-script-format-workers/test-plan.md, scenarios 2 and 3 (#1105), against the
// real JavaScript plugin: the AppHost runs the fleans-plugins host (Plugins__Enabled=javascript) as
// fleans-javascript-worker when FLEANS_JAVASCRIPT_WORKER_PROJECT points at it. The e2e CI job checks
// out and builds nightBaker/fleans-plugins for this; without the variable the specs are inconclusive.
[TestClass]
[TestCategory("E2E")]
public class JavaScriptScriptTaskTests : WorkflowE2ETestBase
{
    private const string TaskType = "fleans.script.javascript";

    [TestMethod]
    public async Task JavaScriptTask_RunsOnThePlugin_AndWritesBackWhatItChanged()
    {
        await WaitForPluginAsync();
        var deployed = await ApiClient.DeployAsync(BpmnFixtureLoader.Load("02-script-tasks", "javascript-variables.bpmn"));
        Assert.IsEmpty(deployed.Warnings ?? [], "A connected JavaScript worker means no deploy warning.");

        var started = await ApiClient.StartAsync(deployed.ProcessDefinitionKey);
        var state = await ApiClient.WaitForCompletionAsync(started.WorkflowInstanceId, timeout: TimeSpan.FromSeconds(30));

        Assert.IsFalse(state.IsFailed);
        state.AssertCompletedActivities("start", "seed", "compute", "end");
        state.AssertVariableEquals("x", "24");
        state.AssertVariableEquals("greeting", "hello 24");
    }

    [TestMethod]
    public async Task ThrowingJavaScript_FailsTheTaskWithCode500AndItsMessage()
    {
        await WaitForPluginAsync();
        var deployed = await ApiClient.DeployAsync(BpmnFixtureLoader.Load("02-script-tasks", "javascript-throws.bpmn"));

        var started = await ApiClient.StartAsync(deployed.ProcessDefinitionKey);
        var state = await ApiClient.WaitForStateAsync(started.WorkflowInstanceId, s => s.IsFailed, timeout: TimeSpan.FromSeconds(30));

        state.AssertNotCompleted("end");
        var error = state.CompletedActivities.Single(a => a.ActivityId == "compute").ErrorState;
        Assert.IsNotNull(error);
        Assert.AreEqual("500", error.Code);
        StringAssert.Contains(error.Message, "x is too large: 10");
    }

    private static async Task WaitForPluginAsync()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FLEANS_JAVASCRIPT_WORKER_PROJECT")))
            Assert.Inconclusive("FLEANS_JAVASCRIPT_WORKER_PROJECT is not set: the JavaScript plugin host is not running.");

        // The worker registers once its gRPC subscription is up, after fleans-core is healthy.
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(90);
        while (true)
        {
            using var response = await AspireFixture.ApiHttpClient.GetAsync($"/custom-tasks/{TaskType}");
            if (response.StatusCode == HttpStatusCode.OK)
                return;
            if (DateTimeOffset.UtcNow > deadline)
                Assert.Fail($"The JavaScript plugin never registered {TaskType}.");
            await Task.Delay(TimeSpan.FromMilliseconds(500));
        }
    }
}
