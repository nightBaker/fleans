# Fleans

BPMN workflow engine built on Orleans. Architecture + implemented-BPMN table in `README.md`; design docs in `docs/plans/` (check before architectural changes). Editing this file → follow [`docs/conventions/documentation.md#maintaining-claudemd`](docs/conventions/documentation.md#maintaining-claudemd).

## Guiding principle: KISS

Choose the simplest design that satisfies the requirement: reuse existing types and paths, build for today's case, and add abstraction, configuration, or concurrency machinery only when a concrete need forces it. *Why:* every extra layer here (grains, streams, event sourcing) multiplies the states to reason about. *How to apply:* when two designs both work, ship the one with fewer moving parts and say why in the PR.

**Pre-1.0: no upgrade path from earlier releases.** Until `v1.0.0`, don't write code that carries data from a previous version forward — no SQLite `AdditiveColumns` backfills, no event upcasters / schema versioning, no fallbacks for old serialized grain state or stream payloads. *Why:* there are no production users to protect yet, and every compat shim is permanent complexity. *How to apply:* change schemas, events and state in place; a fresh database is the upgrade path (say so in the PR). Postgres EF migrations are still required — they're how a fresh database gets its schema.

## Build & Test

From `src/Fleans/` (Aspire is the startup project):

```bash
dotnet build
dotnet test                                                                       # unit + integration
dotnet test Fleans.E2E.Tests/Fleans.E2E.Tests.csproj --filter "TestCategory=E2E"   # Playwright E2E (Docker required)
dotnet run --project Fleans.Aspire                                                # full stack (Api + Web + Redis)
```

Test logs are huge — load them only at the verification step.

## Git

Work on `feature/<desc>` or `fix/<desc>` branches off `main`, then open a PR (CI: build + unit + E2E).

## Docs are part of "done"

Every feature/fix updates docs **in the same PR**. **Documentation scope split:**

- **Repo docs** (`README.md`, `CLAUDE.md`, `docs/`) — contributors with a source checkout.
- **Website** (`website/src/content/docs/`) — users of a *released* artifact (containers, Helm, REST API, nuget.org plugins). Quick Start uses a released artifact, never `git clone && dotnet run`.

**Release PRs sweep version-pinned guides**: `grep` `website/src/content/docs/guides` for the previous `vX.Y.Z` and replace — nothing in CI catches a stale tag. File list: [`docs/runbooks/release.md`](docs/runbooks/release.md). Tabs/`syncKey`/`.mdx` rules → [`docs/conventions/documentation.md`](docs/conventions/documentation.md).

## Architecture

- **Clean Architecture / DDD.** Domain has no infrastructure concerns.
- **Grains change state only through domain methods** that apply a whole transition atomically (fields that change together live in one method).
- **`WorkflowInstance` is event-sourced (`JournaledGrain`)**: mutate via `DrainAndRaiseEvents()` (drain → `RaiseEvent` → `ConfirmEvents()`), never `WriteStateAsync()`. Other grains use `IPersistentState`. Partial-file layout: header comment of `Fleans.Application/Grains/WorkflowInstance.cs`.
- **Every state mutation is logged** with a `[LoggerMessage]` call.
- **Workflow metrics/traces** (`Fleans` Meter/ActivitySource, `FleansDiagnostics.cs`) are emitted from `WorkflowInstance.LogEvent` — fires once per real transition — and stay out of `Apply` handlers, which re-run on replay. Catalog: `website/src/content/docs/reference/observability.md`.

## Code conventions

- Records for DTOs; `[GenerateSerializer]` on anything crossing grain boundaries. **Collections in serialized records are `List<T>`** — `[x, y]` collection expressions yield `ReadOnlyArray<T>`, which has no Orleans copier and fails at runtime.
- Workflow variables: `ExpandoObject` + `Newtonsoft.Json`.
- **Logging: `[LoggerMessage]` source generators only** (`private partial void` on partial classes). EventId ranges: [`docs/conventions/observability-eventids.md`](docs/conventions/observability-eventids.md).
- Tests: MSTest + Orleans.TestingHost, AAA. **Activity tests verify state after both completion AND failure** (pattern: `ScriptTaskTests.cs`), querying `workflowInstance.GetState()` *after* the outcome rather than holding a pre-completion reference.
- **Any `.razor` / `Fleans.Web` edit** → [`docs/conventions/web-ui.md`](docs/conventions/web-ui.md) (grains-not-HTTP, Fluent UI component rules, `@Assets[...]` cache-busting).

## Load-bearing invariants

One line each; **before touching the area, read the full rule in [`docs/conventions/invariants.md`](docs/conventions/invariants.md)**.

- **At-most-once activity execution** — each activity instance completes OR fails exactly once; loops create new instances.
- **Compensation handlers run in isolated child scopes** and merge variables back into the enclosing scope before the next handler spawns.
- **Registration failures → `FailActivity`; cleanup failures → log-only** (effect handlers in `Fleans.Application/Effects/`).
- **User-task claim = three-rule OR** (assignee / candidate user / candidate group). Extend by adding a branch; rejections stay identifier-free.
- **Stream-redelivery idempotency** — every state-mutating method reachable from a stream handler or `IWorkflowInstanceCallback` must survive at-least-once delivery; new methods extend the guard set.

## How-to guides & deep dives

| Task / area | File |
|---|---|
| Add a BPMN activity | [`docs/conventions/adding-a-bpmn-activity.md`](docs/conventions/adding-a-bpmn-activity.md) |
| Add a custom-task plugin | [`docs/conventions/adding-a-custom-task-plugin.md`](docs/conventions/adding-a-custom-task-plugin.md) |
| Add a REST endpoint | [`docs/conventions/adding-an-api-endpoint.md`](docs/conventions/adding-an-api-endpoint.md) |
| Persistence (SQLite default / Postgres, new providers) | [`docs/conventions/persistence.md`](docs/conventions/persistence.md) |
| Streaming (Redis default, providers, tuning, subscriber traps) | [`docs/conventions/streaming.md`](docs/conventions/streaming.md) |
| Reminders (Redis, fail-fast) | [`docs/conventions/reminders.md`](docs/conventions/reminders.md) |
| Plugin NuGet stack (leaf packages, reflection trap) | [`docs/conventions/plugin-stack.md`](docs/conventions/plugin-stack.md) |
| Core / Worker / Plugin placement, `WorkerHost` | [`docs/conventions/placement-and-roles.md`](docs/conventions/placement-and-roles.md) |
| BPMN editor UI invariants | [`docs/conventions/bpmn-editor.md`](docs/conventions/bpmn-editor.md) |
| BPMN extension namespaces (`fleans:` / `zeebe:`) | [`docs/conventions/bpmn-extension-namespaces.md`](docs/conventions/bpmn-extension-namespaces.md) |
| Container builds, Aspire topology | [`docs/conventions/container-builds.md`](docs/conventions/container-builds.md) |
| E2E + manual regression, Aspire/Playwright traps | [`docs/conventions/regression-testing.md`](docs/conventions/regression-testing.md) |
| Release / compose bundle | [`docs/runbooks/release.md`](docs/runbooks/release.md), [`docs/runbooks/compose-bundle.md`](docs/runbooks/compose-bundle.md) |

## Manual test plans

Feature behaviour not covered by automated tests gets a plan in `tests/manual/NN-feature-name/` (`.bpmn` fixtures + `test-plan.md`), listed in [`tests/manual/README.md`](tests/manual/README.md). Template: `docs/plans/2026-02-25-manual-test-plan-design.md`.

## Regression tests

Worker-pipeline entry point (the `manual-regression-testing` skill greps this heading). Full procedure: [`docs/conventions/regression-testing.md`](docs/conventions/regression-testing.md). The automated catalog has already passed when a PR reaches "Testing"; per PR check:

1. **CI still green**: `gh pr checks <pr> --repo $REPO` — flag any required check now FAILURE.
2. **Still MERGEABLE**: `gh pr view <pr> --repo $REPO --json mergeable` — CONFLICTING → back to Ready.
3. **Diff scope**: `gh pr diff <pr> --repo $REPO --name-only`. Docs-only (`docs/**`, `website/**`, root `*.md`, `tests/manual/**/test-plan.md`) → PASSED.
4. **Code spot-check**: if `src/Fleans/Fleans.{Domain,Application,Infrastructure,Persistence,Api,Web}/**` changed, scan `tests/manual/README.md` for `STATUS: NEEDS-RERUN`; none → PASSED. Full UI/website sweeps stay with humans in `Review by human`.
