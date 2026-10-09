# 45 — fleans namespace (service task + io mapping + multi-instance)

End-to-end coverage that the engine deploys and runs a workflow whose extension elements use the **`fleans:`** prefix at the new `https://fleans.io/schema/bpmn/1.0` URI for `taskDefinition`, `ioMapping > input/output`, and the multi-instance loop attributes (`collection`, `elementVariable`, `outputCollection`, `outputElement`). Companion to the editor's golden-path manual check (see `tests/manual/38-custom-task-editor/test-plan.md`) and the dual-namespace parser unit tests in `Fleans.Infrastructure.Tests/BpmnConverter/{CustomTaskRoutingTests,MultiInstanceParsingTests}.cs`.

## Prereqs

- Aspire stack running: `dotnet run --project Fleans.Aspire` (from `src/Fleans/`)
- Custom-task plugin registered for `type="stub-task"` is **optional** (the same plugin used by `tests/manual/37-custom-task-framework/`). With no plugin registered, `ct1` stays **Active** (nothing claims the per-type stream) and you advance it manually with `POST /Workflow/complete-activity` (`activityId=ct1`) — this is exactly what the automated E2E port (`RedisAndNamespaceSmokeTests.FleansNamespace_ServiceTaskDeploysAndCompletes`) does.

## Steps

1. **Deploy:** in the Web UI (`http://localhost:8080`), open `Process Definitions`, click **Upload BPMN**, choose `tests/manual/45-fleans-namespace/fleans-service-task.bpmn`. Expected: deploy succeeds, no parse errors. The `fleans:taskDefinition` discriminator must be picked up — the service task should bind to the `stub-task` plugin (verifiable via the deployed-process detail panel).

2. **Inspect XML in editor:** in the editor (`/editor`), open the deployed process. Confirm the saved XML preserves `xmlns:fleans="https://fleans.io/schema/bpmn/1.0"` and all three fleans-namespaced extension shapes (`<fleans:taskDefinition>`, `<fleans:ioMapping>`, multi-instance attrs) round-trip without being silently rewritten.

3. **Start instance:** start a new instance with no input variables. Watch it advance: `start → seed (sets items=[1,2,3]) → ct1 (calls stub-task) → iterate (multi-instance over items) → end`. Without a `stub-task` plugin, `ct1` waits in **Active** — complete it via `POST /Workflow/complete-activity` (`{"WorkflowInstanceId":"<id>","ActivityId":"ct1","Variables":{}}`).

   > **#762 regression guard.** The `seed` script is `_context.items = new List<object> { 1, 2, 3 }`. An earlier version used `new[] { 1, 2, 3 }`, which DynamicExpresso cannot parse: `seed` failed, and because nothing handled the error the instance used to sit with no active activities while being neither completed nor failed. It must now show `seed` with an error **and** the instance status **Failed** — never a silent "Running" with no active activities. (Scenario coverage of that failure path lives in `tests/manual/69-unhandled-failure-fails-instance/`.)

4. **Verify multi-instance output:** instance state shows `doubled_results == [2, 4, 6]`. This proves the parser read the four `fleans:*` multi-instance attributes correctly.

5. **Verify io-mapping output:** instance state shows the `echo` variable populated from the `__response` of the stub-task plugin (exact value depends on the plugin's response shape; just confirm the variable exists).

6. **Backward-compat sanity:** redeploy `tests/manual/37-custom-task-framework/stub-custom-task.bpmn` (zeebe-prefixed equivalent) and start it. Should behave identically. This proves zeebe imports still work.

## Out of scope

- The editor's *write* behavior for new BPMN authored from scratch — covered by `tests/manual/38-custom-task-editor/test-plan.md` plus the manual golden-path check in this PR's verification step (export a new diagram, confirm `xmlns:fleans=…/1.0` is the only ext namespace declared and `xmlns:zeebe` is absent).
- Legacy `http://fleans.io/schema/bpmn/fleans` URI — covered by the unit test `MessageEventTests.ConvertFromXmlAsync_ShouldParseFleansCorrelationKey_LegacyUri`.

## Verdict

Pass / Fail / Known-bug. The instance must end **Completed** (not **Failed**). If failing, capture: (a) the exact error or wrong variable value, (b) the deployed XML as the engine stored it, (c) any console errors from the editor.
