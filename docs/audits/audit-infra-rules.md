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
- **last matched:** 2026-05-25

## pluggability/role-env-aspire-chart-parity
- **dimension:** pluggability
- **severity:** minor
- **signal:** `Fleans.Aspire/Program.cs` does not inject `Fleans__Role` via `WithEnvironment` on any project, while `charts/fleans/templates/deployment-core.yaml` and `deployment-worker.yaml` both set `Fleans__Role`. Search for the absence of `Fleans__Role` in `src/Fleans/Fleans.Aspire/Program.cs`.
- **remediation:** Add `WithEnvironment("Fleans__Role", builder.Configuration["FLEANS_ROLE"] ?? "Combined")` when wiring the `fleans-core` project in Aspire. Document the `FLEANS_ROLE` local-dev override in CLAUDE.md under the Core/Worker role split section. See issue #416.
- **first seen:** 2026-04-28
- **last matched:** 2026-04-28

## reliability/mcp-tcp-probe-not-http
- **dimension:** reliability
- **severity:** important
- **signal:** `charts/fleans/templates/deployment-mcp.yaml` uses `tcpSocket` for both `livenessProbe` and `readinessProbe` — look for `tcpSocket:` under either probe block in that file. All other Fleans deployments (core, worker, web, custom-worker) use `httpGet` with `/alive` (liveness) and `/health` (readiness).
- **remediation:** Replace both `tcpSocket` blocks in `deployment-mcp.yaml` with `httpGet` probes matching `deployment-core.yaml:44-52`. No application changes needed — `Fleans.Mcp/Program.cs` calls `app.MapDefaultEndpoints()` which registers `/alive` and `/health` via `Fleans.ServiceDefaults/Extensions.cs:110-113`. See issue #609.
- **first seen:** 2026-05-25
- **last matched:** 2026-05-25

## pluggability/chart-persistence-provider-no-fail-guard
- **dimension:** pluggability
- **severity:** minor
- **signal:** `charts/fleans/templates/_helpers.tpl` has a `{{- fail ... }}` guard for unknown `streaming.provider` values but no equivalent guard for `persistence.provider` — search for `fail` adjacent to `persistence.provider` in `_helpers.tpl`. An unknown value passes `helm template` silently and only fails at silo startup.
- **remediation:** Add a Helm `fail` guard immediately before the `Persistence__Provider` env var entry in `_helpers.tpl`, mirroring the streaming guard at line 110-112: `{{- if not (has (lower .Values.persistence.provider) (list "sqlite" "postgres")) }}{{- fail (printf "Unsupported persistence.provider %q. Valid: Sqlite, Postgres." .Values.persistence.provider) }}{{- end }}`. See issue #675.
- **first seen:** 2026-05-25
- **last matched:** 2026-05-25

## pluggability/reminders-provider-chart-gap
- **dimension:** pluggability
- **severity:** minor
- **signal:** `charts/fleans/templates/_helpers.tpl` `fleans.commonEnv` template does not set `Fleans__Reminders__Provider` and `charts/fleans/values.yaml` has no `reminders.provider` key — search for `Reminders` in both files. `FleansRemindersExtensions.ResolveRemindersConfiguration` supports `Redis` and `Postgres` providers and throws on unknowns, but only `extraEnv` can configure this in a Helm install.
- **remediation:** Add `reminders.provider: Redis` to `values.yaml` and a corresponding `fail`-guarded `Fleans__Reminders__Provider` env var entry in `fleans.commonEnv` in `_helpers.tpl`, matching the streaming provider pattern. See issue #676.
- **first seen:** 2026-05-25
- **last matched:** 2026-05-25
