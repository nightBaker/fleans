using Fleans.E2E.Tests.Infrastructure;
using Fleans.E2E.Tests.PageObjects;
using Microsoft.Playwright;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Microsoft.Playwright.Assertions;

namespace Fleans.E2E.Tests.Specs;

// Admin UI smoke: /process-instances/{key}[/{version}] (ProcessInstances.razor) — issue #772.
[TestClass]
[TestCategory("E2E")]
public class ProcessInstancesPageTests : WorkflowE2ETestBase
{
    [TestMethod]
    public async Task InstancesList_ShowsStatusPerInstance_AndFiltersByVersion()
    {
        // Arrange — one key, two versions; v1 has a completed and a running instance, v2 one running.
        var suffix = InlineBpmn.UniqueSuffix();
        var key = $"e2e-instances-{suffix}";
        var messageName = $"e2e-instances-msg-{suffix}";
        var xml = InlineBpmn.MessageCatch(key, messageName);

        var v1 = await ApiClient.DeployAsync(xml);
        var completed = await ApiClient.StartAsync(key, new() { ["requestId"] = $"done-{suffix}" });
        var running = await ApiClient.StartAsync(key, new() { ["requestId"] = $"wait-{suffix}" });
        await ApiClient.WaitForStateAsync(completed.WorkflowInstanceId, s => s.ActiveActivityIds.Contains("waitMessage"));
        await ApiClient.WaitForStateAsync(running.WorkflowInstanceId, s => s.ActiveActivityIds.Contains("waitMessage"));
        await ApiClient.SendMessageAsync(messageName, $"done-{suffix}");
        await ApiClient.WaitForCompletionAsync(completed.WorkflowInstanceId);

        var v2 = await ApiClient.DeployAsync(xml);
        Assert.AreEqual(v1.Version + 1, v2.Version, "Redeploying the same key must create a new version.");
        var runningV2 = await ApiClient.StartAsync(key, new() { ["requestId"] = $"v2-{suffix}" });
        await ApiClient.WaitForStateAsync(runningV2.WorkflowInstanceId, s => s.ActiveActivityIds.Contains("waitMessage"));

        // API state is the source of truth the page must mirror.
        var apiAll = await ApiClient.ListInstancesAsync(key);
        var apiV1 = await ApiClient.ListInstancesAsync(key, v1.Version);
        var apiV2 = await ApiClient.ListInstancesAsync(key, v2.Version);
        Assert.AreEqual(3, apiAll.TotalCount);
        Assert.AreEqual(2, apiV1.TotalCount);
        Assert.AreEqual(1, apiV2.TotalCount);
        Assert.IsTrue(apiAll.Items.Single(i => i.InstanceId == completed.WorkflowInstanceId).IsCompleted);

        var page = new ProcessInstancesPage(Page);

        // Act + Assert — all versions.
        await page.OpenAsync(key);
        await Expect(page.Title($"Instances — {key} (all versions)")).ToBeVisibleAsync();
        await Expect(page.Rows).ToHaveCountAsync(apiAll.TotalCount, new() { Timeout = 15_000 });
        await page.AssertInstanceStatusAsync(completed.WorkflowInstanceId, "Completed");
        await page.AssertInstanceStatusAsync(running.WorkflowInstanceId, "Running");
        await page.AssertInstanceStatusAsync(runningV2.WorkflowInstanceId, "Running");

        // Version filter: v1 only.
        await page.OpenAsync(key, v1.Version);
        await Expect(page.Title($"Instances — {key} v{v1.Version}")).ToBeVisibleAsync();
        await Expect(page.Rows).ToHaveCountAsync(apiV1.TotalCount, new() { Timeout = 15_000 });
        await page.AssertInstanceStatusAsync(completed.WorkflowInstanceId, "Completed");
        await page.AssertInstanceStatusAsync(running.WorkflowInstanceId, "Running");
        await Expect(page.Row(runningV2.WorkflowInstanceId)).ToHaveCountAsync(0);

        // Version filter: v2 only.
        await page.OpenAsync(key, v2.Version);
        await Expect(page.Rows).ToHaveCountAsync(apiV2.TotalCount, new() { Timeout = 15_000 });
        await page.AssertInstanceStatusAsync(runningV2.WorkflowInstanceId, "Running");

        // View navigates to the instance details page.
        await page.ViewInstanceAsync(runningV2.WorkflowInstanceId);
        await Expect(Page).ToHaveURLAsync(
            new System.Text.RegularExpressions.Regex($"/process-instance/{runningV2.WorkflowInstanceId:D}$"));
    }
}
