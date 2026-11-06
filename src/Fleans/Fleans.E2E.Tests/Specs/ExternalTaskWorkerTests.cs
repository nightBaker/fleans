using System.Net;
using System.Net.Http.Json;
using Fleans.E2E.Tests.ApiClient;
using Fleans.E2E.Tests.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Fleans.E2E.Tests.Specs;

// Ports tests/manual/77-external-task-grpc-worker/test-plan.md (Scenario 1). The AppHost routes
// e2e-ext-probe to external workers and runs Fleans.E2E.ExternalWorker, a gRPC worker that knows
// only the API's URL.
[TestClass]
[TestCategory("E2E")]
public class ExternalTaskWorkerTests : WorkflowE2ETestBase
{
    private const string TaskType = "e2e-ext-probe";

    [TestMethod]
    public async Task RoutedTask_RunsOnTheGrpcWorker_ListedInTheCatalog()
    {
        var entry = await WaitForCatalogEntryAsync();
        Assert.HasCount(1, entry.SiloNames, $"One external worker expected; got [{string.Join(", ", entry.SiloNames)}].");

        var xml = BpmnFixtureLoader.Load("77-external-task-grpc-worker", "external-probe.bpmn");
        var deployed = await ApiClient.DeployAsync(xml);
        var started = await ApiClient.StartAsync(deployed.ProcessDefinitionKey);

        var state = await ApiClient.WaitForCompletionAsync(started.WorkflowInstanceId, timeout: TimeSpan.FromSeconds(30));

        state.AssertCompletedActivities("start", "probe", "end");
        Assert.AreEqual(entry.SiloNames[0], state.GetVariable("probeWorker"),
            "The task must run on the worker the catalog lists.");
    }

    private sealed record CatalogEntry(string TaskType, string? DisplayName, List<string> SiloNames);

    private static async Task<CatalogEntry> WaitForCatalogEntryAsync()
    {
        // The worker registers once its gRPC subscription is up, after fleans-core is healthy.
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(60);
        while (true)
        {
            using var response = await AspireFixture.ApiHttpClient.GetAsync($"/custom-tasks/{TaskType}");
            if (response.StatusCode == HttpStatusCode.OK)
            {
                var entry = await response.Content.ReadFromJsonAsync<CatalogEntry>();
                if (entry is { SiloNames.Count: > 0 })
                {
                    return entry;
                }
            }
            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new TimeoutException(
                    $"External task '{TaskType}' never appeared in /custom-tasks (last status {(int)response.StatusCode}).");
            }
            await Task.Delay(TimeSpan.FromMilliseconds(500));
        }
    }
}
