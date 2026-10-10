using Fleans.E2E.Tests.ApiClient;
using Fleans.E2E.Tests.Infrastructure;
using Fleans.E2E.Tests.PageObjects;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Fleans.E2E.Tests.Specs;

// Editor-UI plan family — model-level property round-trips driven through the
// window.bpmnEditor.* JS API (see EditorPage).
//
// History (#773): these specs were [Ignore]'d because getElementProperties returned null
// after loadXml / updateProperties. The cause was not bpmn-js registry corruption but a
// boot race: EditorPage.OpenAsync used to wait only for window.bpmnEditor._modeler, while
// Editor.razor's OnAfterRenderAsync was still running (AddBlankTab → newDiagram → loadXml),
// so the blank diagram overwrote the test's import. OpenAsync now waits for
// data-editor-ready="true", rendered only after the boot sequence finishes.
[TestClass]
[TestCategory("E2E")]
public class EditorPropertiesTests : WorkflowE2ETestBase
{
    [TestMethod]
    public async Task DefaultFlow_PrePopulatesFromImportedXml()
    {
        // Read-only assertion: confirms `getElementProperties(gatewayId).defaultFlow`
        // surfaces the `default="..."` attribute from imported BPMN. The edit/clear
        // half of plan #50 lives in the spec below.
        var xml = BpmnFixtureLoader.Load(
            "03-exclusive-gateway", "conditional-branching.bpmn");

        var editor = new EditorPage(Page);
        await editor.OpenAsync();
        await editor.LoadXmlAsync(xml);

        var initial = await editor.GetDefaultFlowAsync("gateway");
        Assert.AreEqual("defaultFlow", initial,
            "Default flow should pre-populate from the imported BPMN.");
    }

    // Ports tests/manual/50-gateway-default-flow/test-plan.md — edit/clear half.
    [TestMethod]
    public async Task DefaultFlow_EditAndClear_RoundTripsThroughExclusiveGateway()
    {
        var xml = BpmnFixtureLoader.Load(
            "03-exclusive-gateway", "conditional-branching.bpmn");

        var editor = new EditorPage(Page);
        await editor.OpenAsync();
        await editor.LoadXmlAsync(xml);

        await editor.UpdateDefaultFlowAsync("gateway", "conditionalFlow");
        var edited = await editor.GetDefaultFlowAsync("gateway");
        Assert.AreEqual("conditionalFlow", edited);

        await editor.UpdateDefaultFlowAsync("gateway", null);
        var cleared = await editor.GetDefaultFlowAsync("gateway");
        Assert.AreEqual(string.Empty, cleared);
    }

    // Ports tests/manual/49-complex-gateway-activation-condition/test-plan.md — model-level write/read/clear.
    [TestMethod]
    public async Task ActivationCondition_WriteReadClear_RoundTripsThroughComplexGateway()
    {
        var xml = BpmnFixtureLoader.Load(
            "20-complex-gateway", "join-activation-condition.bpmn");

        var editor = new EditorPage(Page);
        await editor.OpenAsync();
        await editor.LoadXmlAsync(xml);

        await editor.UpdateActivationConditionAsync("join", "_context._nroftoken >= 2");
        var written = await editor.GetActivationConditionAsync("join");
        Assert.AreEqual("_context._nroftoken >= 2", written);

        await editor.UpdateActivationConditionAsync("join", null);
        var cleared = await editor.GetActivationConditionAsync("join");
        Assert.AreEqual(string.Empty, cleared);
    }
}
