# Manual Test Plan #55 — Plugin Host Isolation (three-role placement contract)

Verifies the three-role placement contract introduced on `feature/plugin-host-isolation`:

- Engine `[WorkerPlacement]` grains (`ScriptExecutorGrain`, `ConditionExpressionEvaluatorGrain`) only land on engine workers (`worker-*` / `combined-*` silos).
- Engine-bundled custom-task plugins (e.g. `Fleans.Plugins.RestCaller`) land on the engine worker silo that has the plugin assembly referenced.
- External plugin hosts using the new `Plugin` role (silo prefix `plugin-*`) host **only** the plugin grains compiled into their assembly load context — engine grains never land on them, and their plugin grains never land on engine silos.
- Engine startup rejects `Fleans:Role=Plugin`; the `AddFleansPluginHost` helper rejects `Fleans:Role=Worker` (and `Core`).

## Prerequisites

- Aspire stack running locally: `dotnet run --project Fleans.Aspire` (from `src/Fleans/`), **or** a publish-mode docker-compose stack: `aspire publish -t docker-compose -o out/compose` then `docker compose -f out/compose/compose.yaml up`.
- An external plugin host built from the [`nightBaker/fleans-custom-worker-example`](https://github.com/nightBaker/fleans-custom-worker-example) GitHub template:
  - `Fleans:Role=Plugin` set via env var (`Fleans__Role=Plugin`).
  - Same Redis connection as the engine cluster (`ConnectionStrings__orleans-redis`).
  - At least one plugin registered via `services.AddCustomTaskPlugin<T>(...)` (e.g. a stub `email` plugin returning `{"sent": true}` from `ExecuteAsync`).
- Web UI reachable at `https://localhost:7124`.
- API origin: `https://localhost:7140`.
- Orleans Dashboard reachable at `http://localhost:8080/Dashboard` (Aspire mode) or whichever host:port the publish-mode compose exposes.

## Scenario 1 — Engine grain placement (Script / Condition stays off `plugin-*`)

1. **Deploy a BPMN with a ScriptTask.** Use any chained-script-task fixture, e.g. `tests/manual/02-script-tasks/script-chain.bpmn`.
   ```bash
   curl -k -X POST https://localhost:7140/Workflow/deploy \
     -H "Content-Type: application/json" \
     -d "{\"BpmnXml\": $(jq -Rs . < tests/manual/02-script-tasks/script-chain.bpmn)}"
   ```
2. **Start an instance.**
   ```bash
   curl -k -X POST https://localhost:7140/Workflow/start \
     -H "Content-Type: application/json" \
     -d '{"WorkflowId":"script-chain"}'
   ```
3. **Open the Orleans Dashboard `Grains` page.** Find `ScriptExecutorGrain` rows.
4. **Verify (positive):** every `ScriptExecutorGrain` activation row's `SiloName` column is prefixed with `worker-` or `combined-`.
5. **Verify (negative):** no `ScriptExecutorGrain` activation exists on any `plugin-*` silo. The external plugin host must show **0** `ScriptExecutorGrain` activations on its row in the `Cluster` tab.

**Pass criteria:**

- [ ] All `ScriptExecutorGrain` activations are on `worker-*` or `combined-*` silos.
- [ ] Zero `ScriptExecutorGrain` activations on any `plugin-*` silo.
- [ ] The workflow completes (`isCompleted: true` via `GET /Workflow/instances/<id>/state`).

## Scenario 2 — Engine-bundled plugin placement (`RestCallerHandler` on the engine worker)

1. **Deploy** `tests/manual/39-rest-caller/rest-call.bpmn`.
2. **Start an instance** with the variables that fixture expects (see `39-rest-caller/test-plan.md`).
3. **Open the Orleans Dashboard `Grains` page** and find `RestCallerHandler` activations.

**Pass criteria:**

- [ ] `RestCallerHandler` activations land on the engine worker silo (silo prefix `worker-` or `combined-`) — the engine references `Fleans.Plugins.RestCaller` so its assembly is loaded there.
- [ ] No `RestCallerHandler` activation on any `plugin-*` silo (the external host doesn't reference `Fleans.Plugins.RestCaller`).
- [ ] The workflow completes successfully.

## Scenario 3 — External plugin grain placement (stays on `plugin-*` only)

Requires the external plugin host registered with a stub plugin whose `TaskType` is **not** referenced by any engine project. For this scenario, assume the external host registers an `email` plugin (`services.AddCustomTaskPlugin<EmailHandler>("email", "Email")`).

1. **Confirm the catalog lists the plugin under the external host:**
   ```bash
   curl -k https://localhost:7140/custom-tasks
   ```
   Expect an entry `{ "taskType": "email", "siloNames": ["plugin-<host>-<guid>"] }`. The `siloNames` array must contain **only** `plugin-*` entries — no `worker-*` / `combined-*`.
2. **Deploy a BPMN with a `<serviceTask type="email">`.** Author a minimal fixture inline, or reuse a stub from the template repo's tests.
3. **Start an instance** and watch the Orleans Dashboard `Grains` page for `EmailHandler` activations.

**Pass criteria:**

- [ ] `GET /custom-tasks` for `email` lists only `plugin-*` silos in `siloNames`.
- [ ] All `EmailHandler` activations are on `plugin-*` silos.
- [ ] No `EmailHandler` activation on any `worker-*` / `combined-*` / `core-*` silo (the engine doesn't have `EmailHandler.dll` loaded, so Orleans' `GetCompatibleSilos` filters it out).
- [ ] The workflow completes (the activity returns the plugin's output dictionary).

## Scenario 4 — Role-validation negative on the engine (`Fleans.Api` rejects `Plugin`)

1. **Stop the Aspire stack** (or shut down `fleans-core` in compose mode).
2. **Set `Fleans__Role=Plugin`** for `Fleans.Api`. In dev mode, easiest path is editing the Aspire host:
   ```bash
   FLEANS_ROLE=Plugin dotnet run --project Fleans.Aspire
   ```
   (Aspire's host stamps `Fleans__Role` on every project, so the override propagates to `Fleans.Api`.)
   In compose mode, edit `out/compose/.env` or pass the env var via `docker compose run`.
3. **Observe `fleans-core` startup logs.**

**Pass criteria:**

- [ ] `fleans-core` fails to start.
- [ ] The exception message contains the explicit string: `Fleans.Api does not support Fleans:Role=Plugin. The 'Plugin' role is reserved for external custom worker hosts` (or the equivalent message emitted by `src/Fleans/Fleans.Api/Program.cs`).
- [ ] No silo joins the cluster (verify on the Orleans Dashboard `Cluster` page — only the surviving non-engine silos remain).
- [ ] Reverting `Fleans__Role` to `Combined` (or unsetting it) restores normal startup.

## Scenario 5 — Role-validation negative on the plugin host (`AddFleansPluginHost` rejects `Worker`)

1. **Stop the external plugin host.**
2. **Set `Fleans__Role=Worker`** on the plugin host (env var, `appsettings.json`, or launch profile).
3. **Restart the plugin host.**

**Pass criteria:**

- [ ] The plugin host fails to start.
- [ ] Startup throws `InvalidOperationException` with a message containing: `Fleans:Role='Worker' is not valid for a custom plugin host. Use 'Plugin' (recommended) or 'Combined'` (verbatim text from `src/Fleans/Fleans.Worker/Hosting/PluginHostExtensions.cs`).
- [ ] Setting `Fleans__Role=Core` produces an equivalent rejection.
- [ ] Setting `Fleans__Role=Plugin` (or `Combined`) restores normal startup; the host joins the cluster with silo name `plugin-<machine>-<guid>` (verify via Orleans Dashboard).

## Verdict

- **PASSED** — all five scenarios green. Move PR to Review by Human.
- **FAILED / BUG** — file follow-up issue, send PR back to Ready.
