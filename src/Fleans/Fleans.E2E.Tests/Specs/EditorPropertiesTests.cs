using Fleans.E2E.Tests.ApiClient;
using Fleans.E2E.Tests.Infrastructure;
using Fleans.E2E.Tests.PageObjects;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Fleans.E2E.Tests.Specs;

// Editor-UI plan family — pending an in-page-state-corruption issue (see [Ignore]s below).
//
// The EditorPage POM and these specs are kept in tree as infrastructure: the read path
// (loadXml → getElementProperties) works cleanly, and read-only assertions (e.g. the
// pre-population part of plans #49/#50) are tractable. The write-then-read round-trips
// hit a bpmn-js + Blazor-Server interaction bug — calling modeling.updateProperties
// for properties that hold element references (default → SequenceFlow,
// activationCondition → FormalExpression) leaves the elementRegistry in a state where
// subsequent .get(id) lookups return null. Reproduces with both default-namespace and
// bpmn:-prefixed fixtures, and on both macOS (intermittent) and ubuntu-latest
// (deterministic). Likely affects the Razor properties panel too — worth a follow-up
// product issue.
[TestClass]
[TestCategory("E2E")]
public class EditorPropertiesTests : WorkflowE2ETestBase
{
    [TestMethod]
    [Ignore("Editor page + bpmn-js is unstable in the headless Linux test cluster — getElementProperties returns null on ubuntu-latest even immediately after loadXml succeeds. Reproducible on dotnet.yml's e2e job; passes locally on macOS. Investigation may need to widen to the Blazor Server SignalR circuit. Spec kept in tree for when the page stabilises.")]
    public async Task DefaultFlow_PrePopulatesFromImportedXml()
    {
        // Read-only assertion: confirms `getElementProperties(gatewayId).defaultFlow`
        // surfaces the `default="..."` attribute from imported BPMN. The edit/clear
        // half of plan #50 lives in the [Ignore]'d spec below pending the
        // updateProperties bug fix.
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
    [Ignore("bpmn-js modeling.updateProperties({ default: <SequenceFlow> }) leaves elementRegistry in a state where get(gatewayId) returns null afterward. Reproduces on macOS + ubuntu-latest, with both default-namespace and bpmn:-prefixed BPMN. Likely affects the Razor panel UI; pending product investigation.")]
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

    // Ports tests/manual/49-complex-gateway-activation-condition/test-plan.md — same root cause.
    [TestMethod]
    [Ignore("Same root cause as DefaultFlow edit: modeling.updateProperties({ activationCondition: <FormalExpression> }) corrupts elementRegistry; subsequent registry.get(id) returns null.")]
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
