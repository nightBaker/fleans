# Fleans

BPMN workflow engine built on Orleans.

## Build & Test

From `src/Fleans/`:

```bash
dotnet build
dotnet test
```

**Context management:** Do not read test files into context until you are at the test verification step (i.e., writing or modifying tests, or verifying that tests pass). Test output logs can be large — avoid loading them prematurely.

Run the full stack (Api + Web + Redis) via Aspire:

```bash
dotnet run --project Fleans.Aspire
```

## Git Workflow

- **Never commit directly to `main`.** Always create a feature branch for any new feature, bug fix, or change.
- Branch naming: `feature/<short-description>` or `fix/<short-description>`
- Open a PR from the feature branch to `main`, merge back. CI runs build + test on PRs.

## Documentation Website

The public docs site lives in `website/` — an Astro + Starlight project deployed to GitHub Pages via `.github/workflows/deploy-website.yml` (Node 22, deploy job gated to `main`).

- Content: `website/src/content/docs/` (`guides/`, `concepts/`, `reference/`, `index.mdx`)
- Theme: `website/src/styles/custom.css` — palettes scoped to `:root[data-theme='light']` and `:root[data-theme='dark']`
- Local dev: `cd website && npm install && npm run dev`
- Build check: `npm run build` (must pass before merging)

**Documentation rule:** Any new feature, BPMN element, API endpoint, or user-facing behavior MUST be reflected in the docs site in the same PR. Update the relevant page under `website/src/content/docs/` (e.g. new BPMN activity → `concepts/bpmn-support.md`; new endpoint → `reference/api.md`; new workflow → add a guide). If no suitable page exists, create one and add it to the Starlight sidebar in `website/astro.config.mjs`. Documentation is part of "done", not a follow-up task.

## How to Add a New BPMN Activity

1. Create the activity class in `Fleans.Domain/Activities/` — extend the existing `Activity` base class
2. Register it in `Fleans.Infrastructure/Bpmn/BpmnConverter.cs` so BPMN XML maps to it
3. Add tests in `Fleans.Domain.Tests/` using Orleans `TestCluster`. **Every activity must have tests that verify workflow state after both completion and failure** — see `ScriptTaskTests.cs` and `TaskActivityTests.cs` for the pattern:
   - **Completion tests:** completed activities list, variable merging (count + values), activity instance marked completed, no active activities remain, sequential chaining
   - **Fail tests:** error state set with correct code/message (500 for generic Exception, 400 for BadRequestActivityException), failed activity still transitions to next activity, no variables merged on failure
4. Update the BPMN elements table in `README.md`

## How to Add a New API Endpoint

Add it to `Fleans.Api/Controllers/WorkflowController.cs`. DTOs go in `Fleans.ServiceDefaults/`.

## Architecture Principles

- **Always respect SOLID, DDD, and Clean Architecture principles.** Keep domain logic in `Fleans.Domain` free of infrastructure concerns. Depend on abstractions, not concretions. Ensure single responsibility across classes and methods. Model the domain with aggregates, value objects, and domain events where appropriate.
- **Domain state methods must be self-contained.** If a flag and a timestamp (or any related fields) always change together, they belong in the same domain state method. Grains must not set state properties directly — call a domain method that encapsulates the full state transition atomically. This prevents inconsistent state from partial updates.

## Code Conventions

- Follow existing patterns — records for immutable DTOs, `[GenerateSerializer]` on anything crossing grain boundaries
- ExpandoObject + Newtonsoft.Json for dynamic workflow variable state
- Tests use MSTest + Orleans.TestingHost, AAA pattern. Activity tests must verify both post-completion and post-failure state. Query state via `workflowInstance.GetState()` after completion/failure — never hold grain references from before completion to assert on.
- **Admin UI (Fleans.Web) communicates with Orleans grains directly via `WorkflowEngine` service** — not through HTTP API endpoints. The Web app runs as Blazor Server (InteractiveServer), so Razor components execute server-side and can call grains directly. Do not add API endpoints for admin UI functionality.
- **WorkflowInstance partial class layout** — `WorkflowInstance` is a thin coordinator grain split into 3 partial files. Place new methods in the correct file:
  - `WorkflowInstance.cs` — fields, constructor, lifecycle overrides (`OnActivateAsync`, `OnDeactivateAsync`, `TransitionState`), `ICustomStorageInterface` implementation (`ReadStateFromStorage`, `ApplyUpdatesToStorage`), all public entry points grouped by: workflow lifecycle (`SetWorkflow`, `StartWorkflow`), activity completion/failure (`CompleteActivity`, `FailActivity`, `CompleteConditionSequence`), child workflow coordination (`SetParentInfo`, `SetInitialVariables`, `OnChildWorkflowCompleted`, `OnChildWorkflowFailed`), external event handling (`HandleTimerFired`, `HandleMessageDelivery`, `HandleBoundaryMessageFired`, `HandleSignalDelivery`, `HandleBoundarySignalFired`), user task operations (`ClaimUserTask`, `UnclaimUserTask`, `CompleteUserTask`), state queries (`GetState`, `GetVariable`, `GetVariables`, `GetActiveActivities`, `GetCompletedActivities`, `GetConditionSequenceStates`, `SetConditionSequenceResult`, `FindForkByToken`), request context/scope utilities
  - `WorkflowInstance.Infrastructure.cs` — private infrastructure methods (`RunExecutionLoop`, `ResolveExternalCompletions`, `ComputeTransitionsForEntries`, `HandleScopeCompletions`, `PerformEffects`, `PerformMessageSubscribe`, `PerformSignalSubscribe`, `PerformStartChildWorkflow`, `PublishDomainEvent`, `ProcessPendingEvents`, `ProcessPendingEventsTimer`, `DisposePendingEventsTimerIfTerminal`, `EnsurePendingEventsTimerRegistered`, `DrainAndRaiseEvents`, `LogEvent`)
  - `WorkflowInstance.Logging.cs` — all `[LoggerMessage]` partial method declarations
- **Logging: always use `[LoggerMessage]` source generators** instead of `ILogger.Log*()` extension methods. Define log methods as `private partial void` on `partial` classes. New `[LoggerMessage]` declarations go in `WorkflowInstance.Logging.cs`. EventId ranges are documented in `docs/plans/2026-02-08-structured-workflow-logging.md`.
- **WorkflowInstance state changes** flow through `DrainAndRaiseEvents()`, which drains uncommitted events from the aggregate, calls `RaiseEvent(event)` for each, then `ConfirmEvents()` to persist. Never use `WriteStateAsync()` in WorkflowInstance. Other grains still use `WriteStateAsync()` with their own IPersistentState storage.
- **Log all workflow instance state changes.** Every grain method that mutates state (adds/removes activities, changes condition results, completes/fails instances) must have a `[LoggerMessage]` log call. No silent state mutations.
- **Fluent UI Blazor (Fleans.Web)**: Only use components that exist in the library (https://www.fluentui-blazor.net/). Use `IconStart`/`IconEnd` parameters on `FluentButton` — never place `<FluentIcon>` as child content. Use the `Loading` parameter for buttons with loading states.

## Design Constraints

- **Each activity instance executes at most once** — every non-boundary activity instance runs exactly once (completes or fails). An activity definition can be visited multiple times (e.g., in a loop), creating a new instance each time. `TimerCallbackGrain` keying uses `hostActivityInstanceId` to distinguish instances of the same activity.

## Manual Test Plans

- **Every new feature must have a manual test plan.** After writing the design doc and implementation plan, create a manual test folder in `tests/manual/` with:
  1. `.bpmn` fixture file(s) that exercise the feature
  2. `test-plan.md` with scenario description, prerequisites, numbered steps (deploy, start, trigger events, verify), and a checklist of expected outcomes
- Follow the folder naming convention: `NN-feature-name/` (numbered for ordering)
- Test plans are verified via Chrome (Web UI) + API calls for messages/signals
- See `docs/plans/2026-02-25-manual-test-plan-design.md` for the template and full design

### BPMN Fixture Authoring Rules
- **Always use `<scriptTask>` with `scriptFormat="csharp"`** — never bare `<task>`. The engine does not recognize `<task>` elements.
- **Message events require the Zeebe namespace** — add `xmlns:zeebe="http://camunda.org/schema/zeebe/1.0"` to `<definitions>` and include `<zeebe:subscription correlationKey="= varName" />` inside the `<message>` element.
- **Every fixture must include a `<bpmndi:BPMNDiagram>` section** — the BPMN editor requires diagram info to render. Without it, the import silently produces a blank canvas.
- **Use short timer durations** for test fixtures (PT5S–PT10S) so tests complete quickly.

### API Endpoints for Manual Tests
- Start instance: `POST https://localhost:7140/Workflow/start` — body: `{"WorkflowId":"process-id"}`
- Send message: `POST https://localhost:7140/Workflow/message` — body: `{"MessageName":"...", "CorrelationKey":"...", "Variables":{}}`
- Send signal: `POST https://localhost:7140/Workflow/signal` — body: `{"SignalName":"..."}`
- Complete activity: `POST https://localhost:7140/Workflow/complete-activity` — body: `{"WorkflowInstanceId":"guid", "ActivityId":"activity-id", "Variables":{}}`

## Persistence Providers

Two providers: **SQLite** (default, local dev) and **PostgreSQL** (production/load testing). Selected via configuration — no code changes needed.

- **Config key:** `Persistence:Provider` (values: `Sqlite` | `Postgres`, case-insensitive, default `Sqlite`)
- **Aspire:** set `FLEANS_PERSISTENCE_PROVIDER=Postgres` env var before launch to auto-provision a Postgres container
- **Connection strings:** SQLite uses `FLEANS_SQLITE_CONNECTION` / `FLEANS_QUERY_CONNECTION` env vars. PostgreSQL uses `ConnectionStrings:fleans` (required) and `ConnectionStrings:fleans-query` (optional read replica).
- **Migration strategy:** SQLite uses `EnsureCreated()`. PostgreSQL uses `MigrateAsync()` (migrations applied automatically by `Fleans.Api` on startup).
- **Migrations live per-provider:** `Fleans.Persistence.Sqlite/Migrations/Command/` and `Fleans.Persistence.PostgreSql/Migrations/Command/`. Only command-context migrations are maintained (command and query share the same database).
- **Provider packages:** `Fleans.Persistence.Sqlite` and `Fleans.Persistence.PostgreSql` — each registers a `RelationalModelCustomizer` subclass via `ReplaceService<IModelCustomizer>` for provider-specific model tweaks (e.g., SQLite stores `DateTimeOffset` as string; PostgreSQL uses native `timestamptz`).
- **Adding a new provider:** Create a new `Fleans.Persistence.<Provider>` project, implement a `<Provider>ModelCustomizer : RelationalModelCustomizer`, add an `Add<Provider>Persistence()` extension, generate initial migrations, and wire into host `Program.cs` files.

## Things to Know

- **Aspire is the startup project**, not Api or Web
- **WorkflowInstance uses JournaledGrain with event sourcing** — events are persisted via EfCoreEventStore, read-side state is projected via EfCoreWorkflowStateProjection (CQRS pattern). Other grains (ProcessDefinition, correlations, timers, start event listeners, user tasks) use IPersistentState with EF Core IGrainStorage.
- **BPMN coverage is partial** — see the table in `README.md` for what's implemented
- **Design docs** live in `docs/plans/` — check them before making architectural changes

