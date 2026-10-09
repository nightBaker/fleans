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
- **remediation:** (1) Short-term: add a configurable `MaxEventsPerLoad` guard and throw when exceeded. (2) Periodic snapshotting (every 100 events + on graceful deactivation) is now implemented in `WorkflowInstance.cs` — ensure snapshot write errors surface loudly rather than leaving `_lastSnapshotVersion` stale. See issue #414.
- **first seen:** 2026-04-28
- **last matched:** 2026-05-11

## scalability/user-task-full-table-materialize
- **dimension:** scalability
- **severity:** minor
- **signal:** `WorkflowQueryService.GetPendingUserTasks` (the paged overload) in `src/Fleans/Fleans.Persistence/WorkflowQueryService.cs` calls `sievedQuery.ToListAsync()` before the `ApplyUserTaskFilters` call — the assignee/candidateGroup WHERE clause is applied in memory after the full non-completed user-task table is loaded. Look for `ToListAsync()` followed by `ApplyUserTaskFilters(` with no intervening DB-level WHERE on assignee or candidateGroup.
- **remediation:** Add provider-specific EF Core expressions for `CandidateUsers`/`CandidateGroups` JSON array containment (`@>` / `?` on PostgreSQL via Npgsql's `EF.Functions`) so the filter executes server-side on the Postgres provider. The SQLite path can retain the in-memory fallback. Follow the `RelationalModelCustomizer` subclass pattern in `Fleans.Persistence.Sqlite` and `Fleans.Persistence.PostgreSql`. See issue #415.
- **first seen:** 2026-04-28
- **last matched:** 2026-05-11

## pluggability/role-env-aspire-chart-parity
- **dimension:** pluggability
- **severity:** minor
- **signal:** `Fleans.Aspire/Program.cs` does not inject `Fleans__Role` via `WithEnvironment` on any project, while `charts/fleans/templates/deployment-core.yaml` and `deployment-worker.yaml` both set `Fleans__Role`. Search for the absence of `Fleans__Role` in `src/Fleans/Fleans.Aspire/Program.cs`.
- **remediation:** Add `WithEnvironment("Fleans__Role", builder.Configuration["FLEANS_ROLE"] ?? "Combined")` when wiring the `fleans-core` project in Aspire. Document the `FLEANS_ROLE` local-dev override in CLAUDE.md under the Core/Worker role split section. See issue #416.
- **first seen:** 2026-04-28
- **last matched:** 2026-04-28

## pluggability/streaming-azurequeue-chart-gap
- **dimension:** pluggability
- **severity:** important
- **signal:** `charts/fleans/templates/_helpers.tpl` `fleans.commonEnv` has a branch for `kafka` but no branch for `azurequeue`, while `FleanStreamingExtensions.cs` and `Fleans.Aspire/Program.cs` both support `AzureQueue` as a third streaming provider — search for the absence of `azurequeue` or `AzureQueue` in `_helpers.tpl` and `values.yaml`.
- **remediation:** Add an `{{- if eq (lower .Values.streaming.provider) "azurequeue" }}` branch to `_helpers.tpl` setting `Fleans__Streaming__Provider=AzureQueue` and `Fleans__Streaming__AzureQueue__ConnectionString`. Add `streaming.azureQueue.connectionString` to `values.yaml` (empty default). Update `values.yaml` comment to `# Memory | Kafka | AzureQueue`. See issue #551.
- **first seen:** 2026-05-11
- **last matched:** 2026-05-11

## reliability/orphaned-grain-interface
- **dimension:** reliability
- **severity:** minor
- **signal:** `src/Fleans/Fleans.Application/Events/Handlers/IWorkflowEventsHandler.cs` declares an Orleans grain interface (`IGrainWithStringKey`) with zero implementing classes and zero callers anywhere in the codebase — grep for `IWorkflowEventsHandler` outside the declaration file to confirm absence.
- **remediation:** Delete `IWorkflowEventsHandler.cs`. If a handler is planned, add an inline comment with the tracking issue number so the interface is not mistaken for dead code. See issue #552.
- **first seen:** 2026-05-11
- **last matched:** 2026-05-11
