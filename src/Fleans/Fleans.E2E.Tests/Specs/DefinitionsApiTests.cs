using System.Net;
using Fleans.E2E.Tests.ApiClient;
using Fleans.E2E.Tests.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Fleans.E2E.Tests.Specs;

// Ports tests/manual/69-api-read-endpoints-negative-paths/test-plan.md (Definitions section)
// Covers DefinitionsController: GET list, GET {key}/instances, GET {key}/{version}/instances,
// and the deploy negative paths (malformed / unsupported BPMN, empty body).
[TestClass]
[TestCategory("E2E")]
public class DefinitionsApiTests : WorkflowE2ETestBase
{
    internal const string Plan = "69-api-read-endpoints-negative-paths";
    internal const string ReadFixture = "api-read-endpoints.bpmn";
    internal const string ReadKey = "api-read-endpoints";
    internal const string WaitActivity = "wait";

    [TestMethod]
    public async Task ListDefinitions_ContainsDeployedVersions_FilterSortAndPaging()
    {
        var xml = BpmnFixtureLoader.Load(Plan, ReadFixture);
        var first = await ApiClient.DeployAsync(xml);
        var second = await ApiClient.DeployAsync(xml);
        Assert.AreEqual(ReadKey, first.ProcessDefinitionKey);
        Assert.AreEqual(first.Version + 1, second.Version, "Redeploy must bump the version.");

        // Unfiltered list is non-empty and reports a total.
        var all = await ApiClient.ListDefinitionsAsync(pageSize: 100);
        Assert.IsGreaterThanOrEqualTo(2, all.TotalCount);
        Assert.AreEqual(1, all.Page);
        Assert.AreEqual(100, all.PageSize);

        // Filter by key + sort newest-first.
        var mine = await ApiClient.ListDefinitionsAsync(
            pageSize: 100, sorts: "-Version", filters: $"ProcessDefinitionKey=={ReadKey}");
        Assert.IsTrue(mine.Items.All(d => d.ProcessDefinitionKey == ReadKey),
            "Key filter must only return the requested key.");
        Assert.IsGreaterThanOrEqualTo(2, mine.TotalCount);
        var latest = mine.Items[0];
        Assert.AreEqual(second.Version, latest.Version, "-Version sort must put the newest version first.");
        Assert.IsTrue(latest.IsActive);
        Assert.AreEqual(3, latest.ActivitiesCount, "start + wait + end");
        Assert.AreEqual(2, latest.SequenceFlowsCount);
        Assert.IsFalse(string.IsNullOrEmpty(latest.ProcessDefinitionId));
        CollectionAssert.Contains(mine.Items.Select(d => d.Version).ToList(), first.Version);

        // Paging: pageSize=1 / page=2 returns the second-newest version.
        var page2 = await ApiClient.ListDefinitionsAsync(
            page: 2, pageSize: 1, sorts: "-Version", filters: $"ProcessDefinitionKey=={ReadKey}");
        Assert.HasCount(1, page2.Items);
        Assert.AreEqual(first.Version, page2.Items[0].Version);
        Assert.AreEqual(mine.TotalCount, page2.TotalCount);

        // Unknown key → empty page, not an error.
        var none = await ApiClient.ListDefinitionsAsync(filters: "ProcessDefinitionKey==no-such-key-766");
        Assert.AreEqual(0, none.TotalCount);
        Assert.IsEmpty(none.Items);
    }

    [TestMethod]
    public async Task ListInstances_ByKeyAndByVersion_ReturnStartedInstances()
    {
        var xml = BpmnFixtureLoader.Load(Plan, ReadFixture);
        var v1 = await ApiClient.DeployAsync(xml);
        var onV1 = await ApiClient.StartAsync(ReadKey);
        var v2 = await ApiClient.DeployAsync(xml);
        var onV2 = await ApiClient.StartAsync(ReadKey);

        // Wait for both instances to be projected and parked on the wait task.
        await ApiClient.WaitForStateAsync(onV1.WorkflowInstanceId, s => s.ActiveActivityIds.Contains(WaitActivity));
        await ApiClient.WaitForStateAsync(onV2.WorkflowInstanceId, s => s.ActiveActivityIds.Contains(WaitActivity));

        var byKey = await ApiClient.ListInstancesByKeyAsync(ReadKey, pageSize: 100);
        var byKeyIds = byKey.Items.Select(i => i.InstanceId).ToList();
        Assert.Contains(onV1.WorkflowInstanceId, byKeyIds);
        Assert.Contains(onV2.WorkflowInstanceId, byKeyIds);
        var row = byKey.Items.Single(i => i.InstanceId == onV2.WorkflowInstanceId);
        Assert.IsTrue(row.IsStarted);
        Assert.IsFalse(row.IsCompleted);
        Assert.IsFalse(row.IsCancelled);
        Assert.IsNotNull(row.CreatedAt);

        var byV1 = (await ApiClient.ListInstancesByKeyAndVersionAsync(ReadKey, v1.Version, pageSize: 100))
            .Items.Select(i => i.InstanceId).ToList();
        Assert.Contains(onV1.WorkflowInstanceId, byV1);
        Assert.DoesNotContain(onV2.WorkflowInstanceId, byV1, "v1 listing must not include a v2 instance.");

        var byV2 = (await ApiClient.ListInstancesByKeyAndVersionAsync(ReadKey, v2.Version, pageSize: 100))
            .Items.Select(i => i.InstanceId).ToList();
        Assert.Contains(onV2.WorkflowInstanceId, byV2);
        Assert.DoesNotContain(onV1.WorkflowInstanceId, byV2, "v2 listing must not include a v1 instance.");

        // The two rows carry different definition ids (one per version).
        var v1Row = byKey.Items.Single(i => i.InstanceId == onV1.WorkflowInstanceId);
        Assert.AreNotEqual(v1Row.ProcessDefinitionId, row.ProcessDefinitionId);

        // Sieve filter on the instance listing: completed instances only.
        using (var complete = await ApiClient.CompleteActivityAsync(onV1.WorkflowInstanceId, WaitActivity))
        {
            Assert.AreEqual(HttpStatusCode.OK, complete.StatusCode);
        }
        await ApiClient.WaitForCompletionAsync(onV1.WorkflowInstanceId);
        var completed = (await ApiClient.ListInstancesByKeyAsync(ReadKey, pageSize: 100, filters: "IsCompleted==true"))
            .Items.Select(i => i.InstanceId).ToList();
        Assert.Contains(onV1.WorkflowInstanceId, completed);
        Assert.DoesNotContain(onV2.WorkflowInstanceId, completed);

        // Unknown key / unknown version → empty page, not an error.
        Assert.AreEqual(0, (await ApiClient.ListInstancesByKeyAsync("no-such-key-766")).TotalCount);
        Assert.AreEqual(0, (await ApiClient.ListInstancesByKeyAndVersionAsync(ReadKey, 999_999)).TotalCount);

        // Clean up the still-running v2 instance.
        using var cleanup = await ApiClient.CompleteActivityAsync(onV2.WorkflowInstanceId, WaitActivity);
        Assert.AreEqual(HttpStatusCode.OK, cleanup.StatusCode);
    }

    [TestMethod]
    public async Task Deploy_MalformedXml_Returns400WithParserMessage()
    {
        using var response = await ApiClient.DeployRawAsync(BpmnFixtureLoader.Load(Plan, "malformed.bpmn"));
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        StringAssert.Contains(body, "Invalid BPMN");
        // The XML parser's own diagnostic is surfaced, so the author can find the fault.
        Assert.IsGreaterThan("{\"error\":\"Invalid BPMN: \"}".Length, body.Length,
            $"Rejection should carry the parser diagnostic; got: {body}");

        await AssertNotDeployedAsync("api-malformed");
    }

    [TestMethod]
    public async Task Deploy_UnsupportedBpmn_NoProcessElement_Returns400()
    {
        using var response = await ApiClient.DeployRawAsync(BpmnFixtureLoader.Load(Plan, "no-process.bpmn"));
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        StringAssert.Contains(body, "Invalid BPMN");
        StringAssert.Contains(body, "process element");
    }

    [TestMethod]
    public async Task Deploy_NotBpmnOrEmpty_Returns400()
    {
        using (var notXml = await ApiClient.DeployRawAsync("this is not xml"))
        {
            Assert.AreEqual(HttpStatusCode.BadRequest, notXml.StatusCode);
            StringAssert.Contains(await notXml.Content.ReadAsStringAsync(), "Invalid BPMN");
        }

        using (var empty = await ApiClient.DeployRawAsync("   "))
        {
            Assert.AreEqual(HttpStatusCode.BadRequest, empty.StatusCode);
            StringAssert.Contains(await empty.Content.ReadAsStringAsync(), "BpmnXml is required");
        }
    }

    [TestMethod]
    public async Task DisableEnable_UnknownKey_Returns404()
    {
        using (var disable = await ApiClient.DisableAsync("no-such-key-766"))
        {
            Assert.AreEqual(HttpStatusCode.NotFound, disable.StatusCode);
            StringAssert.Contains(await disable.Content.ReadAsStringAsync(), "not registered");
        }
        using (var enable = await ApiClient.EnableAsync("no-such-key-766"))
        {
            Assert.AreEqual(HttpStatusCode.NotFound, enable.StatusCode);
        }
    }

    private async Task AssertNotDeployedAsync(string key)
    {
        var defs = await ApiClient.ListDefinitionsAsync(filters: $"ProcessDefinitionKey=={key}");
        Assert.AreEqual(0, defs.TotalCount, $"A rejected deploy must not register '{key}'.");
    }
}
