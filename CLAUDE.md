# Fleans

BPMN workflow engine built on Orleans.

## Build & Test

From `src/Fleans/`:

```bash
dotnet build
dotnet test
```

Run the full stack (Api + Web + Redis) via Aspire:

```bash
dotnet run --project Fleans.Aspire
```

## Git Workflow

- **Never commit directly to `main`.** Always create a feature branch for any new feature, bug fix, or change.
- Branch naming: `feature/<short-description>` or `fix/<short-description>`
- Open a PR from the feature branch to `main`, merge back. CI runs build + test on PRs.

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
- **Logging: always use `[LoggerMessage]` source generators** instead of `ILogger.Log*()` extension methods. Define log methods as `private partial void` on `partial` classes. See `WorkflowInstance.cs` for the pattern. EventId ranges are documented in `docs/plans/2026-02-08-structured-workflow-logging.md`.
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

### Known Bugs (see `docs/plans/2026-02-25-manual-test-results.md`)
- Boundary events on IntermediateCatchEvents don't register subscriptions (affects timer/message/signal boundaries)
- Child process errors don't propagate to parent error boundary on CallActivity

## Things to Know

- **Aspire is the startup project**, not Api or Web
- **Grain state is in-memory** — no persistence yet, this is a known gap, not a design choice. Don't design against eventual persistence.
- **BPMN coverage is partial** — see the table in `README.md` for what's implemented
- **Design docs** live in `docs/plans/` — check them before making architectural changes

