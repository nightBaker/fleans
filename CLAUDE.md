# Fleans

BPMN workflow engine built on Orleans. Architecture overview in `README.md`; design docs in `docs/plans/`.

## How to keep this file useful

This file is loaded into every Claude conversation, so its length is a tax on every turn.

- **Add a rule** when you've burned >1h debugging something a rule would have prevented. State the rule, then give a short *Why* and *How to apply* so it survives edge cases.
- **Prune as you add.** This file grows monotonically by default. When you add a section, ask whether something else can be shortened, deleted, or moved. Long-form patterns belong in `docs/conventions/`; operational procedures belong in `docs/runbooks/`.
- **Don't restate what code or `grep` already tells you** — file paths, route tables, BPMN element lists, EventId ranges. Those live in `README.md`, the code itself, or design docs.

## Build & Test

From `src/Fleans/`:

```bash
dotnet build
dotnet test                                                                       # unit + integration
dotnet test Fleans.E2E.Tests/Fleans.E2E.Tests.csproj --filter "TestCategory=E2E"   # Playwright E2E (Docker required)
dotnet run --project Fleans.Aspire                                                # full stack (Api + Web + Redis)
```

**Don't load test logs into context** until you're at the verification step — they're huge.

## Git Workflow

- **Never commit directly to `main`.** Always create a feature branch.
- Branch naming: `feature/<short-description>` or `fix/<short-description>`.
- Open a PR → CI runs build + unit tests + E2E.

## Documentation is part of "done"

Every feature/fix MUST update docs **in the same PR**.

**Documentation scope split** — the two doc surfaces have non-overlapping audiences and MUST stay separated:

- **Repo docs** (`README.md`, `CLAUDE.md`, `docs/`) — for contributors with a source checkout.
- **Website** (`website/src/content/docs/`) — for users consuming a *released* artifact (containers, Helm, REST API, plugins from nuget.org). Quick Start MUST use a released artifact, not `git clone && dotnet run`.

Full conventions (Tabs `syncKey` rules, `.mdx` imports, the CI guard that enforces this) → [`docs/conventions/documentation.md`](docs/conventions/documentation.md).

## Architecture principles

- **SOLID / DDD / Clean Architecture.** Domain layer is free of infrastructure concerns; depend on abstractions, not concretions.
- **Domain state methods are self-contained.** If a flag and a timestamp (or any related fields) always change together, they belong in the same domain state method. Grains MUST NOT set state properties directly — call a domain method that encapsulates the full state transition atomically. This prevents inconsistent state from partial updates.
- **`WorkflowInstance` is event-sourced via `JournaledGrain`.** Mutations flow through `DrainAndRaiseEvents()` (drain → `RaiseEvent` per event → `ConfirmEvents()`). Never call `WriteStateAsync()` in `WorkflowInstance`. Other grains use `IPersistentState` normally.
- **Log every state mutation.** Every grain method that mutates state (adds/removes activities, changes condition results, completes/fails instances) has a `[LoggerMessage]` log call. No silent mutations.

## Code conventions

- Records for immutable DTOs; `[GenerateSerializer]` on anything crossing grain boundaries.
  - **Collections inside serialized records must be `List<T>`** (or another Orleans-copyable collection). `[x, y, z]` collection expressions produce `ReadOnlyArray<T>` which has no Orleans copier and fails at runtime.
- Dynamic workflow variable state: `ExpandoObject` + `Newtonsoft.Json`.
- **Logging: `[LoggerMessage]` source generators only** — `private partial void` on partial classes. No `ILogger.Log*()` extension calls. EventId ranges in `docs/plans/2026-02-08-structured-workflow-logging.md`.
- Tests: MSTest + Orleans.TestingHost, AAA. **Activity tests must verify state after both completion AND failure** (pattern: `ScriptTaskTests.cs`, `TaskActivityTests.cs`). Query state via `workflowInstance.GetState()` *after* completion/failure — never hold a pre-completion grain reference to assert on.
- **Admin UI (`Fleans.Web`) calls Orleans grains directly via the `WorkflowEngine` service** — not through HTTP. Don't add API endpoints to back admin-UI features.
- `WorkflowInstance` partial-class layout — see the header comment at the top of `Fleans.Application/Grains/WorkflowInstance.cs` for what goes in which of the three partial files.
- **Fluent UI Blazor** (`Fleans.Web`): only use components that exist in <https://www.fluentui-blazor.net/>. `FluentButton` icons go via `IconStart`/`IconEnd` (never `<FluentIcon>` as child content); use `Loading` for spinner states.
- **Cache-bust local Razor host assets**: every `<script>`/`<link>` in `App.razor` (or any other Razor host file) that references an asset under `wwwroot/` MUST use `@Assets["..."]` so the URL carries a content hash. Externally-versioned CDN URLs (e.g. `https://unpkg.com/bpmn-js@17.11.1/...`) are exempt — the path already encodes the version.

## Design constraints — load-bearing invariants

These guard correctness. Break them and silent bugs ship.

- **Each activity instance executes at most once.** Every non-boundary activity instance runs exactly once (completes OR fails). An activity definition can be visited multiple times (e.g., in a loop), creating a new instance each time. `TimerCallbackGrain` keys on `hostActivityInstanceId` to distinguish.

- **Compensation handlers run in isolated child scopes.** Each handler gets a fresh variable scope seeded with the compensable activity's completion-time snapshot, overlaying the enclosing scope. After a handler completes successfully, its variable changes MUST be merged back into the enclosing scope before the next handler spawns. Otherwise: (a) later handlers see stale variables, and (b) compensation side-effects vanish after the walk finishes. Enforced via `WorkflowExecution.AdvanceCompensationWalkIfHandlerCompleted` emitting a `VariablesMerged` event targeting the parent scope's variables ID.

- **Registration-vs-cleanup error asymmetry.** Registration-path failures (build-time `ProcessRegisterMessage`, handler-time `RegisterTimerEffect.Activate`, `SubscribeMessageEffect` / `SubscribeSignalEffect`) MUST route to `FailActivity` so the workflow surfaces the failure. Cleanup-path failures (`UnsubscribeMessageEffect`, `UnsubscribeSignalEffect`, `UnregisterTimerEffect`, `ThrowSignalEffect`) MUST be log-only — the owning activity has already completed or been cancelled, and failing now would violate the "at most once" invariant. Effect handlers in `Fleans.Application/Effects/{Message,Signal,Timer}EffectHandler.cs` enforce this.

- **User-task claim authorization is a three-rule OR.** `UserTaskLifecycle.Claim` allows a claim iff at least one of: (a) `Assignee == userId`, (b) `CandidateUsers.Contains(userId)`, (c) `CandidateGroups ∩ userGroups ≠ ∅` (ordinal case-sensitive). When no constraint is present the task is unrestricted. Caller-side `userGroups` comes from `IUserGroupResolver` — `JwtUserGroupResolver` reads `HttpContext.User.FindAll("groups")` when `Authentication:Authority` is configured; `BodyUserGroupResolver` reads the body when auth is disabled (body-supplied groups are ignored under JWT so callers can't spoof). Rejection messages MUST stay identifier-free; structured audit lives in `LogUserTaskClaimRejected` (EventId 1066, Warning) carrying `UserGroupCount` only — never group names. **When extending (e.g., role-based claims): add a 4th branch, never replace the existing three.**

- **Stream-redelivery idempotency.** Every state-mutating grain method reachable from a `[ImplicitStreamSubscription]` handler MUST be idempotent under at-least-once delivery. Current guarantees are a layered guard set: (1) grain-entry stale guard `HasActiveEntry(activityInstanceId)` on `CompleteActivity`/`FailActivity`; (2) domain-entry stale guard `entry.IsCompleted` as defense-in-depth; (3) `joinState.HasFired` on `CompleteActivationCondition`; (4) `entry.IsCompleted || entry.ActivityInstanceId != hostActivityInstanceId` on `CompleteMultiInstanceEarly`; (5) `CompleteConditionSequence` relies on set-by-key projection idempotency; (6) `WorkflowExecution.IsTimerFireStale(timerActivityId, hostActivityInstanceId)` pre-call check on `HandleTimerFired` — mirrors the aggregate's internal regular-timer and ESP-scope-container guards; late reminder fires (post-completion, post-cancel) are logged as warnings (EventId 3034) and silently dropped. Pairs with `TimerCallbackGrain.Cancel`'s swallow-on-failure cleanup per the registration-vs-cleanup asymmetry rule. **When adding a new state-mutating method on `IWorkflowInstanceCallback` (or any other interface called from a stream handler), extend this guard set or prove the new path is naturally idempotent.** Silent doubling under redelivery is a production correctness bug, not a test flake.

## How-to guides

- BPMN activity → [`docs/conventions/adding-a-bpmn-activity.md`](docs/conventions/adding-a-bpmn-activity.md)
- Custom-task plugin → [`docs/conventions/adding-a-custom-task-plugin.md`](docs/conventions/adding-a-custom-task-plugin.md)
- REST API endpoint → [`docs/conventions/adding-an-api-endpoint.md`](docs/conventions/adding-an-api-endpoint.md)
- New persistence provider → [`docs/conventions/persistence.md`](docs/conventions/persistence.md)
- New stream provider → [`docs/conventions/streaming.md`](docs/conventions/streaming.md)

## Deep dives — load when relevant

| Area | File |
|---|---|
| Plugin-author NuGet stack (leaf-package design, reflection trap, namespace-preserving moves) | [`docs/conventions/plugin-stack.md`](docs/conventions/plugin-stack.md) |
| Three-role placement contract (Core / Worker / Plugin), `WorkerHost`, custom-worker template | [`docs/conventions/placement-and-roles.md`](docs/conventions/placement-and-roles.md) |
| Orleans streaming (providers, parallelism knobs, stream-id sharding, subscriber traps, custom-task per-type namespace) | [`docs/conventions/streaming.md`](docs/conventions/streaming.md) |
| BPMN editor UI invariants (multi-tab, plugin-switch dialog gate, custom-task properties panel) | [`docs/conventions/bpmn-editor.md`](docs/conventions/bpmn-editor.md) |
| BPMN extension namespace policy (`fleans:` / legacy / `zeebe:`) | [`docs/conventions/bpmn-extension-namespaces.md`](docs/conventions/bpmn-extension-namespaces.md) |
| Container builds (`SdkContainerSupport`, Aspire dev vs. publish topology) | [`docs/conventions/container-builds.md`](docs/conventions/container-builds.md) |
| Persistence providers (SQLite/Postgres, advisory lock, test parity, safety valves) | [`docs/conventions/persistence.md`](docs/conventions/persistence.md) |
| Regression testing (automated E2E + manual catalog, known Aspire/Playwright traps) | [`docs/conventions/regression-testing.md`](docs/conventions/regression-testing.md) |
| Documentation conventions (scope split, Tabs syncKey rules) | [`docs/conventions/documentation.md`](docs/conventions/documentation.md) |
| Release runbook (pre-tag checklist, tag, post-tag verify, rollback) | [`docs/runbooks/release.md`](docs/runbooks/release.md) |
| Compose-bundle post-processing | [`docs/runbooks/compose-bundle.md`](docs/runbooks/compose-bundle.md) |

## Manual test plans

Every new feature MUST have a manual test plan under `tests/manual/NN-feature-name/` containing the `.bpmn` fixture(s) and a `test-plan.md`. The canonical catalog is [`tests/manual/README.md`](tests/manual/README.md) — append new entries there. Template: `docs/plans/2026-02-25-manual-test-plan-design.md`.

## Regression tests

Authoritative procedure: [`docs/conventions/regression-testing.md`](docs/conventions/regression-testing.md). The steps below are the worker-pipeline entry point — the `manual-regression-testing` skill greps this heading literally, so don't rename it.

The worker only promotes a PR to "Testing" once the `e2e` job is `SUCCESS` and the bot-approve marker is fresh, so the automated catalog has already passed by the time this skill runs. The manual residual to check per PR:

1. **CI is still green.** `gh pr checks <pr> --repo $REPO` — flag if any required check has flipped to FAILURE since promotion (rollup state can lag).
2. **PR is still MERGEABLE.** `gh pr view <pr> --repo $REPO --json mergeable` — if CONFLICTING, move back to Ready per the skill's normal flow.
3. **Diff scope.** `gh pr diff <pr> --repo $REPO --name-only`. Docs-only diffs (`docs/**`, `website/**`, root `*.md`, `tests/manual/**/test-plan.md`) have no further manual work — mark PASSED.
4. **Code PR spot-check.** If the diff touches `src/Fleans/Fleans.{Domain,Application,Infrastructure,Persistence,Api,Web}/**`, scan `tests/manual/README.md` for any plan flagged `STATUS: NEEDS-RERUN`. If none, mark PASSED. Full UI/website sweeps remain a human task in `Review by human`.

## Things to know

- **Aspire is the startup project**, not `Api` or `Web`.
- **BPMN coverage is partial** — see the table in `README.md` for what's implemented.
- **Design docs live in `docs/plans/`** — check before architectural changes.
- **Streaming defaults to Redis** (already provisioned for Orleans clustering); switch via `FLEANS_STREAMING_PROVIDER`. Operator notes & tuning in [`docs/conventions/streaming.md`](docs/conventions/streaming.md).
- **Persistence defaults to SQLite** (file-based, shared via `FLEANS_SQLITE_CONNECTION` set by Aspire). Switch to Postgres via `FLEANS_PERSISTENCE_PROVIDER=Postgres`. Details in [`docs/conventions/persistence.md`](docs/conventions/persistence.md).
- **Reminders default to Redis** (via `Microsoft.Orleans.Reminders.Redis`, shares the `orleans-redis` connection used for clustering + streaming). Silos fail-fast on missing connection. Details in [`docs/conventions/reminders.md`](docs/conventions/reminders.md).
- **Workflow-level metrics + tracing** are emitted on the `Fleans` Meter / ActivitySource (defined in [`Fleans.Application/Observability/FleansDiagnostics.cs`](src/Fleans/Fleans.Application/Observability/FleansDiagnostics.cs), registered by name in `Fleans.ServiceDefaults/Extensions.cs`). Emit new workflow metrics from `WorkflowInstance.LogEvent` (called via `DrainAndRaiseEvents` — fires once per real state transition, never on JournaledGrain replay); never from `Apply` handlers. Catalog + caveats in [`website/src/content/docs/reference/observability.md`](website/src/content/docs/reference/observability.md).
