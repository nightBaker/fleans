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
- **last matched:** 2026-05-18

## pluggability/role-env-aspire-chart-parity
- **dimension:** pluggability
- **severity:** minor
- **signal:** `Fleans.Aspire/Program.cs` does not inject `Fleans__Role` via `WithEnvironment` on any project, while `charts/fleans/templates/deployment-core.yaml` and `deployment-worker.yaml` both set `Fleans__Role`. Search for the absence of `Fleans__Role` in `src/Fleans/Fleans.Aspire/Program.cs`.
- **remediation:** Add `WithEnvironment("Fleans__Role", builder.Configuration["FLEANS_ROLE"] ?? "Combined")` when wiring the `fleans-core` project in Aspire. Document the `FLEANS_ROLE` local-dev override in CLAUDE.md under the Core/Worker role split section. See issue #416.
- **first seen:** 2026-04-28
- **last matched:** 2026-04-28

## reliability/mcp-deployment-tcpsocket-probes
- **dimension:** reliability
- **severity:** important
- **signal:** `charts/fleans/templates/deployment-mcp.yaml` uses `tcpSocket` for both `livenessProbe` and `readinessProbe` — look for `tcpSocket:` under `livenessProbe:` or `readinessProbe:` in that file. All other deployments (core, worker, web, custom-worker) use `httpGet` on `/alive` and `/health`.
- **remediation:** Replace the `tcpSocket` probes with `httpGet` on path `/alive` (liveness) and `/health` (readiness) at the container port, matching `deployment-core.yaml:44-52`. MCP calls `app.MapDefaultEndpoints()` via `Fleans.ServiceDefaults/Extensions.cs` which registers both paths. See issue #609.
- **first seen:** 2026-05-18
- **last matched:** 2026-05-18

## pluggability/chart-streaming-redis-not-first-class
- **dimension:** pluggability
- **severity:** important
- **signal:** `charts/fleans/values.yaml` documents `streaming.provider` as "Memory | Kafka" with no Redis option, and `charts/fleans/templates/_helpers.tpl` in `fleans.commonEnv` only injects `Fleans__Streaming__Provider` when the value is "kafka" — look for the absence of a redis branch in the `{{- if eq (lower .Values.streaming.provider) "kafka" }}` block and the absence of `Fleans__Streaming__Redis__TotalQueueCount` anywhere in the templates directory.
- **remediation:** (1) Add a `redis` branch to `_helpers.tpl` that injects `Fleans__Streaming__Provider=Redis` and optionally `Fleans__Streaming__Redis__TotalQueueCount`. (2) Change `values.yaml` default `provider` to `Redis` with a comment "Redis | Kafka | AzureQueue | Memory" and add a `redis.totalQueueCount` knob (default 8). (3) Update the self-host guides to document Redis as the recommended streaming provider for multi-silo k8s deployments. See issue #610.
- **first seen:** 2026-05-18
- **last matched:** 2026-05-18

## scalability/editor-process-definitions-unbounded
- **dimension:** scalability
- **severity:** minor
- **signal:** `WorkflowQueryService.GetAllProcessDefinitions()` (the no-arg overload) in `src/Fleans/Fleans.Persistence/WorkflowQueryService.cs` calls `.ToListAsync()` with no `Take` limit, and is called from `src/Fleans/Fleans.Web/Components/Pages/Editor.razor:435` to populate the deploy dialog — look for `GetAllProcessDefinitions()` (no PageRequest argument) called outside a test file.
- **remediation:** Either (a) switch `Editor.razor` to the paged overload and load definitions lazily/on-demand, or (b) add a hard cap (`Take(500)`) to the non-paged overload with a warning log when truncated, to prevent editor stalls in large deployments. The paged overload already exists in the same service. See issue #611.
- **first seen:** 2026-05-18
- **last matched:** 2026-05-18

## reliability/orphaned-grain-interfaces
- **dimension:** reliability
- **severity:** minor
- **signal:** Search `src/Fleans/Fleans.Application/Events/Handlers/` for any `interface … : IGrainWithStringKey` (or other `IGrainWith*Key`) declaration whose name does not have a corresponding implementing class anywhere in `src/Fleans/` — look for `IWorkflowEventsHandler` as the known instance.
- **remediation:** Delete orphaned grain interfaces. Orleans scans assemblies for grain types; an implemented-later stub with no prior plan causes silent registration and potential mis-routing. If the interface is a future placeholder, add an inline comment with the tracking issue. See issue #552.
- **first seen:** 2026-05-18
- **last matched:** 2026-05-18

## pluggability/azurequeue-streaming-chart-parity
- **dimension:** pluggability
- **severity:** important
- **signal:** `charts/fleans/templates/_helpers.tpl` in `fleans.commonEnv` has no `azurequeue` branch — look for the absence of `Fleans__Streaming__AzureQueue__ConnectionString` in the templates directory. Aspire injects this via `Fleans__Streaming__Provider=AzureQueue` + `Fleans__Streaming__AzureQueue__ConnectionString` (see `src/Fleans/Fleans.Aspire/Program.cs` lines ~148-154).
- **remediation:** Add an `azurequeue` branch to `_helpers.tpl` and a `streaming.azureQueue.connectionString` value to `values.yaml`. Update `values.yaml` comment from "Memory | Kafka" to "Redis | Kafka | AzureQueue | Memory". Update the self-host guides. See issue #551.
- **first seen:** 2026-05-18
- **last matched:** 2026-05-18
