using System.Net;
using Fleans.E2E.Tests.ApiClient;
using Fleans.E2E.Tests.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Fleans.E2E.Tests.Specs;

// Ports tests/manual/70-api-read-endpoints-negative-paths/ — Execution section (manual plan retired — automated here)
// Negative paths on ExecutionController: start an unknown / disabled process key, and
// complete-activity for an unknown instance, an unknown activity, and an already-completed
// activity.
[TestClass]
[TestCategory("E2E")]
public class ExecutionApiTests : WorkflowE2ETestBase
{
    private const string DisableFixture = "api-disable-start.bpmn";
    private const string DisableKey = "api-disable-start";

    [TestMethod]
    public async Task Start_UnknownProcessKey_Returns404()
    {
        using var response = await ApiClient.StartRawAsync("no-such-process-766");
        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        StringAssert.Contains(body, "no-such-process-766");
        StringAssert.Contains(body, "not registered");
    }

    [TestMethod]
    public async Task Start_MissingWorkflowId_Returns400()
    {
        using var response = await ApiClient.StartRawAsync("  ");
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        StringAssert.Contains(await response.Content.ReadAsStringAsync(), "WorkflowId is required");
    }

    [TestMethod]
    public async Task Start_DisabledProcess_Returns409_ThenSucceedsAfterEnable()
    {
        await ApiClient.DeployAsync(BpmnFixtureLoader.Load(DefinitionsApiTests.Plan, DisableFixture));
        try
        {
            using (var disable = await ApiClient.DisableAsync(DisableKey))
            {
                Assert.AreEqual(HttpStatusCode.OK, disable.StatusCode);
            }

            using (var rejected = await ApiClient.StartRawAsync(DisableKey))
            {
                Assert.AreEqual(HttpStatusCode.Conflict, rejected.StatusCode);
                var body = await rejected.Content.ReadAsStringAsync();
                StringAssert.Contains(body, "disabled");
            }

            // The rejected start must not have created an instance.
            var instances = await ApiClient.ListInstancesByKeyAsync(DisableKey, pageSize: 100);
            var before = instances.TotalCount;

            using (var enable = await ApiClient.EnableAsync(DisableKey))
            {
                Assert.AreEqual(HttpStatusCode.OK, enable.StatusCode);
            }

            var started = await ApiClient.StartAsync(DisableKey);
            var final = await ApiClient.WaitForCompletionAsync(started.WorkflowInstanceId);
            final.AssertCompletedActivities("start", "end");

            // The listing is projected asynchronously (#861): wait for the started instance.
            var after = await ApiClient.WaitForInstancesPageAsync(
                t => ApiClient.ListInstancesByKeyAsync(DisableKey, pageSize: 100, ct: t),
                p => p.Items.Any(i => i.InstanceId == started.WorkflowInstanceId), "the post-enable instance");
            Assert.AreEqual(before + 1, after.TotalCount, "Only the post-enable start creates an instance.");
        }
        finally
        {
            // Never leave the key disabled (redeploys inherit the disabled flag).
            using var _ = await ApiClient.EnableAsync(DisableKey);
        }
    }

    [TestMethod]
    public async Task Start_WithBusinessId_IsOnStateAndFilterableInInstanceList()
    {
        await ApiClient.DeployAsync(BpmnFixtureLoader.Load(DefinitionsApiTests.Plan, DefinitionsApiTests.ReadFixture));
        var businessId = $"ORDER-{Guid.NewGuid():N}";

        var started = await ApiClient.StartAsync(DefinitionsApiTests.ReadKey, businessId: businessId);

        var state = await ApiClient.WaitForStateAsync(started.WorkflowInstanceId,
            s => s.ActiveActivityIds.Contains(DefinitionsApiTests.WaitActivity));
        Assert.AreEqual(businessId, state.BusinessId);

        var page = await ApiClient.WaitForInstancesPageAsync(
            t => ApiClient.ListInstancesByKeyAsync(DefinitionsApiTests.ReadKey, filters: $"BusinessId=={businessId}", ct: t),
            p => p.TotalCount >= 1, "the instance with the business id");
        Assert.AreEqual(1, page.TotalCount);
        Assert.AreEqual(started.WorkflowInstanceId, page.Items[0].InstanceId);
        Assert.AreEqual(businessId, page.Items[0].BusinessId);

        // Don't leave a parked instance behind for other specs listing this key.
        using var complete = await ApiClient.CompleteActivityAsync(started.WorkflowInstanceId, DefinitionsApiTests.WaitActivity);
        Assert.AreEqual(HttpStatusCode.OK, complete.StatusCode);
    }

    [TestMethod]
    public async Task InstanceList_BusinessIdQueryParameter_MatchesSieveMetacharactersAndRejectsPadding()
    {
        await ApiClient.DeployAsync(BpmnFixtureLoader.Load(DefinitionsApiTests.Plan, DefinitionsApiTests.ReadFixture));
        // `,` and `|` would split a Sieve filters term; the query parameter must bind them verbatim.
        var businessId = $"ACME, Inc|{Guid.NewGuid():N}";
        var started = await ApiClient.StartAsync(DefinitionsApiTests.ReadKey, businessId: businessId);

        var page = await ApiClient.WaitForInstancesPageAsync(
            t => ApiClient.ListInstancesByBusinessIdAsync(DefinitionsApiTests.ReadKey, businessId, t),
            p => p.TotalCount >= 1, "the instance with the business id");
        Assert.AreEqual(started.WorkflowInstanceId, page.Items.Single().InstanceId);

        // A whitespace-only value binds to null (no filter); a padded one reaches validation.
        using var padded = await ApiClient.ListInstancesByBusinessIdRawAsync(DefinitionsApiTests.ReadKey, " ORDER-1");
        Assert.AreEqual(HttpStatusCode.BadRequest, padded.StatusCode);

        using var complete = await ApiClient.CompleteActivityAsync(started.WorkflowInstanceId, DefinitionsApiTests.WaitActivity);
        Assert.AreEqual(HttpStatusCode.OK, complete.StatusCode);
    }

    [TestMethod]
    public async Task Start_OverlongBusinessId_Returns400()
    {
        using var response = await ApiClient.StartRawAsync(
            DefinitionsApiTests.ReadKey, businessId: new string('x', 257));
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        StringAssert.Contains(await response.Content.ReadAsStringAsync(), "BusinessId");
    }

    [TestMethod]
    public async Task CompleteActivity_UnknownInstance_Returns404()
    {
        var unknown = Guid.NewGuid();
        using var response = await ApiClient.CompleteActivityAsync(unknown, "wait");
        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
        StringAssert.Contains(await response.Content.ReadAsStringAsync(), unknown.ToString());

        // The probe must not have materialised an instance.
        Assert.IsNull(await ApiClient.GetStateAsync(unknown));
    }

    [TestMethod]
    public async Task CompleteActivity_UnknownAndAlreadyCompletedActivity_Return409()
    {
        await ApiClient.DeployAsync(BpmnFixtureLoader.Load(DefinitionsApiTests.Plan, DefinitionsApiTests.ReadFixture));
        var started = await ApiClient.StartAsync(DefinitionsApiTests.ReadKey);
        var instanceId = started.WorkflowInstanceId;
        await ApiClient.WaitForStateAsync(instanceId, s => s.ActiveActivityIds.Contains(DefinitionsApiTests.WaitActivity));

        // Activity id that doesn't exist in the definition.
        using (var unknown = await ApiClient.CompleteActivityAsync(instanceId, "no-such-activity"))
        {
            Assert.AreEqual(HttpStatusCode.Conflict, unknown.StatusCode);
            StringAssert.Contains(await unknown.Content.ReadAsStringAsync(), "no-such-activity");
        }

        // Activity that exists but is not active (start event already completed).
        using (var notActive = await ApiClient.CompleteActivityAsync(instanceId, "start"))
        {
            Assert.AreEqual(HttpStatusCode.Conflict, notActive.StatusCode);
        }

        // Rejections must not disturb the running instance.
        var mid = await ApiClient.GetStateAsync(instanceId);
        Assert.IsNotNull(mid);
        Assert.Contains(DefinitionsApiTests.WaitActivity, mid.ActiveActivityIds);
        Assert.IsFalse(mid.IsCompleted);

        // Happy path, then a second completion of the same activity → 409.
        using (var ok = await ApiClient.CompleteActivityAsync(instanceId, DefinitionsApiTests.WaitActivity))
        {
            Assert.AreEqual(HttpStatusCode.OK, ok.StatusCode);
        }
        var final = await ApiClient.WaitForCompletionAsync(instanceId);
        final.AssertCompletedActivities("start", DefinitionsApiTests.WaitActivity, "end");

        using (var again = await ApiClient.CompleteActivityAsync(instanceId, DefinitionsApiTests.WaitActivity))
        {
            Assert.AreEqual(HttpStatusCode.Conflict, again.StatusCode);
            StringAssert.Contains(await again.Content.ReadAsStringAsync(), "No active entry");
        }

        // State unchanged by the rejected repeat — the activity completed exactly once.
        var after = await ApiClient.GetStateAsync(instanceId);
        Assert.IsNotNull(after);
        Assert.AreEqual(1, after.CompletedActivities.Count(a => a.ActivityId == DefinitionsApiTests.WaitActivity));
    }

    [TestMethod]
    public async Task CompleteActivity_MissingFields_Returns400()
    {
        using (var noInstance = await ApiClient.CompleteActivityAsync(Guid.Empty, "wait"))
        {
            Assert.AreEqual(HttpStatusCode.BadRequest, noInstance.StatusCode);
        }
        using (var noActivity = await ApiClient.CompleteActivityAsync(Guid.NewGuid(), " "))
        {
            Assert.AreEqual(HttpStatusCode.BadRequest, noActivity.StatusCode);
        }
    }
}
