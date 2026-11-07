using System.Net;
using Fleans.E2E.Tests.Infrastructure;
using Fleans.E2E.Tests.PageObjects;
using Microsoft.Playwright;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Microsoft.Playwright.Assertions;

namespace Fleans.E2E.Tests.Specs;

// Plugin versions and the input contract (#1051), through the fixture's gRPC worker: it announces
// e2e-ext-versioned as version 1.2.0 with a required integer delayMs and an optional list of integers.
[TestClass]
[TestCategory("E2E")]
public class CustomTaskVersionTests : WorkflowE2ETestBase
{
    private const string VersionedTask = "e2e-ext-versioned";
    private const string Version = "1.2.0";

    private static string Process(string key, string version, string inputs) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                     xmlns:fleans="https://fleans.io/schema/bpmn/1.0"
                     id="Definitions_1" targetNamespace="http://bpmn.io/schema/bpmn">
          <process id="{key}" isExecutable="true">
            <startEvent id="start" />
            <serviceTask id="custom">
              <extensionElements>
                <fleans:taskDefinition type="{VersionedTask}" {version} />
                <fleans:ioMapping>{inputs}</fleans:ioMapping>
              </extensionElements>
            </serviceTask>
            <endEvent id="end" />
            <sequenceFlow id="f1" sourceRef="start" targetRef="custom" />
            <sequenceFlow id="f2" sourceRef="custom" targetRef="end" />
          </process>
        </definitions>
        """;

    private async Task WaitForVersionedTaskAsync()
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
        var entry = await ApiClient.GetCustomTaskAsync(VersionedTask);
        while (entry is not { SiloNames.Count: > 0 } && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(250);
            entry = await ApiClient.GetCustomTaskAsync(VersionedTask);
        }
        Assert.IsNotNull(entry, $"{VersionedTask} must be in the catalog once the worker connects.");
        Assert.AreEqual(Version, entry.Versions?.Single().Version);
    }

    [TestMethod]
    public async Task Deploy_RejectsInputsThatDontFit_AndRunsAPinnedTaskWhoseInputsDo()
    {
        // Arrange
        await WaitForVersionedTaskAsync();
        var suffix = InlineBpmn.UniqueSuffix();

        // Act
        var rejected = await ApiClient.DeployRawAsync(Process(
            $"version-rejected-{suffix}", "", """<fleans:input source="30" target="delaysMs" />"""));

        // Assert — a typo names the input, and the required parameter is reported missing
        Assert.AreEqual(HttpStatusCode.BadRequest, rejected.StatusCode);
        var error = await rejected.Content.ReadAsStringAsync();
        StringAssert.Contains(error, "delaysMs");
        StringAssert.Contains(error, "delayMs");

        // Act
        var deployed = await ApiClient.DeployAsync(Process(
            $"version-pinned-{suffix}", $"version=\"{Version}\"", """<fleans:input source="1" target="delayMs" />"""));
        var started = await ApiClient.StartAsync(deployed.ProcessDefinitionKey);

        // Assert
        Assert.IsEmpty(deployed.Warnings ?? []);
        await ApiClient.WaitForCompletionAsync(started.WorkflowInstanceId);
    }

    [TestMethod]
    public async Task Deploy_WarnsWhenThePinnedVersionIsNotConnected()
    {
        // Arrange
        await WaitForVersionedTaskAsync();
        var xml = Process($"version-absent-{InlineBpmn.UniqueSuffix()}", "version=\"9.9.9\"", """<fleans:input source="=anything" target="notChecked" />""");

        // Act
        var deployed = await ApiClient.DeployAsync(xml);

        // Assert
        StringAssert.Contains(deployed.Warnings!.Single(), "9.9.9");
    }

    [TestMethod]
    public async Task Panel_ShowsTypesAndConnectedVersions_AndMarksAnUndeclaredInput()
    {
        // Arrange
        await WaitForVersionedTaskAsync();
        var editor = new EditorPage(Page);
        await editor.OpenAsync();
        await editor.LoadXmlAsync(Process(
            $"version-panel-{InlineBpmn.UniqueSuffix()}", "", """<fleans:input source="1" target="unknownInput" />"""));

        // Act
        await Page.Locator("[data-element-id='custom']").ClickAsync();

        // Assert — the picker offers the connected version, each parameter names its type
        var picker = Page.GetByTestId("plugin-version");
        await Expect(picker).ToBeVisibleAsync();
        await Expect(picker.Locator("fluent-option", new() { HasTextString = Version })).ToHaveCountAsync(1);
        await Expect(Page.Locator("[data-parameter-name='delayMs']").GetByTestId("parameter-type")).ToHaveTextAsync("integer");
        await Expect(Page.Locator("[data-parameter-name='codes']").GetByTestId("parameter-type")).ToHaveTextAsync("list of integer");
        await Expect(Page.GetByTestId("io-undeclared")).ToContainTextAsync("unknownInput");

        // Assert — an =expression fits an integer field
        var delay = Page.Locator("[data-parameter-name='delayMs'] fluent-text-field");
        await delay.Locator("input").FillAsync("=timeout");
        await Expect(delay.Locator("input")).ToHaveValueAsync("=timeout");
        await Expect(Page.Locator("[data-parameter-name='delayMs']")).Not.ToContainTextAsync("whole number or");
    }
}
