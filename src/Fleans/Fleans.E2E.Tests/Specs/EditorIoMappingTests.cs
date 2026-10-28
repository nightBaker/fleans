using Fleans.E2E.Tests.ApiClient;
using Fleans.E2E.Tests.Infrastructure;
using Fleans.E2E.Tests.PageObjects;
using Microsoft.Playwright;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Microsoft.Playwright.Assertions;

namespace Fleans.E2E.Tests.Specs;

// #950 — the properties panel's Input/Output mapping section (fleans:ioMapping), driven through
// the real panel UI rather than window.bpmnEditor.*, then deployed from the editor.
[TestClass]
[TestCategory("E2E")]
public class EditorIoMappingTests : WorkflowE2ETestBase
{
    [TestMethod]
    public async Task ScriptTask_InputAndOutputAddedInPanel_DeployedInstanceGetsMappedValues()
    {
        var key = "e2e-iomap-" + InlineBpmn.UniqueSuffix();
        var xml = InlineBpmn.ScriptOnly(key)
            .Replace("_context.done = true", "_context.doubled = _context.seed * 2");

        var editor = new EditorPage(Page);
        await editor.OpenAsync();
        await editor.LoadXmlAsync(xml);

        await Page.Locator("[data-element-id='work']").ClickAsync();
        var section = Page.GetByTestId("io-mapping-section");
        await Expect(section).ToBeVisibleAsync();

        await section.GetByTestId("io-add-input").ClickAsync();
        await FillRowAsync(section.GetByTestId("io-input-row"), source: "=amount", target: "seed");
        await section.GetByTestId("io-add-output").ClickAsync();
        await FillRowAsync(section.GetByTestId("io-output-row"), source: "=doubled", target: "result");
        await Expect(section.GetByTestId("io-error")).ToHaveCountAsync(0);

        // Deploy from the editor's own dialog.
        await Page.Locator(".editor-actions fluent-button", new() { HasTextString = "Deploy" }).ClickAsync();
        await Page.GetByTestId("deploy-confirm").ClickAsync();
        await Expect(Page.GetByTestId("deploy-dialog")).ToContainTextAsync($"Deployed {key} v1", new() { Timeout = 15_000 });

        var started = await ApiClient.StartAsync(key, new Dictionary<string, object?> { ["amount"] = 21 });
        var state = await ApiClient.WaitForCompletionAsync(started.WorkflowInstanceId);

        Assert.AreEqual("42", state.GetVariable("result"));
        Assert.IsFalse(state.TryGetVariable("seed", out _), "Inputs stay local to the script task.");
        Assert.IsFalse(state.TryGetVariable("doubled", out _), "With outputs, only mapped outputs reach the process scope.");
    }

    [TestMethod]
    public async Task CallActivity_ChildMappingsAndIoMappingAreEditedSeparately()
    {
        var key = "e2e-iomap-call-" + InlineBpmn.UniqueSuffix();
        var xml = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                         xmlns:fleans="https://fleans.io/schema/bpmn/1.0"
                         id="Definitions_1" targetNamespace="http://bpmn.io/schema/bpmn">
              <process id="{key}" isExecutable="true">
                <startEvent id="start" />
                <callActivity id="call" calledElement="child">
                  <extensionElements>
                    <fleans:inputMapping source="orderId" target="childOrderId" />
                    <fleans:ioMapping>
                      <fleans:input source="=1" target="first" />
                      <fleans:input source="=2" target="second" />
                    </fleans:ioMapping>
                  </extensionElements>
                </callActivity>
                <endEvent id="end" />
                <sequenceFlow id="f1" sourceRef="start" targetRef="call" />
                <sequenceFlow id="f2" sourceRef="call" targetRef="end" />
              </process>
            </definitions>
            """;

        var editor = new EditorPage(Page);
        await editor.OpenAsync();
        await editor.LoadXmlAsync(xml);
        await Page.Locator("[data-element-id='call']").ClickAsync();

        var child = Page.GetByTestId("child-mapping-section");
        var io = Page.GetByTestId("io-mapping-section");
        await Expect(child.GetByTestId("child-input-row")).ToHaveCountAsync(1);
        await Expect(io.GetByTestId("io-input-row")).ToHaveCountAsync(2);

        // Removing the first ioMapping input must not touch the child-process mapping.
        await io.GetByTestId("io-input-row").First.GetByTestId("io-remove").ClickAsync();
        await Expect(io.GetByTestId("io-input-row")).ToHaveCountAsync(1);
        await Expect(child.GetByTestId("child-input-row")).ToHaveCountAsync(1);

        var saved = await editor.GetXmlAsync();
        StringAssert.Contains(saved, "target=\"childOrderId\"");
        StringAssert.Contains(saved, "target=\"second\"");
        Assert.IsFalse(saved.Contains("target=\"first\""), "The removed ioMapping input should be gone.");
    }

    // Each field commits on change (blur); the next fill waits until the panel has re-rendered
    // from the list the JS call returned, so the row is never edited mid-round-trip.
    private static async Task FillRowAsync(ILocator row, string source, string target)
    {
        var sourceInput = row.GetByTestId("io-source").Locator("input");
        await sourceInput.FillAsync(source);
        await sourceInput.BlurAsync();
        await Expect(row.GetByTestId("io-error")).ToContainTextAsync("Target must be a variable name");

        var targetInput = row.GetByTestId("io-target").Locator("input");
        await targetInput.FillAsync(target);
        await targetInput.BlurAsync();
        await Expect(row.GetByTestId("io-error")).ToHaveCountAsync(0);
    }
}
