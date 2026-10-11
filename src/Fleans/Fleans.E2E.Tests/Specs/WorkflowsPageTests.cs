using Fleans.E2E.Tests.Infrastructure;
using Fleans.E2E.Tests.PageObjects;
using Microsoft.Playwright;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Microsoft.Playwright.Assertions;

namespace Fleans.E2E.Tests.Specs;

// Admin UI smoke: /workflows (Workflows.razor) — definitions list, search, start, enable/disable. Issue #772.
[TestClass]
[TestCategory("E2E")]
public class WorkflowsPageTests : WorkflowE2ETestBase
{
    [TestMethod]
    public async Task DefinitionsList_ShowsLatestVersion_DisableEnableRoundTripsToApi()
    {
        var key = $"e2e-workflows-{InlineBpmn.UniqueSuffix()}";
        var xml = InlineBpmn.ScriptOnly(key);
        var first = await ApiClient.DeployAsync(xml);
        var latest = await ApiClient.DeployAsync(xml);

        var apiDefs = await ApiClient.ListDefinitionsAsync($"ProcessDefinitionKey=={key}");
        Assert.AreEqual(2, apiDefs.TotalCount);
        Assert.IsTrue(apiDefs.Items.All(d => d.IsActive));

        var page = new WorkflowsPage(Page);
        await page.OpenAsync();
        await page.SearchAsync(key);

        // Parent row = latest version; the older version is a collapsed (not rendered) child row.
        await Expect(page.Row(key)).ToHaveCountAsync(1);
        await Expect(page.VersionBadge(key)).ToHaveTextAsync($"v{latest.Version}");
        await Expect(page.AllRows(key)).ToHaveCountAsync(1);

        // Expanding the group reveals the older version; collapsing hides it again.
        await page.ToggleVersionsAsync(key);
        await Expect(page.VersionRow(key, first.Version)).ToBeVisibleAsync();
        await Expect(page.AllRows(key)).ToHaveCountAsync(apiDefs.TotalCount);
        await page.ToggleVersionsAsync(key);
        await Expect(page.AllRows(key)).ToHaveCountAsync(1);
        await Expect(page.DisabledBadge(key)).ToHaveCountAsync(0);
        await Expect(page.ActionButton(key, "Disable")).ToBeVisibleAsync();

        // Disable from the UI → API reports the key's latest version inactive
        // (Disable/Enable act on the latest version — ProcessDefinitionGrain semantics).
        await page.ClickActionAsync(key, "Disable");
        await Expect(page.SuccessMessage).ToContainTextAsync($"Disabled '{key}'.", new() { Timeout = 15_000 });
        await Expect(page.DisabledBadge(key)).ToBeVisibleAsync();
        await Expect(page.ActionButton(key, "Enable")).ToBeVisibleAsync();
        await Expect(page.ActionButton(key, "Start")).ToHaveAttributeAsync("disabled", string.Empty);
        Assert.IsFalse(await IsVersionActiveAsync(key, latest.Version), "Disable from UI must deactivate the latest version.");

        // Enable from the UI → API reports active again.
        await page.ClickActionAsync(key, "Enable");
        await Expect(page.SuccessMessage).ToContainTextAsync($"Enabled '{key}'.", new() { Timeout = 15_000 });
        await Expect(page.DisabledBadge(key)).ToHaveCountAsync(0);
        await Expect(page.ActionButton(key, "Disable")).ToBeVisibleAsync();
        Assert.IsTrue(await IsVersionActiveAsync(key, latest.Version), "Enable from UI must reactivate the latest version.");
    }

    private async Task<bool> IsVersionActiveAsync(string key, int version)
    {
        var defs = await ApiClient.ListDefinitionsAsync($"ProcessDefinitionKey=={key}");
        return defs.Items.Single(d => d.Version == version).IsActive;
    }

    [TestMethod]
    public async Task StartFromUi_CreatesInstanceVisibleViaApi()
    {
        var key = $"e2e-workflows-start-{InlineBpmn.UniqueSuffix()}";
        var deployed = await ApiClient.DeployAsync(InlineBpmn.ScriptOnly(key));

        var page = new WorkflowsPage(Page);
        await page.OpenAsync();
        await page.SearchAsync(key);

        await page.ClickActionAsync(key, "Start");
        await Expect(page.SuccessMessage).ToContainTextAsync(
            $"Started '{key}' v{deployed.Version}. Instance: ", new() { Timeout = 30_000 });

        // Parse the instance id from the banner and verify it against the API.
        var banner = await page.SuccessMessage.InnerTextAsync();
        var match = System.Text.RegularExpressions.Regex.Match(banner, @"Instance: ([0-9a-fA-F-]{36})");
        Assert.IsTrue(match.Success, $"Success banner should carry the instance id: '{banner}'.");
        var instanceId = Guid.Parse(match.Groups[1].Value);

        var state = await ApiClient.WaitForCompletionAsync(instanceId);
        state.AssertCompletedActivities("work");
        var instances = await ApiClient.ListInstancesAsync(key);
        Assert.AreEqual(1, instances.TotalCount);
        Assert.AreEqual(instanceId, instances.Items[0].InstanceId);

        // Instances action navigates to the per-version list, which shows the new instance.
        await page.ClickActionAsync(key, "Instances");
        await Expect(Page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex(
            $"/process-instances/{System.Text.RegularExpressions.Regex.Escape(key)}/{deployed.Version}$"));
        await BlazorPage.WaitForInteractiveAsync(Page);
        await new ProcessInstancesPage(Page).AssertInstanceStatusAsync(instanceId, "Completed");
    }
}
