using Fleans.Application.CustomTasks;
using Fleans.E2E.Tests.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Fleans.E2E.Tests.Specs;

// Ports tests/manual/69-api-read-endpoints-negative-paths/test-plan.md (Custom-task catalog section)
// The engine-bundled RestCaller plugin ("rest-call") registers itself in the catalog on silo
// startup, so it is always present in the Aspire test cluster.
[TestClass]
[TestCategory("E2E")]
public class CustomTasksApiTests : WorkflowE2ETestBase
{
    private const string RestCall = "rest-call";

    [TestMethod]
    public async Task ListCustomTasks_ContainsRestCallerWithSchema()
    {
        // Catalog registration happens during silo startup; poll briefly rather than read once.
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        var entries = await ApiClient.GetCustomTasksAsync();
        while (entries.All(e => e.TaskType != RestCall) && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(250);
            entries = await ApiClient.GetCustomTasksAsync();
        }

        var restCall = entries.SingleOrDefault(e => e.TaskType == RestCall);
        Assert.IsNotNull(restCall, $"'{RestCall}' missing from catalog: [{string.Join(",", entries.Select(e => e.TaskType))}].");
        Assert.AreEqual("REST Caller", restCall.DisplayName);
        Assert.IsNotEmpty(restCall.SiloNames, "At least one silo must host the plugin.");
        Assert.AreEqual(restCall.SiloNames.Count, restCall.SiloNames.Distinct().Count(), "Silo names must be unique.");

        Assert.IsNotNull(restCall.ParameterSchema);
        var url = restCall.ParameterSchema.Parameters.SingleOrDefault(p => p.Name == "url");
        Assert.IsNotNull(url);
        Assert.IsTrue(url.Required);
        Assert.AreEqual(CustomTaskParameterType.String, url.Type);

        var method = restCall.ParameterSchema.Parameters.Single(p => p.Name == "method");
        Assert.AreEqual("GET", method.DefaultValue);

        var headers = restCall.ParameterSchema.Parameters.Single(p => p.Name == "headers");
        Assert.AreEqual(CustomTaskParameterType.Map, headers.Type);
        Assert.AreEqual(CustomTaskParameterType.String, headers.ItemType);

        // Task types are unique in the catalog.
        Assert.AreEqual(entries.Count, entries.Select(e => e.TaskType).Distinct().Count());
    }

    [TestMethod]
    public async Task GetCustomTask_KnownType_MatchesListEntry_UnknownType_Returns404()
    {
        var single = await ApiClient.GetCustomTaskAsync(RestCall);
        Assert.IsNotNull(single);
        Assert.AreEqual(RestCall, single.TaskType);
        Assert.AreEqual("REST Caller", single.DisplayName);

        var fromList = (await ApiClient.GetCustomTasksAsync()).Single(e => e.TaskType == RestCall);
        CollectionAssert.AreEquivalent(fromList.SiloNames.ToList(), single.SiloNames.ToList());
        CollectionAssert.AreEqual(
            fromList.ParameterSchema!.Parameters.Select(p => p.Name).ToList(),
            single.ParameterSchema!.Parameters.Select(p => p.Name).ToList());

        Assert.IsNull(await ApiClient.GetCustomTaskAsync("no-such-task-type-766"),
            "Unknown task type must return 404.");
    }
}
