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

## reliability/logger-message-convention-violation
- **dimension:** reliability
- **severity:** minor
- **signal:** Grep `src/Fleans` for `_logger\.Log(Error|Warning|Information|Debug|Trace|Critical)\(` — CLAUDE.md mandates `[LoggerMessage]` source generators exclusively, so any direct `ILogger.Log*()` extension call on a non-partial (or partial-but-still-calling) class is a violation. Check each hit's containing class for a `[LoggerMessage]`-attributed alternative.
- **remediation:** Make each violating class `partial` (if not already) and replace the `_logger.Log*()` call with a `[LoggerMessage]`-attributed `private partial void Log(...)` method, assigning EventIds from `docs/conventions/observability-eventids.md`. See `src/Fleans/Fleans.Application/Grains/WorkflowInstance.Logging.cs` for the canonical pattern. See issue #714.
- **first seen:** 2026-06-08
- **last matched:** 2026-08-10

## pluggability/reminders-provider-chart-gap
- **dimension:** pluggability
- **severity:** important
- **signal:** `charts/fleans/values.yaml` has no `reminders:` key and `charts/fleans/templates/_helpers.tpl`'s `fleans.commonEnv` never emits `Fleans__Reminders__Provider`, while `FleansRemindersExtensions.ResolveRemindersConfiguration` (`src/Fleans/Fleans.ServiceDefaults/Reminders/FleansRemindersExtensions.cs`) reads `Fleans:Reminders:Provider` and supports both `Redis` and `Postgres`. Grep `charts/fleans` for `Reminders` (case-insensitive) — absence of any hit confirms the gap.
- **remediation:** Add a `reminders.provider` key to `values.yaml` (default `Redis`) and a corresponding `Fleans__Reminders__Provider` entry (with a `fail` guard rejecting unknown values, mirroring the `streaming.provider` guard) to `_helpers.tpl`'s `fleans.commonEnv`. See issue #676.
- **first seen:** 2026-05-25
- **last matched:** 2026-08-10

## pluggability/persistence-helm-render-time-validation
- **dimension:** pluggability
- **severity:** minor
- **signal:** `charts/fleans/templates/_helpers.tpl` has a `fail` guard for `streaming.provider` (`{{- if not (has $provider (list "memory" "redis" "kafka" "azurequeue")) }}`) but no equivalent guard for `persistence.provider` before it's passed through to `Persistence__Provider`. Grep the file for `Persistence__Provider` and confirm no `fail` call appears near it.
- **remediation:** Add a `fail` guard immediately above the `Persistence__Provider` env entry in `_helpers.tpl`, rejecting any `persistence.provider` value outside `sqlite`/`postgres` (case-insensitive), mirroring the streaming guard's pattern. See issue #675.
- **first seen:** 2026-05-25
- **last matched:** 2026-08-10

## pluggability/chart-custom-worker-not-a-plugin-host
- **dimension:** pluggability
- **severity:** important
- **signal:** `charts/fleans/templates/deployment-custom-worker.yaml` builds its container from `.Values.image.api.repository` (the stock engine image) and stamps `Fleans__Role: Worker`, not `Plugin` — grep the file for `image.api.repository` and `Fleans__Role` to confirm both, then check `values.yaml`'s `customWorker` block for the absence of any `image.repository` override key. Cross-check against `ValidatePluginRole` in `src/Fleans/Fleans.Worker/Hosting/PluginHostExtensions.cs`, which rejects `Role=Worker`/`Core` for a real plugin host.
- **remediation:** Either remove `deployment-custom-worker.yaml`/`customWorker.*` from the chart and document the external `fleans-custom-worker-example` template repo as the only supported plugin-host path, or add a required `customWorker.image.repository` value (no in-tree default) and stamp `Fleans__Role: Plugin` to match the three-role contract in `docs/conventions/placement-and-roles.md`. See issue #741.
- **first seen:** 2026-08-10
- **last matched:** 2026-08-10

## reliability/signal-broadcast-drops-subscribers-on-delivery-failure
- **dimension:** reliability
- **severity:** important
- **signal:** `SignalCorrelationGrain.BroadcastSignal` in `src/Fleans/Fleans.Application/Grains/SignalCorrelationGrain.cs` clears and persists `_state.State.Subscriptions` before the fan-out loop, and its per-subscriber `catch` block only logs (`LogDeliveryFailed`) with no re-add to `Subscriptions`. Compare against `MessageCorrelationGrain.DeliverMessage`'s catch block in `src/Fleans/Fleans.Application/Grains/MessageCorrelationGrain.cs`, which restores `Status = Subscribed` on failure — the signal grain has no equivalent restore.
- **remediation:** On per-subscriber delivery failure inside `BroadcastSignal`, re-add the failed `SignalSubscription` back to `_state.State.Subscriptions` (under the `_mutex`) and persist, mirroring `MessageCorrelationGrain`'s catch-and-restore pattern. See issue #742.
- **first seen:** 2026-08-10
- **last matched:** 2026-08-10

## reliability/http-start-event-endpoints-not-idempotent
- **dimension:** reliability
- **severity:** important
- **signal:** `StartEventListenerGrainBase.FireStartEventCore` in `src/Fleans/Fleans.Application/Grains/StartEventListenerGrainBase.cs` calls `Guid.NewGuid()` per registered process definition with no caller-supplied idempotency key anywhere in the `ExecutionController.SendMessage`/`SendSignal` → `WorkflowCommandService` → `FireMessageStartEvent`/`FireSignalStartEvent` call chain. Grep `SendMessageRequest`/`SendSignalRequest` for the absence of an idempotency-key field.
- **remediation:** Add an optional client-supplied idempotency key to `SendMessageRequest`/`SendSignalRequest` and thread it into `FireStartEventCore` so a retried call short-circuits to the previously-created instance ids, mirroring the "Pending-external-event op-id ledger" pattern in CLAUDE.md (`State.PendingOperations`/`AppliedOperations`, #657). See issue #743.
- **first seen:** 2026-08-10
- **last matched:** 2026-08-10

## pluggability/kafka-security-config-no-aspire-chart-surface
- **dimension:** pluggability
- **severity:** minor
- **signal:** Grep `charts/` and `src/Fleans/Fleans.Aspire/` for `SecurityProtocol|SaslMechanism|SslCaLocation|MskIam` — no hits means the Kafka mTLS/SASL/MSK-IAM options documented in `docs/conventions/streaming.md` have no typed `values.yaml` or `Fleans.Aspire/Program.cs` surface, only the untyped `extraEnv` escape hatch.
- **remediation:** Add `streaming.kafka.securityProtocol`/`saslMechanism`/`saslUsername`/`sslCaLocation`/etc. (or an `existingSecret`-backed sub-block for credentialed fields) to `charts/fleans/values.yaml`, thread them through `_helpers.tpl`'s Kafka branch, and add equivalent `FLEANS_KAFKA_*` passthrough to `Fleans.Aspire/Program.cs`'s `WithStreaming`. See issue #744.
- **first seen:** 2026-08-10
- **last matched:** 2026-08-10
