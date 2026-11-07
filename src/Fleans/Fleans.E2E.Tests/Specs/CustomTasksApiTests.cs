using Fleans.E2E.Tests.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Fleans.E2E.Tests.Specs;

// Ports tests/manual/70-api-read-endpoints-negative-paths/ — Custom-task catalog section (manual plan retired — automated here)
// The engine bundles no custom tasks: every leg runs Fleans.E2E.ExternalWorker, whose `e2e-probe`
// handler is listed once its gRPC subscription is up.
[TestClass]
[TestCategory("E2E")]
public class CustomTasksApiTests : WorkflowE2ETestBase
{
    private const string Probe = "e2e-probe";

    [TestMethod]
    public async Task ListCustomTasks_ContainsProbeWithSchema()
    {
        // The worker registers after fleans-core is healthy; poll briefly rather than read once.
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        var entries = await ApiClient.GetCustomTasksAsync();
        while (entries.All(e => e.TaskType != Probe) && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(250);
            entries = await ApiClient.GetCustomTasksAsync();
        }

        var probe = entries.SingleOrDefault(e => e.TaskType == Probe);
        Assert.IsNotNull(probe, $"'{Probe}' missing from catalog: [{string.Join(",", entries.Select(e => e.TaskType))}].");
        Assert.AreEqual("E2E Probe", probe.DisplayName);
        Assert.IsNotEmpty(probe.SiloNames, "At least one worker must serve the task type.");
        Assert.AreEqual(probe.SiloNames.Count, probe.SiloNames.Distinct().Count(), "Worker names must be unique.");

        Assert.IsNotNull(probe.ParameterSchema);
        CollectionAssert.AreEquivalent(
            new[] { "delayMs", "failCode", "callbackUrl", "probeKey" },
            probe.ParameterSchema.Parameters.Select(p => p.Name).ToList());

        // Task types are unique in the catalog.
        Assert.AreEqual(entries.Count, entries.Select(e => e.TaskType).Distinct().Count());
    }

    [TestMethod]
    public async Task GetCustomTask_KnownType_MatchesListEntry_UnknownType_Returns404()
    {
        var single = await ApiClient.GetCustomTaskAsync(Probe);
        Assert.IsNotNull(single);
        Assert.AreEqual(Probe, single.TaskType);
        Assert.AreEqual("E2E Probe", single.DisplayName);

        var fromList = (await ApiClient.GetCustomTasksAsync()).Single(e => e.TaskType == Probe);
        CollectionAssert.AreEquivalent(fromList.SiloNames.ToList(), single.SiloNames.ToList());
        CollectionAssert.AreEqual(
            fromList.ParameterSchema!.Parameters.Select(p => p.Name).ToList(),
            single.ParameterSchema!.Parameters.Select(p => p.Name).ToList());

        Assert.IsNull(await ApiClient.GetCustomTaskAsync("no-such-task-type-766"),
            "Unknown task type must return 404.");
    }
}
