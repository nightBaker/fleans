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
- **signal:** `WorkflowQueryService.GetPendingUserTasks` (the non-paged overload) in `src/Fleans/Fleans.Persistence/WorkflowQueryService.cs` calls `.ToListAsync()` on `db.UserTasks.Where(t => t.TaskState != Completed)` then passes the result to `ApplyUserTaskFilters` — no assignee/candidateGroup predicate pushed to the DB. The paged overload now uses `_userTaskFilter.GetFilteredBase` (SQL-push on PG, in-memory on SQLite) but the non-paged path does not. Look for `ToListAsync()` followed by `ApplyUserTaskFilters(` in the non-paged overload of `GetPendingUserTasks`.
- **remediation:** Thread `_userTaskFilter.GetFilteredBase` into the non-paged overload the same way the paged overload does, removing the unconditional full-table load. The non-paged path is used by the API's unfiltered task list endpoint; as task volume grows it becomes a silo heap pressure point. See issue #415 (closed; residual gap in non-paged overload).
- **first seen:** 2026-04-28
- **last matched:** 2026-06-22

## pluggability/role-env-aspire-chart-parity
- **dimension:** pluggability
- **severity:** minor
- **signal:** `Fleans.Aspire/Program.cs` does not inject `Fleans__Role` via `WithEnvironment` on any project, while `charts/fleans/templates/deployment-core.yaml` and `deployment-worker.yaml` both set `Fleans__Role`. Search for the absence of `Fleans__Role` in `src/Fleans/Fleans.Aspire/Program.cs`.
- **remediation:** Add `WithEnvironment("Fleans__Role", builder.Configuration["FLEANS_ROLE"] ?? "Combined")` when wiring the `fleans-core` project in Aspire. Document the `FLEANS_ROLE` local-dev override in CLAUDE.md under the Core/Worker role split section. See issue #416.
- **first seen:** 2026-04-28
- **last matched:** 2026-04-28

## pluggability/reminders-provider-chart-gap
- **dimension:** pluggability
- **severity:** important
- **signal:** `charts/fleans/templates/_helpers.tpl` `fleans.commonEnv` does not inject `Fleans__Reminders__Provider`, and `charts/fleans/values.yaml` has no `reminders:` block — search for the absence of `Fleans__Reminders__Provider` in `_helpers.tpl` and `reminders:` in `values.yaml`.
- **remediation:** (1) Add `reminders: provider: Redis` to `values.yaml` with a note that `Postgres` requires `persistence.provider: Postgres`. (2) Add a `fail` guard and `Fleans__Reminders__Provider` env entry to `fleans.commonEnv` in `_helpers.tpl`, mirroring the streaming-provider pattern. Ensures operators can switch the BPMN timer durability backend without undocumented `extraEnv` workarounds. See issue #676.
- **first seen:** 2026-06-22
- **last matched:** 2026-06-22

## pluggability/persistence-helm-render-time-validation
- **dimension:** pluggability
- **severity:** minor
- **signal:** `charts/fleans/templates/_helpers.tpl` `fleans.commonEnv` sets `Persistence__Provider` from `.Values.persistence.provider` without a Helm `fail` guard, while `Fleans__Streaming__Provider` has an explicit `{{- if not (has $provider ...) }}{{- fail ... }}` guard — search for absence of `fail` adjacent to the `Persistence__Provider` env entry in `_helpers.tpl`.
- **remediation:** Add `{{- $persistenceProvider := lower .Values.persistence.provider }}{{- if not (has $persistenceProvider (list "sqlite" "postgres")) }}{{- fail (printf "Unsupported persistence.provider %q ..." .Values.persistence.provider) }}{{- end }}` immediately before the `Persistence__Provider` line, matching the streaming guard pattern. Catches typos at `helm template` time instead of silo startup. See issue #675.
- **first seen:** 2026-06-22
- **last matched:** 2026-06-22
