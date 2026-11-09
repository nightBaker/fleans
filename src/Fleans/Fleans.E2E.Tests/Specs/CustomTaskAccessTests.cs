using System.Net;
using Fleans.Application.CustomTasks;
using Fleans.E2E.Tests.Infrastructure;
using Fleans.E2E.Tests.PageObjects;
using Fleans.ServiceDefaults.DTOs;
using Microsoft.Playwright;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Microsoft.Playwright.Assertions;

namespace Fleans.E2E.Tests.Specs;

// Plugin category and private access (#1022), through the fixture's gRPC worker: it announces the
// private e2e-ext-private task (category E2E) beside the public, uncategorized e2e-ext-probe.
[TestClass]
[TestCategory("E2E")]
public class CustomTaskAccessTests : WorkflowE2ETestBase
{
    private const string PrivateTask = "e2e-ext-private";
    private const string PublicTask = "e2e-ext-probe";

    private static string ProcessUsing(string key, string taskType) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                     xmlns:zeebe="http://camunda.org/schema/zeebe/1.0"
                     id="Definitions_1" targetNamespace="http://bpmn.io/schema/bpmn">
          <process id="{key}" isExecutable="true">
            <startEvent id="start" />
            <serviceTask id="custom"><extensionElements><zeebe:taskDefinition type="{taskType}" /></extensionElements></serviceTask>
            <endEvent id="end" />
            <sequenceFlow id="f1" sourceRef="start" targetRef="custom" />
            <sequenceFlow id="f2" sourceRef="custom" targetRef="end" />
          </process>
        </definitions>
        """;

    private async Task<CustomTaskCatalogEntryDto> WaitForPrivateTaskAsync()
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
        var entry = await ApiClient.GetCustomTaskAsync(PrivateTask);
        while (entry is not { SiloNames.Count: > 0 } && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(250);
            entry = await ApiClient.GetCustomTaskAsync(PrivateTask);
        }
        Assert.IsNotNull(entry, $"{PrivateTask} must be in the catalog once the worker connects.");
        return entry;
    }

    [TestMethod]
    public async Task PrivateTask_DeployIsRejectedUntilTheAllowListIsEditedInTheCatalogPage()
    {
        // Arrange
        var entry = await WaitForPrivateTaskAsync();
        Assert.AreEqual("E2E", entry.Category);
        Assert.AreEqual(CustomTaskVisibility.Private, entry.Visibility);
        var key = $"private-access-{InlineBpmn.UniqueSuffix()}";
        var xml = ProcessUsing(key, PrivateTask);

        // Act + Assert — not on the allow-list
        var rejected = await ApiClient.DeployRawAsync(xml);
        Assert.AreEqual(HttpStatusCode.BadRequest, rejected.StatusCode);
        var error = await rejected.Content.ReadAsStringAsync();
        StringAssert.Contains(error, PrivateTask);
        StringAssert.Contains(error, key);

        // Act — allow it in the UI
        var catalog = new CustomTaskCatalogPage(Page);
        await catalog.OpenAsync();
        await Expect(catalog.Cells(PrivateTask).Nth(2)).ToHaveTextAsync("E2E");
        await Expect(catalog.Cells(PrivateTask).Nth(3)).ToHaveTextAsync("Private");
        await Page.GetByTestId($"custom-task-edit-allowed-{PrivateTask}").ClickAsync();
        var input = Page.GetByTestId("custom-task-allow-list-input").Locator("textarea");
        await input.FillAsync(key);
        await Page.GetByTestId("custom-task-allow-list-save").ClickAsync();

        // Assert — saved, and the deploy now passes
        await Expect(Page.GetByTestId("custom-task-allow-list-dialog")).ToHaveCountAsync(0);
        await Expect(catalog.Cells(PrivateTask).Nth(4)).ToContainTextAsync(key);
        var deployed = await ApiClient.DeployAsync(xml);
        Assert.AreEqual(key, deployed.ProcessDefinitionKey);
    }

    [TestMethod]
    public async Task Editor_GroupsPluginsByCategory_AndOffersAPrivateOneOnlyToAnAllowedProcess()
    {
        // Arrange — the API replaces the allow-list, so other runs' keys do not matter here
        await WaitForPrivateTaskAsync();
        var allowed = $"editor-allowed-{InlineBpmn.UniqueSuffix()}";
        await ApiClient.SetAllowedProcessesAsync(PrivateTask, [allowed]);
        var privateOption = Page.Locator("fluent-option", new() { HasTextString = $"({PrivateTask})" });
        var publicOption = Page.Locator("fluent-option", new() { HasTextString = $"({PublicTask})" });
        var groups = Page.GetByTestId("plugin-group");

        // Act + Assert — allowed: both plugins, each under its group, named categories first
        await CustomTaskLocalization.OpenServiceTaskAsync(Page, PublicTask, allowed);
        await Expect(privateOption).ToHaveCountAsync(1);
        await Expect(publicOption).ToHaveCountAsync(1);
        await Expect(groups.First).ToHaveTextAsync("E2E");
        await Expect(groups.Last).ToHaveTextAsync("Other");
        await Expect(Page.GetByTestId("plugin-not-allowed")).ToHaveCountAsync(0);

        // Act + Assert — another process: hidden, and a task already using it gets a warning
        await CustomTaskLocalization.OpenServiceTaskAsync(Page, PrivateTask, $"editor-other-{InlineBpmn.UniqueSuffix()}");
        await Expect(publicOption).ToHaveCountAsync(1);
        await Expect(privateOption).ToHaveCountAsync(0);
        await Expect(Page.GetByTestId("plugin-not-allowed")).ToContainTextAsync(PrivateTask);
    }
}
