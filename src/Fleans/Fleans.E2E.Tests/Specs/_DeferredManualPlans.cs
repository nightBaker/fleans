using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Fleans.E2E.Tests.Specs;

/// <summary>
/// Placeholder specs for manual plans whose automation requires infrastructure beyond
/// the current scaffolding (BPMN editor automation, custom-task plugin host,
/// Docker-compose-only flows, etc.). Each method below documents what the manual
/// plan asserts and is [Ignore]'d with a clear reason. Activating one of these requires
/// the underlying capability to land first.
/// </summary>
[TestClass]
[TestCategory("E2E")]
public class DeferredManualPlans : WorkflowE2ETestBase_None
{
    // tests/manual/27-instance-state-endpoint/test-plan.md — already implicitly covered
    // (every passing spec exercises /Instances/{id}/state).

    // tests/manual/27-load-testing-infra/test-plan.md — Docker Compose + nginx fan-out.
    // OUT OF SCOPE per the original plan.

    // tests/manual/28-api-auth/test-plan.md — Scenario B (401 without token, 200 with a valid
    // token, wrong audience / garbage token → 401, /health anonymous) is automated in
    // AuthenticationTests (E2E-Auth category, FLEANS_E2E_AUTH=true → Keycloak). Scenario A
    // (auth disabled) is implicitly covered by every default-leg spec.

    // Editor plans (29, 38, 47, 48, 52, 54, 59): the EditorPage POM is now reliable — #773
    // was a boot race (OpenAsync returned before Editor.razor's OnAfterRenderAsync finished
    // loading its blank/restored tab), fixed by waiting on data-editor-ready="true". Model-level
    // round-trips via window.bpmnEditor.* are therefore unblocked; each plan below still needs
    // its own spec (and, for panel-DOM assertions, an ElementPropertiesPanel POM).

    // tests/manual/29-editor-tabs/test-plan.md — BPMN editor multi-tab UI.
    [TestMethod]
    [Ignore("Needs EditorPage page object + bpmn-js drag-drop integration (deferred Phase 4 follow-up).")]
    public void Plan29_EditorTabs_OpenCloseDirtyTracking() { }

    // tests/manual/30-web-auth/test-plan.md — Scenario 2 (anonymous visit → Keycloak login →
    // back on the deep link with "Signed in as alice") is automated in
    // AuthenticationTests.Web_RedirectsToIssuer_LoginEstablishesSession. Scenarios 3–5
    // (dashboard guard, open-redirect table, antiforgery logout) remain manual.

    // tests/manual/31-events-page/test-plan.md — automated in EventsPageTests
    // (steps 2 empty-state and 7 OIDC remain manual).

    // tests/manual/35-kafka-streaming/test-plan.md — OUT OF SCOPE per plan (silo kill).

    // tests/manual/37-custom-task-framework/test-plan.md — Scenario 1 (unregistered
    // plugin: activity stays Active, manual complete unblocks) is automated in
    // CustomTaskFrameworkTests. Scenario 2 (registered plugin auto-completes) and
    // Scenario 3 (multi-silo catalog reconcile) remain manual.

    // tests/manual/38-custom-task-editor/test-plan.md — editor properties panel
    // for custom-task service tasks.
    [TestMethod]
    [Ignore("Needs EditorPage POM + plugin catalog assertion.")]
    public void Plan38_CustomTaskEditor_ParameterSchemaRoundTrip() { }

    // tests/manual/41-nuget-publish/test-plan.md — release pipeline. OUT OF SCOPE.
    // tests/manual/42-release-pipeline/test-plan.md — release pipeline. OUT OF SCOPE.

    // tests/manual/44-azure-queue-streaming/test-plan.md — automated: the E2E-Smoke subset
    // runs against Azurite in dotnet.yml's e2e-providers (azurequeue) leg.

    // tests/manual/44-stream-sharding-parallelism/test-plan.md — config inspection +
    // throughput observation. Not browser-automatable in this scaffolding.
    [TestMethod]
    [Ignore("Configuration + throughput observation; not a workflow-level assertion.")]
    public void Plan44_StreamShardingParallelism_QueueCountTunable() { }

    // tests/manual/47-event-subprocess-editor/test-plan.md — editor UI for event sub-process.
    [TestMethod]
    [Ignore("Needs EditorPage POM + property-panel inspection.")]
    public void Plan47_EventSubprocessEditor_PanelFields() { }

    // tests/manual/48-io-mapping-editor/test-plan.md — add/edit/remove through the panel, deploy
    // and run, plus call-activity child-vs-ioMapping separation, are automated in
    // EditorIoMappingTests (#950). Per-element visibility, zeebe-prefix reuse and the read-only
    // instance view stay manual.
    [TestMethod]
    [Ignore("Residual steps (per-element visibility, zeebe-prefix reuse, read-only view) need panel-DOM checks per element kind.")]
    public void Plan48_IoMappingEditor_Residual() { }

    // tests/manual/49-complex-gateway-activation-condition/test-plan.md — the model-level
    // write/read/clear round-trip is automated in
    // EditorPropertiesTests.ActivationCondition_WriteReadClear_RoundTripsThroughComplexGateway
    // (un-ignored by #773). The panel-DOM half still needs an ElementPropertiesPanel POM.

    // tests/manual/50-gateway-default-flow/test-plan.md — the model-level round-trip is
    // automated in EditorPropertiesTests.DefaultFlow_PrePopulatesFromImportedXml and
    // DefaultFlow_EditAndClear_RoundTripsThroughExclusiveGateway (un-ignored by #773). The
    // panel-DOM half (dropdown population + click-to-select) still needs an
    // ElementPropertiesPanel POM.

    // tests/manual/52-compensation-editor/test-plan.md — editor UI compensation panel.
    [TestMethod]
    [Ignore("Needs EditorPage POM (compensation activityRef + waitForCompletion fields).")]
    public void Plan52_CompensationEditor_PanelFields() { }

    // tests/manual/54-multi-event-editor/test-plan.md — editor UI multi-event panel.
    [TestMethod]
    [Ignore("Needs EditorPage POM (multi-event definitions panel).")]
    public void Plan54_MultiEventEditor_PanelFields() { }

    // tests/manual/41-placement-role-mismatch/test-plan.md — the PlacementRoleAssertion it
    // targets never runs (#787). Role mismatch failing loudly is automated via the explicit
    // Fleans:Role validation in SplitRoleTopologyTests.MisroledSilo_RefusesToStart_… (E2E-SplitRoles).

    // tests/manual/55-plugin-host-isolation/test-plan.md — Scenario 3 (external plugin runs
    // only on the plugin- silo) and the role-validation negatives are automated in
    // SplitRoleTopologyTests (E2E-SplitRoles leg). Scenarios 1–2 (Orleans Dashboard placement
    // of ScriptExecutorGrain / plugin handler grains) remain manual.

    // tests/manual/56-streaming-queue-count-config/test-plan.md — config option round-trip.
    [TestMethod]
    [Ignore("Configuration option (Fleans:Streaming:Redis:TotalQueueCount); not a workflow assertion.")]
    public void Plan56_StreamingQueueCountConfig_RoundTrip() { }

    // tests/manual/58-custom-task-cancellation/test-plan.md — the boundary-timer interrupt of a
    // long-running plugin task is automated in SplitRoleTopologyTests (E2E-SplitRoles); token
    // propagation on interrupt is an engine gap (#786, spec [Ignore]'d there).
    [TestMethod]
    [Ignore("Needs a controlled silo kill/restart mid-plugin-call and EventId 4050/4030 log assertions; the split-roles leg's plugin host is shared by other specs.")]
    public void Plan58_CustomTaskCancellation_DeactivationRedelivery() { }

    // tests/manual/59-custom-task-output-mapping-editor/test-plan.md — editor UI for plugin outputs.
    [TestMethod]
    [Ignore("Needs EditorPage POM + custom-task output mapping panel.")]
    public void Plan59_CustomTaskOutputMappingEditor() { }

    // tests/manual/61-usertask-group-claim/test-plan.md — automated in UserTaskLifecycleTests
    // (auth-disabled body-groups path). The JWT half of plan 61 (groups resolved from the
    // token's `groups` claim; body-supplied groups ignored, so a spoofing caller gets 409) is
    // automated in AuthenticationTests.UserTaskClaim_UsesJwtGroups_BodySuppliedGroupsIgnored.

    // tests/manual/62-chart-streaming-providers/test-plan.md — Helm chart tests. OUT OF SCOPE.
    // tests/manual/63-chart-external-postgres/test-plan.md — Helm chart tests. OUT OF SCOPE.
}

// Stub base class to avoid forcing every [Ignore] method through the full Aspire boot.
// Tests above do not call any infrastructure; this class breaks the inheritance chain.
public abstract class WorkflowE2ETestBase_None
{
}
