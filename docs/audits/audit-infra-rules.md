# audit-infra rules
> Generated and maintained by the `audit-infra` skill. One heading per rule.
> Severity vocabulary: `blocking` / `important` / `minor`.
> Dimensions: `scalability` / `reliability` / `pluggability`.
> Slugs are stable identifiers; renaming a slug means losing idempotency for that rule.

## pluggability/persistence-unknown-provider-silent-fallback
- **dimension:** pluggability
- **severity:** important
- **signal:** `FleansPersistenceExtensions.AddFleansPersistence` in `src/Fleans/Fleans.ServiceDefaults/FleansPersistenceExtensions.cs` uses if/else where the else branch silently activates SQLite for any provider string that isn't "Postgres" — look for the absence of a throw or explicit "Sqlite" check in the else arm.
- **remediation:** Add an explicit `else if (provider.Equals("Sqlite", ...))` arm and a `_ => throw new ArgumentException(...)` default, matching the pattern already used in `FleanStreamingExtensions.AddFleanStreaming` (`src/Fleans/Fleans.ServiceDefaults/FleanStreamingExtensions.cs:26`). The throw message should list all supported provider names. See issue #413.
- **first seen:** 2026-04-28
- **last matched:** 2026-04-28

## scalability/event-store-unbounded-read
- **dimension:** scalability
- **severity:** important
- **signal:** `EfCoreEventStore.ReadEventsAsync` in `src/Fleans/Fleans.Persistence/Events/EfCoreEventStore.cs` calls `.ToListAsync()` on a `WorkflowEvents` query with no `Take(limit)` — search for the absence of `Take(` between the `.Where(e => e.GrainId ==` filter and the `.ToListAsync()` call in that method.
- **remediation:** (1) Short-term: add a configurable `MaxEventsPerLoad` guard and throw when exceeded. (2) Medium-term: implement `JournaledGrain` snapshotting so `ReadEventsAsync` is called with `afterVersion = snapshotVersion` instead of 0 on cold activation. The `ICustomStorageInterface` hook in `WorkflowInstance.cs` is the natural snapshot persistence point. See issue #414.
- **first seen:** 2026-04-28
- **last matched:** 2026-04-28

## scalability/user-task-full-table-materialize
- **dimension:** scalability
- **severity:** minor
- **signal:** `WorkflowQueryService.GetPendingUserTasks` (the paged overload) in `src/Fleans/Fleans.Persistence/WorkflowQueryService.cs` calls `sievedQuery.ToListAsync()` before the `ApplyUserTaskFilters` call — the assignee/candidateGroup WHERE clause is applied in memory after the full non-completed user-task table is loaded. Look for `ToListAsync()` followed by `ApplyUserTaskFilters(` with no intervening DB-level WHERE on assignee or candidateGroup.
- **remediation:** Add provider-specific EF Core expressions for `CandidateUsers`/`CandidateGroups` JSON array containment (`@>` / `?` on PostgreSQL via Npgsql's `EF.Functions`) so the filter executes server-side on the Postgres provider. The SQLite path can retain the in-memory fallback. Follow the `RelationalModelCustomizer` subclass pattern in `Fleans.Persistence.Sqlite` and `Fleans.Persistence.PostgreSql`. See issue #415.
- **first seen:** 2026-04-28
- **last matched:** 2026-04-28

## pluggability/role-env-aspire-chart-parity
- **dimension:** pluggability
- **severity:** minor
- **signal:** `Fleans.Aspire/Program.cs` does not inject `Fleans__Role` via `WithEnvironment` on any project, while `charts/fleans/templates/deployment-core.yaml` and `deployment-worker.yaml` both set `Fleans__Role`. Search for the absence of `Fleans__Role` in `src/Fleans/Fleans.Aspire/Program.cs`.
- **remediation:** Add `WithEnvironment("Fleans__Role", builder.Configuration["FLEANS_ROLE"] ?? "Combined")` when wiring the `fleans-core` project in Aspire. Document the `FLEANS_ROLE` local-dev override in CLAUDE.md under the Core/Worker role split section. See issue #416.
- **first seen:** 2026-04-28
- **last matched:** 2026-04-28

## scalability/unpaged-getpendingusertasks-interface-overload
- **dimension:** scalability
- **severity:** important
- **signal:** `WorkflowQueryService.GetPendingUserTasks(string?, string?)` (the 2-arg overload, `src/Fleans/Fleans.Persistence/WorkflowQueryService.cs`, implementing `IWorkflowQueryService.GetPendingUserTasks(string?, string?)`) calls `db.UserTasks.Where(t => t.TaskState != UserTaskLifecycleState.Completed).ToListAsync()` with no `Take(`/page bound, then applies `ApplyUserTaskFilters(tasks, assignee, candidateGroup)` afterward in memory — search for the absence of any SQL-level assignee/candidateGroup predicate or row cap between the `.Where(t => t.TaskState !=` filter and `.ToListAsync()` in that specific 2-arg overload (distinct from the paged 3-arg overload, which already pushes down via `IUserTaskFilterStrategy` on Postgres).
- **remediation:** Delete the overload if it remains callerless, or reimplement it by delegating to the paged overload with a bounded page size (mirroring `EfCoreEventStore.ReadEventsAsync`'s `_maxEventsPerLoad` guard) and route it through `IUserTaskFilterStrategy`. See issue #749.
- **first seen:** 2026-08-24
- **last matched:** 2026-08-24

## pluggability/chart-custom-worker-not-plugin-host
- **dimension:** pluggability
- **severity:** blocking
- **signal:** `charts/fleans/templates/deployment-custom-worker.yaml` sets `Fleans__Role: "Worker"` (not `Plugin`) and builds its image from `.Values.image.api.repository` — search for the absence of `Fleans__Role: "Plugin"` and the absence of any `image.customWorker.repository` (or similar dedicated key) in `values.yaml`'s `customWorker` block.
- **remediation:** Either remove `deployment-custom-worker.yaml`/`customWorker.*` and document the external `fleans-custom-worker-example` template-repo workflow as the only supported path, or add a required `customWorker.image.repository` value (no in-tree default) and set `Fleans__Role: Plugin`, failing the template render via `{{ fail }}` when enabled but unset (mirror the streaming-provider `{{ fail }}` pattern in `_helpers.tpl:110-112`). See issue #741.
- **first seen:** 2026-08-24
- **last matched:** 2026-08-24

## pluggability/aspire-missing-streaming-tuning-envs
- **dimension:** pluggability
- **severity:** important
- **signal:** `charts/fleans/templates/_helpers.tpl` emits `Fleans__Streaming__Redis__TotalQueueCount` and `Fleans__Streaming__Kafka__QueueCount` from `values.yaml`, but `src/Fleans/Fleans.Aspire/Program.cs`'s `WithStreaming` helper has no corresponding `builder.Configuration["FLEANS_STREAMING_..._QUEUE_COUNT"]` read / `WithEnvironment("Fleans__Streaming__Redis__TotalQueueCount", ...)` or `...Kafka__QueueCount...` call — grep `Program.cs` for `QueueCount` and confirm zero matches while the chart has them.
- **remediation:** Add `FLEANS_STREAMING_REDIS_TOTAL_QUEUE_COUNT` / `FLEANS_STREAMING_KAFKA_QUEUE_COUNT` reads in `Program.cs` and stamp `Fleans__Streaming__Redis__TotalQueueCount` / `Fleans__Streaming__Kafka__QueueCount` via `WithEnvironment` inside `WithStreaming`, following the explicit-default pattern already used for `coreRole`/`persistenceProvider`. See issue #750.
- **first seen:** 2026-08-24
- **last matched:** 2026-08-24

## reliability/timer-start-scheduler-reminder-redelivery
- **dimension:** reliability
- **severity:** blocking
- **signal:** `TimerStartEventSchedulerGrain.FireTimerStartEvent` (`src/Fleans/Fleans.Application/Grains/TimerStartEventSchedulerGrain.cs`) calls `child.StartWorkflow()` before `State.IncrementFireCount(); await _state.WriteStateAsync()`, and `ReceiveReminder` calls `FireTimerStartEvent()` with no pre-call stale/dedup check — search for the absence of any per-tick fired-marker check (analogous to `WorkflowExecution.IsTimerFireStale`) at the top of `ReceiveReminder` or `FireTimerStartEvent` in that file.
- **remediation:** Persist a durable per-tick fired-marker in `TimerStartEventSchedulerState` before calling `StartWorkflow()`, and check it at entry to `FireTimerStartEvent`/`ReceiveReminder`, mirroring `WorkflowExecution.IsTimerFireStale`'s pre-call-check pattern used by `TimerCallbackGrain.ReceiveReminder`. Add the method to CLAUDE.md's stream-redelivery idempotency guard-set once fixed. See issue #751.
- **first seen:** 2026-08-24
- **last matched:** 2026-08-24

## reliability/custom-task-plugin-cancellation-redelivery-side-effect
- **dimension:** reliability
- **severity:** important
- **signal:** `CustomTaskHandlerBase.OnNextAsync` (`src/Fleans/Fleans.Worker/CustomTasks/CustomTaskHandlerBase.cs`) re-throws on `OperationCanceledException` when `_grainLifetimeCts.IsCancellationRequested` to force stream redelivery, and `docs/conventions/adding-a-custom-task-plugin.md` does not state that `ExecuteAsync` must be idempotent under this specific redelivery path — search the doc for the absence of an explicit "must be safe to invoke twice" statement near its cancellation-is-not-a-failure section, and check whether `RestCallerHandler`'s `idempotencyKeyHeader` (`src/Fleans/Fleans.Plugins.RestCaller/RestCallerHandler.cs`) still defaults to off.
- **remediation:** Strengthen the plugin-authoring doc with an explicit idempotency-under-redelivery requirement and a concrete pattern; consider defaulting `RestCallerHandler` to auto-generate an idempotency key for non-GET requests rather than requiring opt-in. See issue #752.
- **first seen:** 2026-08-24
- **last matched:** 2026-08-24
