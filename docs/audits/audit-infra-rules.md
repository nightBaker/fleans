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
- **resolved:** 2026-08-17 — the else arm now throws `ArgumentException` listing supported providers; no silent fallback remains.

## scalability/event-store-unbounded-read
- **dimension:** scalability
- **severity:** important
- **signal:** `EfCoreEventStore.ReadEventsAsync` in `src/Fleans/Fleans.Persistence/Events/EfCoreEventStore.cs` calls `.ToListAsync()` on a `WorkflowEvents` query with no `Take(limit)` — search for the absence of `Take(` between the `.Where(e => e.GrainId ==` filter and the `.ToListAsync()` call in that method.
- **remediation:** (1) Short-term: add a configurable `MaxEventsPerLoad` guard and throw when exceeded. (2) Medium-term: implement `JournaledGrain` snapshotting so `ReadEventsAsync` is called with `afterVersion = snapshotVersion` instead of 0 on cold activation. The `ICustomStorageInterface` hook in `WorkflowInstance.cs` is the natural snapshot persistence point. See issue #414.
- **first seen:** 2026-04-28
- **last matched:** 2026-04-28
- **resolved:** 2026-08-17 — `ReadEventsAsync` now applies `.Take(limit)` before `.ToListAsync()`.

## scalability/user-task-full-table-materialize
- **dimension:** scalability
- **severity:** minor
- **signal:** `WorkflowQueryService.GetPendingUserTasks` (the paged overload) in `src/Fleans/Fleans.Persistence/WorkflowQueryService.cs` calls `sievedQuery.ToListAsync()` before the `ApplyUserTaskFilters` call — the assignee/candidateGroup WHERE clause is applied in memory after the full non-completed user-task table is loaded. Look for `ToListAsync()` followed by `ApplyUserTaskFilters(` with no intervening DB-level WHERE on assignee or candidateGroup.
- **remediation:** Add provider-specific EF Core expressions for `CandidateUsers`/`CandidateGroups` JSON array containment (`@>` / `?` on PostgreSQL via Npgsql's `EF.Functions`) so the filter executes server-side on the Postgres provider. The SQLite path can retain the in-memory fallback. Follow the `RelationalModelCustomizer` subclass pattern in `Fleans.Persistence.Sqlite` and `Fleans.Persistence.PostgreSql`. See issue #415.
- **first seen:** 2026-04-28
- **last matched:** 2026-04-28
- **resolved:** 2026-08-17 — Postgres path now pushes assignee/candidateGroup filters to SQL via `IUserTaskFilterStrategy`; SQLite retains the in-memory fallback, which is the sanctioned exception in the original remediation. See #415.

## pluggability/role-env-aspire-chart-parity
- **dimension:** pluggability
- **severity:** minor
- **signal:** `Fleans.Aspire/Program.cs` does not inject `Fleans__Role` via `WithEnvironment` on any project, while `charts/fleans/templates/deployment-core.yaml` and `deployment-worker.yaml` both set `Fleans__Role`. Search for the absence of `Fleans__Role` in `src/Fleans/Fleans.Aspire/Program.cs`.
- **remediation:** Add `WithEnvironment("Fleans__Role", builder.Configuration["FLEANS_ROLE"] ?? "Combined")` when wiring the `fleans-core` project in Aspire. Document the `FLEANS_ROLE` local-dev override in CLAUDE.md under the Core/Worker role split section. See issue #416.
- **first seen:** 2026-04-28
- **last matched:** 2026-04-28
- **resolved:** 2026-08-17 — `Fleans__Role` is now stamped via `WithEnvironment` on the core/worker projects in `Fleans.Aspire/Program.cs`.

## pluggability/auth-authority-missing-on-api-mcp
- **dimension:** pluggability
- **severity:** important
- **signal:** `Fleans.Aspire/Program.cs` calls `.WithEnvironment("Authentication__Authority", ...)` (and `ClientId`/`ClientSecret`) only on the `webProject` builder chain, not on `apiProject` or the Mcp project builder — search for the absence of a second such call scoped to those two projects. Likewise `charts/fleans/templates/deployment-core.yaml` and `deployment-mcp.yaml` include only `fleans.commonEnv` with no direct `Authentication__Authority` env entry, while `deployment-web.yaml` has one. Both `Fleans.Api/Program.cs` and `Fleans.Mcp/Program.cs` gate JWT auth on `Authentication:Authority` being non-empty.
- **remediation:** Add the same three `Authentication__*` env stampings to `apiProject`/Mcp in `Fleans.Aspire/Program.cs` (mirroring the `webProject` block) and to `deployment-core.yaml`/`deployment-mcp.yaml` in the chart (mirroring `deployment-web.yaml`), or fold the block into `fleans.commonEnv` once. See issue #747.
- **first seen:** 2026-08-17
- **last matched:** 2026-08-17
