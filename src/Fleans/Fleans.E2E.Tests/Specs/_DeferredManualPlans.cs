using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Fleans.E2E.Tests.Specs;

/// <summary>
/// Placeholder specs for manual plans whose automation requires infrastructure beyond
/// the current scaffolding (BPMN editor automation, custom-task plugin host, OIDC/auth
/// stub, Docker-compose-only flows, etc.). Each method below documents what the manual
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

    // tests/manual/28-api-auth/test-plan.md — JWT bearer auth.
    [TestMethod]
    [Ignore("Needs OIDC/JWT test setup (Authority + ClientId + token-issuer container).")]
    public void Plan28_ApiAuth_JwtBearerEnforced() { }

    // tests/manual/29-editor-tabs/test-plan.md — BPMN editor multi-tab UI.
    [TestMethod]
    [Ignore("Needs EditorPage page object + bpmn-js drag-drop integration (deferred Phase 4 follow-up).")]
    public void Plan29_EditorTabs_OpenCloseDirtyTracking() { }

    // tests/manual/30-web-auth/test-plan.md — Blazor Web app auth gate.
    [TestMethod]
    [Ignore("Needs OIDC/JWT test setup for Fleans.Web.")]
    public void Plan30_WebAuth_LoginRequired() { }

    // tests/manual/31-events-page/test-plan.md — /events page in Web UI.
    [TestMethod]
    [Ignore("Needs EventsPage POM; UI assertion on Fluent DataGrid filtering.")]
    public void Plan31_EventsPage_FilterAndDisplay() { }

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

    // tests/manual/39-rest-caller/test-plan.md — Scenario 1 (GET happy path against
    // an in-process echo server) is automated in RestCallerTests. Scenarios 2 (POST),
    // 3 (404 → boundary error), 4 (timeout), 5 (idempotency-key) need extra fixtures.

    // tests/manual/41-nuget-publish/test-plan.md — release pipeline. OUT OF SCOPE.
    // tests/manual/42-release-pipeline/test-plan.md — release pipeline. OUT OF SCOPE.

    // tests/manual/44-azure-queue-streaming/test-plan.md — Azurite container.
    [TestMethod]
    [Ignore("Needs Azurite emulator container in the test cluster.")]
    public void Plan44_AzureQueueStreaming_BasicSmoke() { }

    // tests/manual/44-stream-sharding-parallelism/test-plan.md — config inspection +
    // throughput observation. Not browser-automatable in this scaffolding.
    [TestMethod]
    [Ignore("Configuration + throughput observation; not a workflow-level assertion.")]
    public void Plan44_StreamShardingParallelism_QueueCountTunable() { }

    // tests/manual/45-user-task-fail-cancel/test-plan.md — fail/cancel user task.
    [TestMethod]
    [Ignore("Needs fail-task + cancel-task DTO + client helpers; lifecycle covered partially by Plan18.")]
    public void Plan45_UserTaskFailCancel_TerminalStatesTransition() { }

    // tests/manual/47-event-subprocess-editor/test-plan.md — editor UI for event sub-process.
    [TestMethod]
    [Ignore("Needs EditorPage POM + property-panel inspection.")]
    public void Plan47_EventSubprocessEditor_PanelFields() { }

    // tests/manual/48-io-mapping-editor/test-plan.md — editor I/O mappings panel.
    [TestMethod]
    [Ignore("Needs EditorPage POM + property-panel inspection.")]
    public void Plan48_IoMappingEditor_AddEditRemove() { }

    // tests/manual/49-complex-gateway-activation-condition/test-plan.md — the model-level
    // round-trip lives in EditorPropertiesTests.ActivationCondition_WriteReadClear_…
    // (currently [Ignore]'d pending investigation into a bpmn-js elementRegistry-inconsistent
    // state after modeling.updateProperties on default-namespace BPMN). The panel-DOM half
    // still needs an ElementPropertiesPanel POM.

    // tests/manual/50-gateway-default-flow/test-plan.md — the model-level round-trip is
    // automated in EditorPropertiesTests.DefaultFlow_PrePopulatedFromXml_…. The panel-DOM
    // half (dropdown population + click-to-select) still needs an ElementPropertiesPanel POM.

    // tests/manual/52-compensation-editor/test-plan.md — editor UI compensation panel.
    [TestMethod]
    [Ignore("Needs EditorPage POM (compensation activityRef + waitForCompletion fields).")]
    public void Plan52_CompensationEditor_PanelFields() { }

    // tests/manual/54-multi-event-editor/test-plan.md — editor UI multi-event panel.
    [TestMethod]
    [Ignore("Needs EditorPage POM (multi-event definitions panel).")]
    public void Plan54_MultiEventEditor_PanelFields() { }

    // tests/manual/55-plugin-host-isolation/test-plan.md — plugin host placement.
    [TestMethod]
    [Ignore("Needs separate Plugin-role silo in the test cluster (Fleans.CustomWorkerHost).")]
    public void Plan55_PluginHostIsolation_GrainPlacement() { }

    // tests/manual/56-streaming-queue-count-config/test-plan.md — config option round-trip.
    [TestMethod]
    [Ignore("Configuration option (Fleans:Streaming:Redis:TotalQueueCount); not a workflow assertion.")]
    public void Plan56_StreamingQueueCountConfig_RoundTrip() { }

    // tests/manual/58-custom-task-cancellation/test-plan.md — plugin cancellation.
    [TestMethod]
    [Ignore("Needs Worker silo + plugin host + cancellable plugin handler.")]
    public void Plan58_CustomTaskCancellation_GraceefulShutdown() { }

    // tests/manual/59-custom-task-output-mapping-editor/test-plan.md — editor UI for plugin outputs.
    [TestMethod]
    [Ignore("Needs EditorPage POM + custom-task output mapping panel.")]
    public void Plan59_CustomTaskOutputMappingEditor() { }

    // tests/manual/61-usertask-group-claim/test-plan.md — candidate-group claim.
    [TestMethod]
    [Ignore("Needs JWT 'groups' claim resolution in test cluster; spec body lives with Plan18 follow-up.")]
    public void Plan61_UserTaskGroupClaim_AuthorizationRule() { }

    // tests/manual/62-chart-streaming-providers/test-plan.md — Helm chart tests. OUT OF SCOPE.
    // tests/manual/63-chart-external-postgres/test-plan.md — Helm chart tests. OUT OF SCOPE.
}

// Stub base class to avoid forcing every [Ignore] method through the full Aspire boot.
// Tests above do not call any infrastructure; this class breaks the inheritance chain.
public abstract class WorkflowE2ETestBase_None
{
}
