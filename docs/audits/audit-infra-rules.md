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
- **signal:** `WorkflowQueryService.GetPendingUserTasks` (paged overload) in `src/Fleans/Fleans.Persistence/WorkflowQueryService.cs` — look for the ABSENCE of a `_userTaskFilter.PushesToSql` guard that separates a server-side Postgres path (CountAsync + paginated ToListAsync) from the SQLite in-memory fallback. If both providers share the same `sievedQuery.ToListAsync()` before `ApplyUserTaskFilters(`, the Postgres pushdown is missing. Note: the SQLite in-memory path (inside the `else` branch when `PushesToSql` is false) is an accepted design limitation and should not re-trigger this rule on its own.
- **remediation:** Confirmed partially resolved (#415, closed 2026-05-11): Postgres path now uses `_userTaskFilter.PushesToSql` → `CountAsync` + paginated `ToListAsync`; SQLite path retains in-memory fallback by design. Rule fires again only if the `PushesToSql` guard is removed or the Postgres branch regresses.
- **first seen:** 2026-04-28
- **last matched:** 2026-06-15

## pluggability/role-env-aspire-chart-parity
- **dimension:** pluggability
- **severity:** minor
- **signal:** `Fleans.Aspire/Program.cs` does not inject `Fleans__Role` via `WithEnvironment` on any project, while `charts/fleans/templates/deployment-core.yaml` and `deployment-worker.yaml` both set `Fleans__Role`. Search for the absence of `Fleans__Role` in `src/Fleans/Fleans.Aspire/Program.cs`.
- **remediation:** Add `WithEnvironment("Fleans__Role", builder.Configuration["FLEANS_ROLE"] ?? "Combined")` when wiring the `fleans-core` project in Aspire. Document the `FLEANS_ROLE` local-dev override in CLAUDE.md under the Core/Worker role split section. See issue #416.
- **first seen:** 2026-04-28
- **last matched:** 2026-04-28

## pluggability/persistence-provider-no-chart-fail-guard
- **dimension:** pluggability
- **severity:** minor
- **signal:** `charts/fleans/templates/_helpers.tpl` inside `fleans.commonEnv` — look for the absence of a `{{- fail ... }}` guard on `persistence.provider` immediately before the `Persistence__Provider` env entry. The streaming provider has an equivalent guard (search for `fail (printf "Unsupported streaming.provider"`); persistence should too.
- **remediation:** Add `{{- $persistenceProvider := lower .Values.persistence.provider }}` + `{{- if not (has $persistenceProvider (list "sqlite" "postgres")) }}` + `{{- fail ... }}` + `{{- end }}` before the `Persistence__Provider` env entry, mirroring lines 109-112 of the streaming block. See issue #675.
- **first seen:** 2026-06-15
- **last matched:** 2026-06-15

## pluggability/reminders-provider-no-chart-input
- **dimension:** pluggability
- **severity:** important
- **signal:** `charts/fleans/templates/_helpers.tpl` `fleans.commonEnv` — look for the absence of a `Fleans__Reminders__Provider` env entry and a corresponding `values.yaml` key `reminders.provider`. The silo reads `Fleans:Reminders:Provider` via `FleansRemindersExtensions.ResolveRemindersConfiguration` (`src/Fleans/Fleans.ServiceDefaults/Reminders/FleansRemindersExtensions.cs`); if no chart input exists, operators cannot switch to Postgres reminders without `extraEnv`.
- **remediation:** Add `reminders.provider: Redis` to `values.yaml`; add a `fail` guard and `Fleans__Reminders__Provider` env entry to `_helpers.tpl` `fleans.commonEnv`, matching the streaming and persistence provider patterns. See issue #676.
- **first seen:** 2026-06-15
- **last matched:** 2026-06-15

## reliability/logger-message-convention-violation
- **dimension:** reliability
- **severity:** minor
- **signal:** Search `src/Fleans/Fleans.Streaming.Kafka/` and `src/Fleans/Fleans.Application/Effects/EffectDispatcher.cs` for `_logger\.Log(Error|Warning|Information|Debug)` calls. Any match that is NOT inside a `[LoggerMessage]`-generated partial method violates the convention. Currently: `KafkaQueueAdapter.cs:85`, `KafkaQueueAdapterFactory.cs:77,99,108`, `KafkaQueueAdapterReceiver.cs:44,70,88,108,125`, `EffectDispatcher.cs:40`.
- **remediation:** Make each class `partial`; replace `_logger.Log*()` calls with `[LoggerMessage]`-attributed `private partial void Log...()` methods; allocate EventId ranges in `docs/conventions/observability-eventids.md`. Canonical pattern: `src/Fleans/Fleans.Application/Grains/WorkflowInstance.Logging.cs`. See issue #714.
- **first seen:** 2026-06-15
- **last matched:** 2026-06-15
