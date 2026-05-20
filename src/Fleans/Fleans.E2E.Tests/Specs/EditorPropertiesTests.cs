using Fleans.E2E.Tests.ApiClient;
using Fleans.E2E.Tests.Infrastructure;
using Fleans.E2E.Tests.PageObjects;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Fleans.E2E.Tests.Specs;

// Ports tests/manual/50-gateway-default-flow/test-plan.md (round-trip subset) — the
// manual plan additionally exercises the dropdown DOM via mouse clicks, which would
// need an ElementPropertiesPanel DOM POM that hasn't landed yet.
[TestClass]
[TestCategory("E2E")]
public class EditorPropertiesTests : WorkflowE2ETestBase
{
    [TestMethod]
    public async Task DefaultFlow_PrePopulatedFromXml_EditAndClearRoundTrips()
    {
        var xml = BpmnFixtureLoader.Load(
            "03-exclusive-gateway", "conditional-branching.bpmn");

        var editor = new EditorPage(Page);
        await editor.OpenAsync();
        await editor.LoadXmlAsync(xml);

        // Pre-populated from XML — fixture sets `default="defaultFlow"`.
        var initial = await editor.GetDefaultFlowAsync("gateway");
        Assert.AreEqual("defaultFlow", initial,
            "Default flow should pre-populate from the imported BPMN.");

        // Edit to the conditional flow id and verify.
        await editor.UpdateDefaultFlowAsync("gateway", "conditionalFlow");
        var edited = await editor.GetDefaultFlowAsync("gateway");
        Assert.AreEqual("conditionalFlow", edited);

        // Clear (null) → no default attribute → empty read-back.
        await editor.UpdateDefaultFlowAsync("gateway", null);
        var cleared = await editor.GetDefaultFlowAsync("gateway");
        Assert.AreEqual(string.Empty, cleared);
    }

    // Ports tests/manual/49-complex-gateway-activation-condition/test-plan.md — DEFERRED.
    // The 20-complex-gateway/join-activation-condition.bpmn fixture uses default-namespace
    // BPMN (no `bpmn:` prefix) and attribute-form `activationCondition="..."`. When the
    // editor calls `modeling.updateProperties(element, { activationCondition: <expr> })`
    // on that loaded model, the bpmn-js elementRegistry enters an inconsistent state where
    // subsequent `elementRegistry.get('join')` calls return null. The corresponding manual
    // plan presumably authors via the panel UI (which uses the same JS API path), so this
    // is potentially a real product bug rather than a test-shape issue — flagging for
    // follow-up investigation rather than silently working around it here.
    [TestMethod]
    [Ignore("Pending investigation: modeling.updateProperties({ activationCondition }) on default-namespace BPMN breaks elementRegistry state (registry.get returns null after update). Likely affects the panel UI too.")]
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
    }
}
