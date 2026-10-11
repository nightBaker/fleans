using Fleans.Application.QueryModels;
using Fleans.E2E.Tests.ApiClient;
using Fleans.E2E.Tests.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Fleans.E2E.Tests.Specs;

// Ports tests/manual/72-definition-versioning/test-plan.md
//
// The E2E stack persists to a shared SQLite file that survives across runs, so every test
// rewrites the fixture process ids with a per-run suffix. That keeps deployed versions
// deterministic (first deploy == v1) and the per-key instance listings free of rows from
// earlier runs.
[TestClass]
[TestCategory("E2E")]
public class DefinitionVersioningTests : WorkflowE2ETestBase
{
    private const string PlanFolder = "72-definition-versioning";
    private const string TaskUser = "versioning-e2e-user";

    [TestMethod]
    public async Task RunningInstancesFinishOnTheirOwnVersion_NewStartsUseLatest()
    {
        var key = $"versioned-order-{Guid.NewGuid():N}"[..24];
        var v1Xml = LoadWithKey("versioned-order-v1.bpmn", ("versioned-order", key));
        var v2Xml = LoadWithKey("versioned-order-v2.bpmn", ("versioned-order", key));

        // 1. Deploy v1 and start two instances that block on the 'approve' user task.
        var v1 = await ApiClient.DeployAsync(v1Xml);
        Assert.AreEqual(key, v1.ProcessDefinitionKey);
        Assert.AreEqual(1, v1.Version);

        var a = (await ApiClient.StartAsync(key)).WorkflowInstanceId;
        var b = (await ApiClient.StartAsync(key)).WorkflowInstanceId;
        await WaitForActive(a, "approve");
        await WaitForActive(b, "approve");

        // 2. Deploy v2 of the same key with a different flow (same 'approve' id).
        var v2 = await ApiClient.DeployAsync(v2Xml);
        Assert.AreEqual(key, v2.ProcessDefinitionKey);
        Assert.AreEqual(2, v2.Version);

        // 4a. A new start after the v2 deploy runs on v2.
        var c = (await ApiClient.StartAsync(key)).WorkflowInstanceId;
        var cBlocked = await WaitForActive(c, "approve");
        cBlocked.AssertCompletedActivities("start", "v2Prepare");
        cBlocked.AssertNotCompleted("v1Prepare");

        // 3. Complete the v1 instances — they must finish on the v1 graph.
        await CompleteUserTask(a, "approve");
        await CompleteUserTask(b, "approve");
        foreach (var id in new[] { a, b })
        {
            var final = await ApiClient.WaitForCompletionAsync(id);
            final.AssertCompletedActivities("start", "v1Prepare", "approve", "v1Finish", "v1End");
            final.AssertNotCompleted("v2Prepare", "v2Finish", "v2Audit", "v2End");
            final.AssertVariableEquals("preparedBy", "v1");
            final.AssertVariableEquals("finishedBy", "v1");
            Assert.IsFalse(final.TryGetVariable("audited", out _), "v1 instance must not run v2Audit.");
        }

        // 4b. The v2 instance finishes on the v2 graph.
        await CompleteUserTask(c, "approve");
        var cFinal = await ApiClient.WaitForCompletionAsync(c);
        cFinal.AssertCompletedActivities("start", "v2Prepare", "approve", "v2Finish", "v2Audit", "v2End");
        cFinal.AssertNotCompleted("v1Prepare", "v1Finish", "v1End");
        cFinal.AssertVariableEquals("finishedBy", "v2");
        cFinal.AssertVariableEquals("audited", "True");

        // Each instance stays bound to the definition id it started on.
        var aState = await ApiClient.GetStateAsync(a);
        var bState = await ApiClient.GetStateAsync(b);
        Assert.IsNotNull(aState?.ProcessDefinitionId);
        Assert.AreEqual(aState.ProcessDefinitionId, bState?.ProcessDefinitionId);
        Assert.AreNotEqual(aState.ProcessDefinitionId, cFinal.ProcessDefinitionId,
            "v2 instance must be bound to a different process-definition id than the v1 instances.");

        // 4c. Listings: per-version and per-key.
        var v1Page = await ApiClient.WaitForInstancesPageAsync(
            t => ApiClient.GetInstancesPageByKeyAndVersionAsync(key, 1, ct: t),
            p => SameIds(p, a, b) && p.Items.All(i => i.IsCompleted),
            $"{key} v1 lists exactly [a, b], completed");
        Assert.AreEqual(2, v1Page.TotalCount);
        Assert.IsTrue(v1Page.Items.All(i => i.ProcessDefinitionId == aState.ProcessDefinitionId));

        var v2Page = await ApiClient.WaitForInstancesPageAsync(
            t => ApiClient.GetInstancesPageByKeyAndVersionAsync(key, 2, ct: t),
            p => SameIds(p, c) && p.Items.All(i => i.IsCompleted),
            $"{key} v2 lists exactly [c], completed");
        Assert.AreEqual(1, v2Page.TotalCount);
        Assert.AreEqual(cFinal.ProcessDefinitionId, v2Page.Items[0].ProcessDefinitionId);

        var allPage = await ApiClient.WaitForInstancesPageAsync(
            t => ApiClient.GetInstancesPageByKeyAsync(key, ct: t),
            p => SameIds(p, a, b, c),
            $"{key} lists exactly [a, b, c] across versions");
        Assert.AreEqual(3, allPage.TotalCount);

        // A version that was never deployed lists nothing.
        var v3Page = await ApiClient.GetInstancesPageByKeyAndVersionAsync(key, 3);
        Assert.AreEqual(0, v3Page.TotalCount);
    }

    [TestMethod]
    public async Task CallActivity_ResolvesLatestChildVersionAtCallTime_InFlightChildKeepsItsVersion()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var childKey = $"versioned-child-{suffix}";
        var parentKey = $"versioned-parent-{suffix}";
        var childV1Xml = LoadWithKey("versioned-child-v1.bpmn", ("versioned-child", childKey));
        var childV2Xml = LoadWithKey("versioned-child-v2.bpmn", ("versioned-child", childKey));
        var parentXml = LoadWithKey("versioned-parent.bpmn",
            ("versioned-child", childKey), ("versioned-parent", parentKey));

        var childV1 = await ApiClient.DeployAsync(childV1Xml);
        Assert.AreEqual(1, childV1.Version);
        await ApiClient.DeployAsync(parentXml);

        // Parent P1 calls the child while only v1 exists → child C1 runs v1 and blocks on childHold.
        var p1 = (await ApiClient.StartAsync(parentKey)).WorkflowInstanceId;
        await CompleteUserTask(p1, "gate");
        await WaitForActive(p1, "callChild");
        var childV1Page = await ApiClient.WaitForInstancesPageAsync(
            t => ApiClient.GetInstancesPageByKeyAndVersionAsync(childKey, 1, ct: t),
            p => p.Items.Count == 1,
            $"{childKey} v1 lists the child spawned by P1");
        var c1 = childV1Page.Items[0].InstanceId;
        var c1Blocked = await WaitForActive(c1, "childHold");
        c1Blocked.AssertCompletedActivities("childStart", "childV1Work");

        // Parent P2 starts while child v1 is still latest, and holds before its call activity.
        var p2 = (await ApiClient.StartAsync(parentKey)).WorkflowInstanceId;
        await WaitForActive(p2, "gate");

        // Deploy child v2 while P2 is in flight and C1 is mid-execution.
        var childV2 = await ApiClient.DeployAsync(childV2Xml);
        Assert.AreEqual(2, childV2.Version);

        // P2 reaches the call activity after the deploy → resolves to the LATEST child version (v2),
        // not the version that was latest when P2 started. Documented behaviour: no pinning.
        await CompleteUserTask(p2, "gate");
        var p2Final = await ApiClient.WaitForCompletionAsync(p2);
        p2Final.AssertCompletedActivities("parentStart", "gate", "callChild", "parentEnd");
        p2Final.AssertVariableEquals("childResult", "child-v2");

        var childV2Page = await ApiClient.WaitForInstancesPageAsync(
            t => ApiClient.GetInstancesPageByKeyAndVersionAsync(childKey, 2, ct: t),
            p => p.Items.Count == 1 && p.Items[0].IsCompleted,
            $"{childKey} v2 lists the completed child spawned by P2");
        var c2Final = await ApiClient.WaitForCompletionAsync(childV2Page.Items[0].InstanceId);
        c2Final.AssertCompletedActivities("childStart", "childV2Work", "childV2End");
        c2Final.AssertNotCompleted("childV1Work", "childHold", "childV1End");

        // The in-flight v1 child keeps its version and finishes on the v1 graph.
        await CompleteUserTask(c1, "childHold");
        var c1Final = await ApiClient.WaitForCompletionAsync(c1);
        c1Final.AssertCompletedActivities("childStart", "childV1Work", "childHold", "childV1End");
        c1Final.AssertNotCompleted("childV2Work", "childV2End");
        Assert.AreEqual(childV1Page.Items[0].ProcessDefinitionId, c1Final.ProcessDefinitionId);

        var p1Final = await ApiClient.WaitForCompletionAsync(p1);
        p1Final.AssertCompletedActivities("parentStart", "gate", "callChild", "parentEnd");
        p1Final.AssertVariableEquals("childResult", "child-v1");

        // Per-key child listing spans both versions; v1 still lists only C1.
        var childAll = await ApiClient.WaitForInstancesPageAsync(
            t => ApiClient.GetInstancesPageByKeyAsync(childKey, ct: t),
            p => SameIds(p, c1, childV2Page.Items[0].InstanceId),
            $"{childKey} lists both children across versions");
        Assert.AreEqual(2, childAll.TotalCount);
        var childV1After = await ApiClient.GetInstancesPageByKeyAndVersionAsync(childKey, 1);
        Assert.IsTrue(SameIds(childV1After, c1), "v1 child listing must still contain only C1.");
    }

    private static string LoadWithKey(string fileName, params (string From, string To)[] replacements)
    {
        var xml = BpmnFixtureLoader.Load(PlanFolder, fileName);
        foreach (var (from, to) in replacements)
        {
            xml = xml.Replace(from, to, StringComparison.Ordinal);
        }
        return xml;
    }

    private static bool SameIds(PagedResult<WorkflowInstanceInfo> page, params Guid[] expected) =>
        page.Items.Select(i => i.InstanceId).OrderBy(g => g).SequenceEqual(expected.OrderBy(g => g));

    private Task<InstanceStateSnapshot> WaitForActive(Guid instanceId, string activityId) =>
        ApiClient.WaitForStateAsync(instanceId, s => s.ActiveActivityIds.Contains(activityId));

    private async Task CompleteUserTask(Guid instanceId, string activityId)
    {
        var state = await WaitForActive(instanceId, activityId);
        var taskId = state.ActiveActivities.First(x => x.ActivityId == activityId).ActivityInstanceId;

        using (var claim = await ApiClient.ClaimUserTaskAsync(taskId, TaskUser))
        {
            Assert.IsTrue(claim.IsSuccessStatusCode,
                $"Claim of '{activityId}' on {instanceId} failed: {claim.StatusCode} {await claim.Content.ReadAsStringAsync()}");
        }
        using (var complete = await ApiClient.CompleteUserTaskAsync(taskId, TaskUser, new Dictionary<string, object?>()))
        {
            Assert.IsTrue(complete.IsSuccessStatusCode,
                $"Complete of '{activityId}' on {instanceId} failed: {complete.StatusCode} {await complete.Content.ReadAsStringAsync()}");
        }
    }
}
