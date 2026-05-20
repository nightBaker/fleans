using Fleans.E2E.Tests.ApiClient;
using Fleans.E2E.Tests.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Fleans.E2E.Tests.Specs;

// Ports tests/manual/39-rest-caller/test-plan.md (Scenario 1 — GET happy path).
//
// The RestCaller plugin is registered by Fleans.Api at startup
// (`services.AddRestCallerPlugin()` in Program.cs), so it's available in the
// in-process Aspire test cluster without any extra wiring. The HTTP target is a
// localhost echo server (AspireFixture.TestHttpServerBaseUrl) — using httpbin.org
// from CI would be flaky and add an external-network dependency.
//
// Scenarios 2 (POST + body), 3 (404 → boundary error), 4 (timeout), 5
// (idempotency-key) are deferred — they need fixture variants beyond what's checked
// into tests/manual/39-rest-caller.
[TestClass]
[TestCategory("E2E")]
public class RestCallerTests : WorkflowE2ETestBase
{
    [TestMethod]
    public async Task GetHappyPath_RestCallerCompletes_ApiStatus200()
    {
        var xml = BpmnFixtureLoader.Load("39-rest-caller", "rest-call.bpmn");
        var deployed = await ApiClient.DeployAsync(xml);

        var started = await ApiClient.StartAsync(
            deployed.ProcessDefinitionKey,
            variables: new Dictionary<string, object?>
            {
                ["apiUrl"] = $"{AspireFixture.TestHttpServerBaseUrl}/echo",
                ["apiHeaders"] = new Dictionary<string, object?>
                {
                    ["Accept"] = "application/json",
                },
            });

        // RestCaller HTTP call should complete within a few seconds against localhost.
        var state = await ApiClient.WaitForCompletionAsync(
            started.WorkflowInstanceId,
            timeout: TimeSpan.FromSeconds(15));

        state.AssertCompletedActivities("start", "callApi", "end");
        state.AssertVariableEquals("apiStatus", "200");
    }
}
