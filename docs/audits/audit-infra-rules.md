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
- **signal:** `WorkflowQueryService.GetPendingUserTasks` in `src/Fleans/Fleans.Persistence/WorkflowQueryService.cs` has two unbounded materialization paths: (1) the no-arg overload calls `.ToListAsync()` with only a `TaskState != Completed` filter before `ApplyUserTaskFilters`; (2) the paged overload's SQLite branch (`_userTaskFilter.PushesToSql == false`) calls `sievedQuery.ToListAsync()` before in-memory filtering. Look for `ToListAsync()` followed by `ApplyUserTaskFilters(` in either overload.
- **remediation:** The Postgres paged path now pushes filters to SQL via `IUserTaskFilterStrategy.PushesToSql`. Remaining gaps: (1) extend the no-arg overload with the same strategy, or deprecate/remove it in favour of the paged overload with a large-enough page. (2) The SQLite path may retain in-memory filtering, but should at minimum apply a hard cap (`Take(maxRows)`) before materialisation. See issue #415.
- **first seen:** 2026-04-28
- **last matched:** 2026-06-08

## pluggability/role-env-aspire-chart-parity
- **dimension:** pluggability
- **severity:** minor
- **signal:** `Fleans.Aspire/Program.cs` does not inject `Fleans__Role` via `WithEnvironment` on any project, while `charts/fleans/templates/deployment-core.yaml` and `deployment-worker.yaml` both set `Fleans__Role`. Search for the absence of `Fleans__Role` in `src/Fleans/Fleans.Aspire/Program.cs`.
- **remediation:** Add `WithEnvironment("Fleans__Role", builder.Configuration["FLEANS_ROLE"] ?? "Combined")` when wiring the `fleans-core` project in Aspire. Document the `FLEANS_ROLE` local-dev override in CLAUDE.md under the Core/Worker role split section. See issue #416.
- **first seen:** 2026-04-28
- **last matched:** 2026-04-28

## pluggability/reminders-provider-chart-aspire-gap
- **dimension:** pluggability
- **severity:** minor
- **signal:** `Fleans:Reminders:Provider` is read by `FleansRemindersExtensions.AddFleansReminders` in `src/Fleans/Fleans.ServiceDefaults/Reminders/FleansRemindersExtensions.cs` but absent from both `charts/fleans/values.yaml` (no `reminders.provider` key) and `src/Fleans/Fleans.Aspire/Program.cs` (no `WithEnvironment("Fleans__Reminders__Provider", ...)` call). Search for the absence of `Fleans__Reminders` in both files.
- **remediation:** (1) Add `reminders.provider: Redis` to `values.yaml` with a `Redis | Postgres` comment. (2) Add a `fail` guard plus `Fleans__Reminders__Provider` env injection to `_helpers.tpl`'s `fleans.commonEnv`, mirroring the streaming provider pattern at lines 109-133. (3) In `Fleans.Aspire/Program.cs`, stamp `Fleans__Reminders__Provider` when the operator explicitly sets it, so local dev parity is maintained. See issue #676.
- **first seen:** 2026-06-08
- **last matched:** 2026-06-08

## pluggability/persistence-helm-render-validation
- **dimension:** pluggability
- **severity:** minor
- **signal:** `charts/fleans/templates/_helpers.tpl` injects `Persistence__Provider` at line 101 without a `fail` guard, while the streaming provider has one at lines 110-112. Search for `Persistence__Provider` in `_helpers.tpl` and check whether a `{{- if not (has` guard appears before it.
- **remediation:** Add `{{- if not (has $persistenceProvider (list "sqlite" "postgres")) }} {{- fail ... }} {{- end }}` immediately before the `Persistence__Provider` env entry in `fleans.commonEnv`, mirroring the streaming guard. This surfaces misconfigured `persistence.provider` values at `helm template` time rather than at silo startup. See issue #675.
- **first seen:** 2026-06-08
- **last matched:** 2026-06-08

## reliability/direct-logger-extension-calls
- **dimension:** reliability
- **severity:** minor
- **signal:** Grep for `_logger\.Log` (LogError, LogWarning, LogInformation, LogDebug) in `src/Fleans/` excluding test projects — any match in a non-`partial` class violates the `[LoggerMessage]` source-generator convention in CLAUDE.md. Check `Fleans.Streaming.Kafka/` and `Fleans.Application/Effects/`.
- **remediation:** Make each violating class `partial`. Replace every `_logger.Log*()` call with a `[LoggerMessage]`-attributed `private partial void Log<Name>(...)` method. Allocate new EventId ranges in `docs/conventions/observability-eventids.md` for the Kafka streaming provider and `EffectDispatcher`. See `Fleans.Application/Grains/WorkflowInstance.Logging.cs` for the canonical pattern. See issue #714.
- **first seen:** 2026-06-08
- **last matched:** 2026-06-08
