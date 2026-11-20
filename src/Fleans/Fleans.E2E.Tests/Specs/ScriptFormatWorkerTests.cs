using System.Net;
using Fleans.E2E.Tests.ApiClient;
using Fleans.E2E.Tests.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Fleans.E2E.Tests.Specs;

// Ports tests/manual/83-script-format-workers/test-plan.md (#1105). A script task in a format other
// than C# runs on the worker of fleans.script.<format>; Fleans.E2E.ExternalWorker serves the test
// format e2e-json, whose "script" is the JSON object of variables it writes.
[TestClass]
[TestCategory("E2E")]
public class ScriptFormatWorkerTests : WorkflowE2ETestBase
{
    private const string Folder = "83-script-format-workers";

    [TestMethod]
    public async Task ScriptInAWorkerFormat_RunsOnItsWorker_AndMergesWhatItWrites()
    {
        await WaitForWorkerAsync();
        var deployed = await ApiClient.DeployAsync(BpmnFixtureLoader.Load(Folder, "json-script.bpmn"));
        Assert.IsEmpty(deployed.Warnings ?? [], "A connected worker means no deploy warning.");

        var started = await ApiClient.StartAsync(deployed.ProcessDefinitionKey);
        var state = await ApiClient.WaitForCompletionAsync(started.WorkflowInstanceId, timeout: TimeSpan.FromSeconds(30));

        Assert.IsFalse(state.IsFailed);
        state.AssertCompletedActivities("start", "seed", "compute", "end");
        state.AssertVariableEquals("y", "20");
        state.AssertVariableEquals("label", "from-worker");
        state.AssertVariableEquals("x", "10");
        StringAssert.Contains(state.GetVariable("receivedVariables"), "x", "The worker gets every variable.");
    }

    [TestMethod]
    public async Task ScriptThatThrowsOnItsWorker_FailsTheTaskWithCode500AndTheMessage()
    {
        await WaitForWorkerAsync();
        var deployed = await ApiClient.DeployAsync(BpmnFixtureLoader.Load(Folder, "json-script-throws.bpmn"));

        var started = await ApiClient.StartAsync(deployed.ProcessDefinitionKey);
        var state = await ApiClient.WaitForStateAsync(started.WorkflowInstanceId, s => s.IsFailed, timeout: TimeSpan.FromSeconds(30));

        state.AssertNotCompleted("end");
        var error = state.CompletedActivities.Single(a => a.ActivityId == "compute").ErrorState;
        Assert.IsNotNull(error);
        Assert.AreEqual("500", error.Code);
        StringAssert.Contains(error.Message, "e2e script failed");
    }

    [TestMethod]
    public async Task ScriptFormatWithNoConnectedWorker_DeploysWithAWarning()
    {
        var deployed = await ApiClient.DeployAsync(BpmnFixtureLoader.Load(Folder, "missing-worker.bpmn"));

        var warning = (deployed.Warnings ?? []).Single();
        StringAssert.Contains(warning, "'compute'");
        StringAssert.Contains(warning, "fleans.script.e2e-nobody");
    }

    private static async Task WaitForWorkerAsync()
    {
        // The worker registers once its gRPC subscription is up, after fleans-core is healthy.
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(60);
        while (true)
        {
            using var response = await AspireFixture.ApiHttpClient.GetAsync("/custom-tasks/fleans.script.e2e-json");
            if (response.StatusCode == HttpStatusCode.OK)
                return;
            if (DateTimeOffset.UtcNow > deadline)
                Assert.Fail("The E2E worker never registered fleans.script.e2e-json.");
            await Task.Delay(TimeSpan.FromMilliseconds(500));
        }
    }
}
