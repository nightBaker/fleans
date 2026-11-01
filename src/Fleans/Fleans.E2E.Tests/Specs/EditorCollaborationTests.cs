using Fleans.E2E.Tests.ApiClient;
using Fleans.E2E.Tests.Infrastructure;
using Microsoft.Playwright;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Microsoft.Playwright.Assertions;

namespace Fleans.E2E.Tests.Specs;

// Editor collaboration (Editor.Collaboration.cs + IEditorPresenceGrain): presence across two browser
// sessions, deploy-conflict detection, and the deploy dialog's "deploy disabled" option.
[TestClass]
[TestCategory("E2E")]
public class EditorCollaborationTests : WorkflowE2ETestBase
{
    private static async Task<IPage> OpenEditorAsync(IPage page, string key, int version)
    {
        await page.GotoAsync($"/editor/{key}/{version}");
        await page.Locator(".editor-page[data-editor-ready='true']").WaitForAsync(new() { Timeout = 30_000 });
        await Expect(page.GetByTestId("editor-empty-state")).ToBeHiddenAsync(new() { Timeout = 30_000 });
        return page;
    }

    [TestMethod]
    [DataRow(1280, 720)]
    [DataRow(1920, 1080)]
    public async Task SecondEditor_SeesPresence_AndDeployAfterAnotherDeployReportsTheConflict(int width, int height)
    {
        await Page.SetViewportSizeAsync(width, height);
        var key = "e2e-collab-" + InlineBpmn.UniqueSuffix();
        var xml = BpmnFixtureLoader.Load("18-user-task", "user-task-approval.bpmn").Replace("user-task-approval", key);
        await ApiClient.DeployAsync(xml);

        await using var otherContext = await AspireFixture.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true,
            BaseURL = AspireFixture.WebBaseUri.ToString(),
            ViewportSize = new ViewportSize { Width = width, Height = height },
        });
        var mine = await OpenEditorAsync(Page, key, 1);
        var theirs = await OpenEditorAsync(await otherContext.NewPageAsync(), key, 1);
        await Expect(theirs.Locator("[data-element-id='review']")).ToBeVisibleAsync();
        // This spec checks selection propagation and deploy conflicts, not canvas hit-testing.
        // The floating assistant can cover the task. The public focus API raises the same
        // selection.changed -> OnElementSelected callback as a click, without that dependency.
        Assert.IsTrue(await theirs.EvaluateAsync<bool>("() => window.bpmnEditor.focusElement('review')"));

        // Presence: heartbeats every 5 s, so allow a couple of rounds.
        await Expect(mine.GetByTestId("editor-presence").Locator(".orbit-avatar")).ToHaveCountAsync(1, new() { Timeout = 20_000 });
        await Expect(mine.Locator(".orbit-presence-chip")).ToHaveCountAsync(1, new() { Timeout = 20_000 });

        // Someone else deploys v2 while "mine" still edits v1.
        await ApiClient.DeployAsync(xml);
        await mine.Locator(".editor-actions fluent-button", new() { HasTextString = "Deploy" }).ClickAsync();
        await Expect(mine.GetByTestId("deploy-conflict")).ToContainTextAsync("v2 was deployed after you opened v1");

        // Deploy anyway, disabled: the dialog reports the new version and its disabled state.
        await mine.Locator(".deploy-dialog fluent-checkbox").ClickAsync();
        await mine.Locator(".deploy-dialog fluent-button", new() { HasTextString = "Deploy mine anyway" }).ClickAsync();
        var dialog = mine.GetByTestId("deploy-dialog");
        await Expect(dialog).ToContainTextAsync($"Deployed {key} v3", new() { Timeout = 15_000 });
        await Expect(dialog).ToContainTextAsync("Deployed disabled");
    }
}
