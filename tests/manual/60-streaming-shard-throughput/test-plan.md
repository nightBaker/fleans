# 60 — Streaming-shard throughput

Phase 1 of #593. Verifies that the load-delay plugin handler is wired into the load-test `fleans-worker` image, the BPMN fixture deploys, and a single workflow instance completes via the per-task-type stream namespace introduced in #566.

Phases 2 and 3 (driver scripts, partition-utilization probes, per-provider publication) ship in follow-up PRs and append to this folder.

## What this plan does not yet cover

- Driver scripts (`shard.js`, locust port) — Phase 2.
- `fan-out-check.sh`, `partition-utilization.sh` — Phase 2.
- Per-provider numerical publication (Redis / Kafka / AzureQueue) — Phase 3a/3b/3c.
- Pre-shard retrospective baseline — Phase 4 (deferred unless reviewers ask).

## Prerequisites

- Docker + the .NET SDK locally.
- The release build of the `fleans-worker` image **without** load-test plugins:
  ```bash
  cd src/Fleans
  dotnet publish Fleans.WorkerHost/Fleans.WorkerHost.csproj /t:PublishContainer /p:Version=phase1-prod-check
  ```
- The load-test variant of `fleans-worker` (this PR's deliverable):
  ```bash
  cd src/Fleans
  dotnet publish Fleans.WorkerHost/Fleans.WorkerHost.csproj /t:PublishContainer /p:Version=phase1-loadtest /p:FleansLoadTestMode=true
  ```

## Steps

### 1. Production tag does NOT carry the load-delay plugin

The MSBuild conditional in `Fleans.WorkerHost.csproj` keeps `Fleans.Plugins.LoadDelay.dll` out of any build that does not set `/p:FleansLoadTestMode=true`. Verify on the production tag:

```bash
docker run --rm --entrypoint sh fleans-worker:phase1-prod-check \
  -c 'ls /app | grep -c LoadDelay || true'
```

- [ ] Expected output: `0` (no LoadDelay-named file present in `/app`).

### 2. Load-test tag carries the plugin

```bash
docker run --rm --entrypoint sh fleans-worker:phase1-loadtest \
  -c 'ls /app | grep LoadDelay'
```

- [ ] Expected output includes `Fleans.Plugins.LoadDelay.dll` (and optionally a matching `.pdb`).

### 3. Plugin registers cleanly at silo start-up

Start the load-test worker silo against an Aspire stack:

```bash
# From src/Fleans/
FLEANS_LOAD_TEST_MODE=true dotnet run --project Fleans.Aspire
```

Inspect the `fleans-core` (Combined role in dev) startup logs.

- [ ] No `InvalidOperationException` from `AddCustomTaskPlugin<LoadDelayHandler>` (duplicate-TaskType or `[ImplicitStreamSubscription]` drift). Both validation paths in `Fleans.Worker.CustomTasks.CustomTaskServiceCollectionExtensions` would throw at registration time; their absence proves the literal `events.ExecuteCustomTaskEvent.load-delay-100ms` on `LoadDelayHandler` matches `WorkflowEventStreams.GetExecuteCustomTaskNamespace("load-delay-100ms")` exactly.

### 4. BPMN fixture deploys

Use the API (Combined role's `/Definitions/deploy` endpoint surfaces the deploy route post-`49a848a`):

```bash
curl -X POST 'http://localhost:8081/Definitions/deploy' \
  -H 'Content-Type: application/json' \
  --data-binary "@tests/manual/60-streaming-shard-throughput/service-task-shard.bpmn"
```

- [ ] Returns HTTP 200 with the deployed definition's `processDefinitionKey = "load-delay-100ms-shard"`.

### 5. Single workflow instance completes via the plugin

```bash
curl -X POST 'http://localhost:8081/Execution/start' \
  -H 'Content-Type: application/json' \
  -d '{"WorkflowId":"load-delay-100ms-shard"}'
```

- [ ] Returns HTTP 200 with a new instance id.
- [ ] `/Instances/{id}/state` shows the instance has reached the `end` event within 500ms (100ms `Task.Delay` + Orleans/stream overhead).
- [ ] `docker compose logs fleans-worker | grep "Custom-task handler activated on silo"` shows at least one EventId-4035 entry for stream key matching the workflow instance id (proves the per-task-type implicit-subscription routing fired correctly through #566's namespace).

## Pass criteria

All 5 step checklists pass. A failure on Step 1 means the production image leaks the load-test plugin DLL (build-time gating broken). Failures on Steps 2–5 mean the load-test wiring is broken; Phase 2 cannot proceed until they pass.

## Failure modes

- Step 1 fails → check `Fleans.WorkerHost.csproj` `<ItemGroup Condition="'$(FleansLoadTestMode)' == 'true'">` syntax and the `<DefineConstants>` clause; both must be MSBuild-conditional on the same property.
- Step 3 fails with "duplicate TaskType" → another plugin claims `load-delay-100ms`; rename the handler's `TaskType` constant.
- Step 3 fails with "[ImplicitStreamSubscription] mismatch" → the literal on the handler doesn't match `WorkflowEventStreams.GetExecuteCustomTaskNamespace("load-delay-100ms")` = `"events.ExecuteCustomTaskEvent.load-delay-100ms"`. Update the attribute literal.
- Step 5 hangs (instance never reaches `end`) → suspect subscriber-side stream-key reconstruction. Cross-reference CLAUDE.md *"Subscriber-side stream-id trap"* — handler must derive its stream id from `this.GetPrimaryKeyString()`, not a hard-coded constant. `CustomTaskHandlerBase.OnActivateAsync` already does this; the failure is upstream of the new plugin.
