# Manual Test Plan 40 — Custom Worker Host

## Scenario

Verify that `Fleans.CustomWorkerHost` — the worked-example deployable for hosting custom-task
plugins outside the engine repo — joins the Orleans cluster, claims `<serviceTask type="rest-call">`
activities via the `Fleans.Plugins.RestCaller` plugin, and completes them. The test runs in two
modes:

- **Mode (i) — standalone (default for regression)**: launch Aspire to provision Redis + the
  engine, then run `Fleans.CustomWorkerHost` as a separate process pointing at the same Redis.
- **Mode (ii) — docker-compose**: `aspire publish -t docker-compose` emits a compose file that
  includes `fleans-custom-worker`. Bring up the stack with `docker compose up`; `fleans-worker` is
  intentionally omitted from the compose file via the operator's discretion (see notes below).

## Prerequisites

- Aspire stack runnable: `dotnet run --project Fleans.Aspire` (from `src/Fleans/`)
- Clean dev DB (delete the SQLite file or set a fresh `FLEANS_SQLITE_CONNECTION`)
- Web UI reachable at `https://localhost:7124`
- API origin: `https://localhost:7140`
- Network access to `https://httpbin.org` (for the GET happy-path call)

## Mode (i) — standalone

### Setup

1. Start Aspire: `dotnet run --project Fleans.Aspire` (from `src/Fleans/`).
2. Wait for the Aspire dashboard to come up; copy the Redis connection string from the
   `orleans-redis` resource (Connection Strings tab).
3. In a second terminal, launch the custom worker host:
   ```bash
   cd src/Fleans
   ConnectionStrings__orleans-redis="<paste from step 2>" \
     dotnet run --project Fleans.CustomWorkerHost --environment Development
   ```
   It should log "Silo started" within ~5s and stay running.

### Steps

1. **Deploy the fixture.** From `tests/manual/40-custom-worker-host/`:
   ```bash
   curl -k -X POST https://localhost:7140/Workflow/deploy \
     -H 'Content-Type: application/json' \
     -d "{\"BpmnXml\": $(cat rest-call.bpmn | jq -Rs .)}"
   ```
   Verify response contains `ProcessDefinitionKey` and `Version: 1`.

2. **Start an instance.**
   ```bash
   curl -k -X POST https://localhost:7140/Workflow/start \
     -H 'Content-Type: application/json' \
     -d '{"WorkflowId":"custom-worker-host-rest"}'
   ```
   Capture the returned `WorkflowInstanceId`.

3. **Watch the custom-worker-host log.** Within ~10s, expect:
   - `Handling custom-task event for activity rest-call-task (taskType=rest-call)`
   - HTTP 200 from httpbin.org
   - Activity completes; the workflow advances to `end`.

4. **Verify completion via API:**
   ```bash
   curl -k https://localhost:7140/Workflow/instances/<WorkflowInstanceId>/state
   ```
   `isCompleted` must be `true`; `completedActivityIds` includes `rest-call-task`.

### Expected outcomes

- [ ] Custom worker host starts cleanly with `Fleans:Role=Worker`.
- [ ] Joins the same Orleans cluster as the engine (visible in Orleans Dashboard if enabled).
- [ ] Claims and completes the `rest-call` service-task event (engine-side `fleans-core` does
      NOT process the activity — the custom worker host wins via implicit-stream subscription).
- [ ] Workflow instance reaches `Completed` state.
- [ ] `responseBody` variable is populated (visible via state-endpoint diagnostics).

## Mode (ii) — docker-compose

### Setup

1. From `src/Fleans/`:
   ```bash
   aspire publish --project Fleans.Aspire -t docker-compose -o out/compose
   ```
2. Optionally remove the `fleans-worker` service from `out/compose/docker-compose.yaml` to
   prove the custom worker host alone can claim the activity (otherwise both compete and
   either may win — that is also valid behavior).
3. `docker compose -f out/compose/docker-compose.yaml up -d`
4. Wait for all services to report healthy (`docker compose ps`).

### Steps

Same as Mode (i) steps 1–4 but targeting the published API endpoint surfaced by the compose
file (typically `http://localhost:8080`).

### Expected outcomes

- [ ] `fleans-custom-worker` container starts and stays up.
- [ ] Activity completes via the custom worker host.

## Notes

- The `Fleans.CustomWorkerHost` project intentionally references **only** `Fleans.Worker` and
  the chosen plugin assemblies (`Fleans.Plugins.RestCaller`). It does **not** reference
  `Fleans.Application`, `Fleans.Domain`, `Fleans.Infrastructure`, or any persistence project.
  This is the structural guarantee that demonstrates the "host your own plugins" pattern to
  end users.
- Plugin packages share the engine's `<VersionPrefix>` track — every Fleans release bumps
  every plugin's NuGet version even when the plugin source is bit-identical (precedent:
  `Aspire.Hosting.*` / `Microsoft.Orleans.*`).
