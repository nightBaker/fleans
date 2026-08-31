# audit-infra rules
> Generated and maintained by the `audit-infra` skill. One heading per rule.
> Severity vocabulary: `blocking` / `important` / `minor`.
> Dimensions: `scalability` / `reliability` / `pluggability`.
> Slugs are stable identifiers; renaming a slug means losing idempotency for that rule.
>
> **Accretion note (2026-08-31):** every `audit: refresh infra rules` PR opened between
> 2026-05-11 and 2026-08-24 (#553, #612, #677, #715, #730, #740, #745, #748, #753) landed on a
> disposable per-run branch and was never merged, so this file was stuck at its 2026-04-28 seed
> while eight weeks of rubric-pass findings piled up as unmerged rule text. The GitHub issues
> those runs filed were still created/refreshed for real (issue-sync doesn't depend on this file
> landing), so no findings were lost — but the file itself was blind to them. This revision
> reconstructs the accreted rule set from those PRs' diffs and the current open `audit:*` issues,
> deduplicated by underlying concern, and verifies every one against today's code.

## pluggability/persistence-unknown-provider-silent-fallback
- **dimension:** pluggability
- **severity:** important
- **signal:** `FleansPersistenceExtensions.AddFleansPersistence` in `src/Fleans/Fleans.ServiceDefaults/FleansPersistenceExtensions.cs` uses if/else where the else branch silently activates SQLite for any provider string that isn't "Postgres" — look for the absence of a throw or explicit "Sqlite" check in the else arm.
- **remediation:** Add an explicit `else if (provider.Equals("Sqlite", ...))` arm and a `_ => throw new ArgumentException(...)` default, matching the pattern already used in `FleanStreamingExtensions.AddFleanStreaming` (`src/Fleans/Fleans.ServiceDefaults/FleanStreamingExtensions.cs:26`). The throw message should list all supported provider names. See issue #413.
- **first seen:** 2026-04-28
- **last matched:** 2026-04-28
- **resolved:** 2026-08-31 — the else arm now throws `ArgumentException` listing supported providers (`Sqlite`, `Postgres`); no silent fallback remains.

## scalability/event-store-unbounded-read
- **dimension:** scalability
- **severity:** important
- **signal:** `EfCoreEventStore.ReadEventsAsync` in `src/Fleans/Fleans.Persistence/Events/EfCoreEventStore.cs` calls `.ToListAsync()` on a `WorkflowEvents` query with no `Take(limit)` — search for the absence of `Take(` between the `.Where(e => e.GrainId ==` filter and the `.ToListAsync()` call in that method.
- **remediation:** (1) Short-term: add a configurable `MaxEventsPerLoad` guard and throw when exceeded. (2) Medium-term: implement `JournaledGrain` snapshotting so `ReadEventsAsync` is called with `afterVersion = snapshotVersion` instead of 0 on cold activation. The `ICustomStorageInterface` hook in `WorkflowInstance.cs` is the natural snapshot persistence point. See issue #414.
- **first seen:** 2026-04-28
- **last matched:** 2026-04-28
- **resolved:** 2026-08-31 — `ReadEventsAsync` now applies `.Take(limit)` (line 80) before `.ToListAsync()`.

## scalability/user-task-full-table-materialize
- **dimension:** scalability
- **severity:** minor
- **signal:** `WorkflowQueryService.GetPendingUserTasks` (the paged 3-arg overload) in `src/Fleans/Fleans.Persistence/WorkflowQueryService.cs` — look for the ABSENCE of a `_userTaskFilter.PushesToSql` guard separating a server-side Postgres path (`CountAsync` + paginated `ToListAsync`) from the SQLite in-memory fallback. The SQLite in-memory path is an accepted design limitation and should not re-trigger this rule on its own.
- **remediation:** Confirmed resolved for the paged overload: Postgres now pushes assignee/candidateGroup filters to SQL via `IUserTaskFilterStrategy.PushesToSql` (`WorkflowQueryService.cs:374-388`); SQLite retains the in-memory fallback by design. The unpaged 2-arg overload was missed by this fix — tracked separately as `scalability/unpaged-getpendingusertasks-interface-overload` (#749) below.
- **first seen:** 2026-04-28
- **last matched:** 2026-04-28
- **resolved:** 2026-08-31 — paged overload confirmed fixed; residual gap spun off to #749.

## pluggability/role-env-aspire-chart-parity
- **dimension:** pluggability
- **severity:** minor
- **signal:** `Fleans.Aspire/Program.cs` does not inject `Fleans__Role` via `WithEnvironment` on any project, while `charts/fleans/templates/deployment-core.yaml` and `deployment-worker.yaml` both set `Fleans__Role`. Search for the absence of `Fleans__Role` in `src/Fleans/Fleans.Aspire/Program.cs`.
- **remediation:** Add `WithEnvironment("Fleans__Role", builder.Configuration["FLEANS_ROLE"] ?? "Combined")` when wiring the `fleans-core` project in Aspire. Document the `FLEANS_ROLE` local-dev override in CLAUDE.md under the Core/Worker role split section. See issue #416.
- **first seen:** 2026-04-28
- **last matched:** 2026-04-28
- **resolved:** 2026-08-31 — `Fleans__Role` is stamped via `WithEnvironment` on the core project (line 181) and worker project (line 248) in `Fleans.Aspire/Program.cs`.

## pluggability/persistence-helm-no-fail-guard
- **dimension:** pluggability
- **severity:** minor
- **signal:** `charts/fleans/templates/_helpers.tpl` sets `Persistence__Provider` from `.Values.persistence.provider` with no `{{- fail ... }}` guard, while `Fleans__Streaming__Provider` has one (`{{- if not (has $provider (list "memory" "redis" "kafka" "azurequeue")) }}` at line 111). Grep `_helpers.tpl` for `Persistence__Provider` and confirm no `fail` call appears near it.
- **remediation:** Add `{{- $persistenceProvider := lower .Values.persistence.provider }}{{- if not (has $persistenceProvider (list "sqlite" "postgres")) }}{{- fail (printf "Unsupported persistence.provider %q. Valid: Sqlite, Postgres (case-insensitive)." .Values.persistence.provider) }}{{- end }}` immediately before the `Persistence__Provider` env entry, mirroring the streaming guard. Catches typos at `helm template` time instead of silo startup (`FleansPersistenceExtensions.cs:51-55` already fails correctly, just too late). See issue #675.
- **first seen:** 2026-05-25
- **last matched:** 2026-08-31

## pluggability/reminders-provider-chart-gap
- **dimension:** pluggability
- **severity:** important
- **signal:** `charts/fleans/values.yaml` has no `reminders:` key and `_helpers.tpl`'s `fleans.commonEnv` never emits `Fleans__Reminders__Provider` — grep `charts/fleans` (case-insensitive) for `reminders` and confirm zero hits. `FleansRemindersExtensions.ResolveRemindersConfiguration` (`src/Fleans/Fleans.ServiceDefaults/Reminders/FleansRemindersExtensions.cs`) supports `Redis` (default) and `Postgres` and throws on unknown values, but the chart's only path to `Postgres` reminders is the untyped `extraEnv` escape hatch.
- **remediation:** Add `reminders.provider: Redis` to `values.yaml` (comment: "Redis | Postgres — Postgres requires persistence.provider: Postgres"), and a `fail`-guarded `Fleans__Reminders__Provider` env entry to `fleans.commonEnv` in `_helpers.tpl`, mirroring the streaming-provider pattern. See issue #676.
- **first seen:** 2026-05-25
- **last matched:** 2026-08-31

## reliability/logger-message-convention-violation
- **dimension:** reliability
- **severity:** minor
- **signal:** Grep `src/Fleans` (excluding test projects) for `_logger\.Log(Error|Warning|Information|Debug|Trace|Critical)\(` — CLAUDE.md mandates `[LoggerMessage]` source generators exclusively. Current violators: `Fleans.Streaming.Kafka/KafkaQueueAdapter.cs`, `Fleans.Streaming.Kafka/KafkaQueueAdapterReceiver.cs`, `Fleans.Application/Effects/EffectDispatcher.cs`. `KafkaQueueAdapterFactory.cs` was already converted (EventIds 11100-11106) — use it as the in-tree exemplar of the fix, not a remaining violator.
- **remediation:** Make each violating class `partial`; replace `_logger.Log*()` calls with `[LoggerMessage]`-attributed `private partial void Log...()` methods; allocate new EventId ranges in `docs/conventions/observability-eventids.md` for Kafka streaming and `EffectDispatcher`. Canonical pattern: `src/Fleans/Fleans.Application/Grains/WorkflowInstance.Logging.cs`. See issue #714.
- **first seen:** 2026-06-08
- **last matched:** 2026-08-31

## pluggability/chart-custom-worker-not-plugin-host
- **dimension:** pluggability
- **severity:** blocking
- **signal:** `charts/fleans/templates/deployment-custom-worker.yaml` builds its container from `.Values.image.api.repository` and stamps `Fleans__Role: "Worker"` (not `Plugin`) — grep the file for `image.api.repository` and `Fleans__Role`, and confirm `values.yaml`'s `customWorker` block has no `image.repository`/`image.customWorker.repository` override key. Cross-check against `PluginHostExtensions.ValidatePluginRole` (`src/Fleans/Fleans.Worker/Hosting/PluginHostExtensions.cs`), the only path that accepts `Fleans:Role=Plugin`.
- **remediation:** Either (1) remove `deployment-custom-worker.yaml`/`customWorker.*` from the chart and document the external `fleans-custom-worker-example` template-repo workflow as the only supported plugin-host path, or (2) add a required `customWorker.image.repository` value (no in-tree default) and change the env stamp to `Fleans__Role: Plugin`, failing the render via `{{ fail }}` when `customWorker.enabled=true` but the image is unset, mirroring the streaming-provider `fail` pattern in `_helpers.tpl:110-112`. See issue #741.
- **first seen:** 2026-08-10
- **last matched:** 2026-08-31

## reliability/signal-broadcast-drops-subscribers-on-delivery-failure
- **dimension:** reliability
- **severity:** important
- **signal:** `SignalCorrelationGrain.BroadcastSignal` in `src/Fleans/Fleans.Application/Grains/SignalCorrelationGrain.cs` clears and persists `_state.State.Subscriptions` before the fan-out loop; its per-subscriber `catch` block only calls `LogDeliveryFailed` with no re-add to `Subscriptions`. Compare against `MessageCorrelationGrain.DeliverMessage`'s catch block, which restores `Status = Subscribed` on failure.
- **remediation:** On per-subscriber delivery failure inside `BroadcastSignal`, re-add the failed `SignalSubscription` back to `_state.State.Subscriptions` (under `_mutex`) and persist, mirroring `MessageCorrelationGrain`'s catch-and-restore pattern. See issue #742.
- **first seen:** 2026-08-10
- **last matched:** 2026-08-31

## reliability/http-start-event-endpoints-not-idempotent
- **dimension:** reliability
- **severity:** important
- **signal:** `StartEventListenerGrainBase.FireStartEventCore` in `src/Fleans/Fleans.Application/Grains/StartEventListenerGrainBase.cs` mints `Guid.NewGuid()` per registered process definition with no caller-supplied idempotency key anywhere in the `ExecutionController.SendMessage`/`SendSignal` → `WorkflowCommandService` → `FireMessageStartEvent`/`FireSignalStartEvent` call chain. Grep `SendMessageRequest`/`SendSignalRequest` (`src/Fleans/Fleans.ServiceDefaults/DTOs/`) for the absence of an idempotency-key field.
- **remediation:** Add an optional client-supplied idempotency key to `SendMessageRequest`/`SendSignalRequest` and thread it into `FireStartEventCore` so a retried call short-circuits to the previously-created instance ids, mirroring the "Pending-external-event op-id ledger" pattern in CLAUDE.md (`State.PendingOperations`/`AppliedOperations`, #657). See issue #743.
- **first seen:** 2026-08-10
- **last matched:** 2026-08-31

## pluggability/kafka-security-config-no-aspire-chart-surface
- **dimension:** pluggability
- **severity:** minor
- **signal:** Grep `charts/` and `src/Fleans/Fleans.Aspire/Program.cs` for `SecurityProtocol|SaslMechanism|SslCaLocation|MskIam` — no hits in either means the Kafka mTLS/SASL/MSK-IAM options implemented in `KafkaClientConfigExtensions.ApplySecurity` and the `Fleans.Streaming.Kafka.AwsMsk` package have no typed `values.yaml` or `Program.cs` surface, only the untyped `extraEnv` escape hatch.
- **remediation:** Add `streaming.kafka.securityProtocol`/`saslMechanism`/`saslUsername`/`sslCaLocation`/etc. (or an `existingSecret`-backed sub-block for credentialed fields) to `charts/fleans/values.yaml`, thread them through `_helpers.tpl`'s Kafka branch, and add equivalent `FLEANS_KAFKA_*` passthrough to `Fleans.Aspire/Program.cs`'s `WithStreaming`. See issue #744.
- **first seen:** 2026-08-10
- **last matched:** 2026-08-31

## pluggability/auth-authority-missing-on-api-mcp
- **dimension:** pluggability
- **severity:** important
- **signal:** `Fleans.Aspire/Program.cs` calls `.WithEnvironment("Authentication__Authority", ...)` (and `ClientId`/`ClientSecret`) only on the `webProject` builder chain (around line 200) — grep the file for `Authentication__Authority` and confirm `apiProject` has no matching call. Both `Fleans.Api/Program.cs` and `Fleans.Mcp/Program.cs` gate JWT auth on `Authentication:Authority` being non-empty.
- **remediation:** Add the same `Authentication__*` env stampings to `apiProject` (and the Mcp project, if hosted separately in Aspire) mirroring the `webProject` block, and to `deployment-core.yaml`/`deployment-mcp.yaml` in the Helm chart (mirroring `deployment-web.yaml`) — or fold the block into `fleans.commonEnv` once, so all silos get consistent auth wiring. See issue #747.
- **first seen:** 2026-08-17
- **last matched:** 2026-08-31

## scalability/unpaged-getpendingusertasks-interface-overload
- **dimension:** scalability
- **severity:** important
- **signal:** `WorkflowQueryService.GetPendingUserTasks(string?, string?)` (the 2-arg overload, `src/Fleans/Fleans.Persistence/WorkflowQueryService.cs:342-353`) calls `db.UserTasks.Where(t => t.TaskState != UserTaskLifecycleState.Completed).ToListAsync()` with no `Take`/page bound, then applies `ApplyUserTaskFilters` afterward in memory — distinct from the paged 3-arg overload, which already pushes down via `IUserTaskFilterStrategy` on Postgres.
- **remediation:** Delete the overload if it remains callerless (currently zero production callers — `UserTasksController.cs` uses only the paged overload), or reimplement it by delegating to the paged overload with a bounded page size, mirroring `EfCoreEventStore.ReadEventsAsync`'s `_maxEventsPerLoad` guard. See issue #749.
- **first seen:** 2026-08-24
- **last matched:** 2026-08-31

## pluggability/aspire-missing-streaming-tuning-envs
- **dimension:** pluggability
- **severity:** important
- **signal:** `charts/fleans/templates/_helpers.tpl` emits `Fleans__Streaming__Redis__TotalQueueCount` and `Fleans__Streaming__Kafka__QueueCount` from `values.yaml`, but `Fleans.Aspire/Program.cs`'s `WithStreaming` helper has no corresponding read/`WithEnvironment` call for either — grep `Program.cs` for `QueueCount` and confirm zero matches.
- **remediation:** Add `FLEANS_STREAMING_REDIS_TOTAL_QUEUE_COUNT` / `FLEANS_STREAMING_KAFKA_QUEUE_COUNT` reads in `Program.cs` and stamp the corresponding env vars via `WithEnvironment` inside `WithStreaming`, following the explicit-default pattern already used for `coreRole`/`persistenceProvider`. See issue #750.
- **first seen:** 2026-08-24
- **last matched:** 2026-08-31

## reliability/timer-start-scheduler-reminder-redelivery
- **dimension:** reliability
- **severity:** blocking
- **signal:** `TimerStartEventSchedulerGrain.FireTimerStartEvent` (`src/Fleans/Fleans.Application/Grains/TimerStartEventSchedulerGrain.cs`) calls `child.StartWorkflow()` (line 80) before `State.IncrementFireCount(); await _state.WriteStateAsync()` (lines 82-83), and `ReceiveReminder` calls `FireTimerStartEvent()` unconditionally with no pre-call stale/dedup check — search for the absence of a guard analogous to `WorkflowExecution.IsTimerFireStale` at the top of either method.
- **remediation:** Persist a durable per-tick fired-marker in `TimerStartEventSchedulerState` before calling `StartWorkflow()`, and check it at entry to `FireTimerStartEvent`/`ReceiveReminder`, mirroring `IsTimerFireStale`'s pre-call-check pattern used by `TimerCallbackGrain.ReceiveReminder`. Add the method to CLAUDE.md's stream-redelivery idempotency guard-set once fixed. See issue #751.
- **first seen:** 2026-08-24
- **last matched:** 2026-08-31

## reliability/custom-task-plugin-cancellation-redelivery-side-effect
- **dimension:** reliability
- **severity:** important
- **signal:** `CustomTaskHandlerBase.OnNextAsync` (`src/Fleans/Fleans.Worker/CustomTasks/CustomTaskHandlerBase.cs`) re-throws on `OperationCanceledException` when `_grainLifetimeCts.IsCancellationRequested` to force stream redelivery. `docs/conventions/adding-a-custom-task-plugin.md` has zero mentions of "idempot" — grep the doc to confirm. `RestCallerHandler.ExecuteAsync`'s `idempotencyKeyHeader` (`src/Fleans/Fleans.Plugins.RestCaller/RestCallerHandler.cs:89-96`) remains opt-in rather than default-on.
- **remediation:** Strengthen the plugin-authoring doc with an explicit "must be safe to invoke twice under deactivation-triggered redelivery" requirement and a concrete idempotency-key pattern; consider defaulting `RestCallerHandler` to auto-generate an idempotency key for non-GET requests rather than requiring opt-in. See issue #752.
- **first seen:** 2026-08-24
- **last matched:** 2026-08-31

## scalability/unbounded-registered-events-snapshot
- **dimension:** scalability
- **severity:** important
- **signal:** `WorkflowQueryService.GetRegisteredEventsAsync` in `src/Fleans/Fleans.Persistence/WorkflowQueryService.cs` issues five `.ToListAsync()` calls (over `MessageStartEventRegistrations`, `SignalStartEventRegistrations`, `ConditionalStartEventListeners`, `MessageSubscriptions`, `SignalSubscriptions`) with no `Take`/`PageRequest` bound on any of them — unlike every other list-query method in the same file. Consumed unpaginated by `Fleans.Web/Components/Pages/Events.razor`'s `FluentDataGrid`s.
- **remediation:** Apply the same `PageRequest`/`Take` pattern used by `GetInstancesByKey`/`GetAllProcessDefinitions` (same file) to each of the five sub-queries, and switch `Events.razor` to `FluentDataGrid`'s paginated/virtualized mode. See issue #754.
- **first seen:** 2026-08-31
- **last matched:** 2026-08-31
- **first seen:** 2026-08-24
- **last matched:** 2026-08-31
