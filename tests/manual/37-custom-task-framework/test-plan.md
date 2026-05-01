# Manual Test Plan #37 — Custom Task Framework

Verifies the custom-task framework end-to-end: BPMN parses to a `CustomTaskActivity`, the activity emits an event, the catalog reflects registered/dropped plugins, and a manual `complete-activity` API call resumes a workflow whose plugin is unregistered.

## Prerequisites
- Aspire stack running: `dotnet run --project Fleans.Aspire` (from `src/Fleans/`).
- Clean dev DB (delete the SQLite file or set a fresh `FLEANS_SQLITE_CONNECTION`).
- Web UI reachable at `https://localhost:7124`.
- API origin: `https://localhost:7140`.
- Fixture: `stub-custom-task.bpmn` (this folder).

## Scenario 1 — Unregistered plugin: activity stays Active until manually completed

1. **Deploy the workflow.**
   ```bash
   curl -k -X POST https://localhost:7140/Workflow/deploy \
     -H "Content-Type: application/json" \
     -d "{\"BpmnXml\": $(jq -Rs . < tests/manual/37-custom-task-framework/stub-custom-task.bpmn)}"
   ```
   Expect HTTP 200 with `{"ProcessDefinitionKey":"stub-custom-task","Version":1}`.

2. **Confirm the catalog has no `stub-task` entry.**
   ```bash
   curl -k https://localhost:7140/custom-tasks
   ```
   Expect `[]` (or no entry with `taskType=stub-task`).

3. **Start an instance.**
   ```bash
   curl -k -X POST https://localhost:7140/Workflow/start \
     -H "Content-Type: application/json" \
     -d '{"WorkflowId":"stub-custom-task"}'
   ```
   Capture the returned instance ID.

4. **Confirm the activity is Active.**
   ```bash
   curl -k https://localhost:7140/Workflow/instances/<instance-id>/state
   ```
   Expect `activeActivityIds` contains `ct1`; `isCompleted: false`.

5. **Manually complete the activity** (no plugin will, so this is the only way forward).
   ```bash
   curl -k -X POST https://localhost:7140/Workflow/complete-activity \
     -H "Content-Type: application/json" \
     -d '{"WorkflowInstanceId":"<instance-id>","ActivityId":"ct1","Variables":{"echo":"manual"}}'
   ```

6. **Confirm completion.** The state endpoint now returns `isCompleted: true` and `echo` shows up in the variable projection.

## Scenario 2 — Registered plugin: end-to-end claim and complete

A real plugin must exist in the silo's host. For this scenario, build a tiny in-tree stub plugin (e.g. `Fleans.Plugins.StubTask` referencing `Fleans.Worker`) that returns `{"__response":"hi from stub"}` from `ExecuteAsync`. Register it in `Fleans.Aspire`'s host setup with `services.AddCustomTaskPlugin<StubTaskHandler>("stub-task", "Stub task")`.

1. **Restart the Aspire stack** so the registrar runs and announces the plugin.
2. **Confirm the catalog now lists the plugin.**
   ```bash
   curl -k https://localhost:7140/custom-tasks
   ```
   Expect one entry with `taskType=stub-task` and at least one silo in `siloNames`.
3. **Start an instance** (same body as Scenario 1 step 3).
4. **Within ~1 s** the activity should auto-complete: state shows `isCompleted: true` and `echo == "hi from stub"`.

## Scenario 3 — Catalog reconciliation drops a stopped silo

Requires multi-silo setup (e.g. Docker Compose with two worker silos, each registered with the stub plugin).

1. Start cluster, observe `GET /custom-tasks` returns one `stub-task` entry with two silos in `siloNames`.
2. Stop one worker silo (`docker compose stop fleans-core-2`).
3. Wait ≤ 30 s for the catalog reconcile timer.
4. Re-query `GET /custom-tasks` — expect the entry now lists only the surviving silo.

## Expected outcomes (checklist)

- [ ] Scenario 1, step 1: deploy succeeds.
- [ ] Scenario 1, step 2: catalog returns `[]`.
- [ ] Scenario 1, step 4: activity is Active with `ct1`.
- [ ] Scenario 1, step 5: complete-activity succeeds.
- [ ] Scenario 1, step 6: workflow completes; `echo` variable holds `"manual"`.
- [ ] Scenario 2: plugin handler runs automatically; workflow completes with `echo == "hi from stub"`.
- [ ] Scenario 3: dropped silo disappears from `siloNames` within 30 s; surviving silo entry remains.

## Known limitations (v1)
- Catalog state is in-memory only; Core silo restart wipes it until Worker silos re-register at their own next startup. Persistence is a v2 follow-up.
- Per-task-type stream partitioning is not implemented; every plugin handler receives every `ExecuteCustomTaskEvent` and discards mismatches with an early return.
