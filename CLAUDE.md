# Fleans

BPMN workflow engine built on Orleans.

## Main Rule

**After completing each feature or fix, update this CLAUDE.md** with any lessons learned, patterns discovered, or pitfalls encountered during the work. The goal is to capture hard-won knowledge so the same problems are never solved twice. Add entries to the relevant section (conventions, constraints, lessons learned, etc.) or create a new section if needed.

**After completing each feature or fix, update the website documentation** (`website/src/content/docs/`) with: (1) the reason/motivation behind the change, (2) how to use it, and (3) a best-practice example showing real-world usage. Documentation should help users understand not just *what* exists but *why* it exists and *how* to use it correctly.

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

## Container Builds

Container images are produced by the .NET SDK's built-in `SdkContainerSupport` — no Dockerfiles. Each deployable service has a `<ContainerRepository>fleans-<svc></ContainerRepository>` property in its csproj, and `src/Fleans/Directory.Build.props` is the single source of truth for `<VersionPrefix>` and `<ContainerImageTag>`.

**Local build (one image):**

```bash
cd src/Fleans
dotnet publish Fleans.Api/Fleans.Api.csproj /t:PublishContainer /p:Version=0.1.0-test
# → produces local Docker image  fleans-api:0.1.0-test
```

The same command works against `Fleans.Web/Fleans.Web.csproj`, `Fleans.WorkerHost/Fleans.WorkerHost.csproj`, and `Fleans.Mcp/Fleans.Mcp.csproj` — they each emit `fleans-{web,worker,mcp}:0.1.0-test`.

`/p:Version=...` overrides `<VersionPrefix>` from `Directory.Build.props`, so CI invocations that pass `/p:Version=$(git tag without v)` stamp the image and the assembly version with the same string.

**Aspire publish (full topology):**

```bash
cd src/Fleans
aspire publish --project Fleans.Aspire -t docker-compose -o out/compose
aspire publish --project Fleans.Aspire -t kubernetes -o out/k8s   # Aspire.Hosting.Kubernetes
```

`Fleans.Aspire/Program.cs` registers `AddKubernetesEnvironment("k8s")` and conditionally registers `Fleans.WorkerHost` in publish mode (it stays out of the dev `dotnet run` topology — Combined-role `Fleans.Api` already hosts worker grains there). The publish output therefore covers all four services (api/web/worker/mcp) plus their dependencies (Redis, optional PostgreSQL, optional Kafka).

`Aspire.Hosting.Kubernetes` ships only as preview NuGet builds; the version is pinned to `13.2.3-preview.1.26217.6` to track the rest of the Aspire 13.2.3 stack — bump it together with the other `Aspire.Hosting.*` packages on Aspire upgrades.

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

**Load-test results publishing rule:** `website/src/content/docs/reference/load-testing.md` publishes load-test results **only for the current public release version** of Fleans. Each result section MUST be headed with `### Fleans vX.Y.Z — YYYY-MM-DD — <stack description>` so readers can scope numbers to a release + run date. When a new release ships, REMOVE prior-version sections from the website page in the same release PR — do not let stale numbers accumulate publicly. The full historical reports stay in the repo at `tests/load/results/<run-id>/report.md` and are recoverable via the matching git tag; only the website page is curated.

### Hero BPMN Diagram

The landing page includes a rendered BPMN workflow diagram between the hero and the "Why Fleans?" cards. Two themed SVG variants (light/dark) are pre-rendered from `tests/manual/04-parallel-gateway/fork-join.bpmn` using bpmn-js in a headless Playwright browser.

- **Prerequisites:** `npx playwright install chromium` (one-time setup)
- **Trigger:** re-run when the source fixture changes or `bpmn-js` version is bumped
- **Command:** `cd website && npm run render-bpmn`
- **Output:** `website/public/hero-workflow-light.svg` and `website/public/hero-workflow-dark.svg`
- **Rule:** visually inspect both SVGs in a browser before committing, AND open each file directly (not embedded in a page) to confirm the browser's XML viewer accepts it without a parse-error banner. The output must begin with `<?xml …?>` + `<!-- created with bpmn-js -->` + `<!DOCTYPE svg …>`.
- **Structural cleanup happens in the DOM, not via regex.** `render-bpmn.mjs` calls `viewer.saveSVG()`, round-trips the result through `DOMParser` + `querySelectorAll('.djs-hit, .djs-outline, .djs-dragger').remove()`, then re-serializes via `XMLSerializer` with the prolog/DOCTYPE re-prepended. Do not re-introduce regex-based element stripping — `<[^>]+>` absorbs the `/` of self-closing `<rect class="djs-hit" .../>` tags, causing a non-greedy `[\s\S]*?</[^>]+>` trailer to consume unrelated `</g>` closers (that is the root cause of #366 — 21 missing `</g>` per file, SVG rejected by strict XML parsers).
- **Known limitation:** interior type-markers (script/user/service icons) are stripped from the SVG — only shapes (rectangles, diamonds, circles, arrows) are rendered. The admin UI (Fleans.Web) shows full markers because it loads the bpmn-font.

### 3D Landing Background

The splash page (`website/src/content/docs/index.mdx`) loads an interactive Three.js silo scene as its background via `src/components/SiloBackground.astro`. Key points:

- **Feature-gated:** loads the scene only on desktop (≥ 768 px), when `prefers-reduced-motion` is not set, and when WebGL2 is available. Otherwise renders `public/silo-poster-{dark,light}.webp`.
- **Theme-reactive:** a `MutationObserver` on `<html data-theme>` recolors the scene in place — no reload, no rebuild.
- **Only imported by `index.mdx`:** doc pages are untouched and pay zero bundle cost.
- **Regenerating posters:** if you change scene visuals, run `cd website && npm run posters` (requires `npx playwright install chromium`). Commit the updated `public/silo-poster-*.webp` files.
- **Contrast guardrail:** `cd website && npm run check:contrast` runs a Playwright check that fails if hero text drops below WCAG AA against the themed composite background. It is *not* wired into `npm run build` (that would force CI to install Chromium on every deploy). Run it manually after any change to the silo-background CSS, silo-scene, or hero styling.

## How to Add a New BPMN Activity

1. Create the activity class in `Fleans.Domain/Activities/` — extend the existing `Activity` base class
2. Register it in `Fleans.Infrastructure/Bpmn/BpmnConverter.cs` so BPMN XML maps to it
3. Add tests in `Fleans.Domain.Tests/` using Orleans `TestCluster`. **Every activity must have tests that verify workflow state after both completion and failure** — see `ScriptTaskTests.cs` and `TaskActivityTests.cs` for the pattern:
   - **Completion tests:** completed activities list, variable merging (count + values), activity instance marked completed, no active activities remain, sequential chaining
   - **Fail tests:** error state set with correct code/message (500 for generic Exception, 400 for BadRequestActivityException), failed activity still transitions to next activity, no variables merged on failure
4. Update the BPMN elements table in `README.md`

## How to Add a Custom Task Plugin

A *custom task* is a `<bpmn:serviceTask type="…">` whose execution is supplied by a user-written plugin grain on a Worker silo. Use this — **not** "Add a New BPMN Activity" — when the new behavior is plugin-shaped (REST call, email, custom external system).

1. Add a new project (e.g. `Fleans.Plugins.MyThing`) referencing `Fleans.Worker`. Inside it, write a class deriving from `Fleans.Worker.CustomTasks.CustomTaskHandlerBase`. Override `TaskType` and `ExecuteAsync(...)`. The base class carries `[ImplicitStreamSubscription("events")]` and `[WorkerPlacement]` — subclasses inherit both.
2. Throw `Fleans.Domain.Errors.CustomTaskFailedActivityException(int code, string message)` from `ExecuteAsync` to fail with a typed error; any other thrown exception fails with code 500.
3. The plugin's `ExecuteAsync` returns an `IDictionary<string, object?>`. Output mappings (`<zeebe:output source="=__response.body" target="…"/>`) walk that dictionary.
4. Expose a DI extension method on the plugin assembly:
   ```csharp
   public static IServiceCollection AddMyThingPlugin(this IServiceCollection services) =>
       services.AddCustomTaskPlugin<MyThingHandler>(taskType: "my-thing", displayName: "My Thing");
   ```
   Plugin authors who want their plugin to live in the catalog UI must call this from the Worker silo's host registration.
5. Tests: write unit tests for the plugin's logic (call `ExecuteAsync` directly with stub inputs); end-to-end TestCluster integration is exercised by manual test plan #37 once a real plugin ships.
6. Documentation: update `website/src/content/docs/concepts/custom-tasks.md` with the plugin's parameter schema and any limitations.

## How to Add a New API Endpoint

Add it to `Fleans.Api/Controllers/WorkflowController.cs`. DTOs go in `Fleans.ServiceDefaults/`.

## Architecture Principles

- **Always respect SOLID, DDD, and Clean Architecture principles.** Keep domain logic in `Fleans.Domain` free of infrastructure concerns. Depend on abstractions, not concretions. Ensure single responsibility across classes and methods. Model the domain with aggregates, value objects, and domain events where appropriate.
- **Domain state methods must be self-contained.** If a flag and a timestamp (or any related fields) always change together, they belong in the same domain state method. Grains must not set state properties directly — call a domain method that encapsulates the full state transition atomically. This prevents inconsistent state from partial updates.

## Code Conventions

- Follow existing patterns — records for immutable DTOs, `[GenerateSerializer]` on anything crossing grain boundaries. Collections inside serialized records must be `List<T>` (or another Orleans-copyable collection) — `ReadOnlyArray<T>` produced by C# collection expressions (`[x, y, z]` assigned to a non-mutable target) has no Orleans copier and fails at runtime.
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
- **Cache-busting for local Razor host assets**: every `<script>` or `<link>` in `App.razor` (or any other Razor host file) that references an asset under `wwwroot/` MUST go through `@Assets["..."]` so the URL carries a content hash. Externally-versioned CDN URLs (e.g. `https://unpkg.com/bpmn-js@17.11.1/...`) are exempt because the version segment in the URL already serves the same purpose. Without this, a published JS API addition can be invisible to browsers that cached the old file (root cause of #373).
- **Core / Worker role split (`Fleans:Role`)**: `Fleans.Api` reads `Fleans:Role` at startup (values: `Core`, `Worker`, `Combined` — case-insensitive, default `Combined`; invalid values throw). The role is stamped into `SiloOptions.SiloName` as `{role}-{machine}-{guid}` so other silos see it via Orleans membership. `Fleans.Worker` hosts the `[StatelessWorker]` script/condition grain **implementations** (`ScriptExecutorGrain`, `ConditionExpressionEvaluatorGrain`); their interfaces remain in `Fleans.Application` so callers don't need a Worker reference. **When adding a new worker-type grain** (e.g. the upcoming REST-call service task), put the implementation in `Fleans.Worker` and keep the interface next to the caller in `Fleans.Application`. Placement strategies that would route worker grains only to silos with the `worker-` / `combined-` prefix are a follow-up — today the split is structural (separate assembly, separate role config) without runtime placement filtering, so a `Core`-tagged silo will still host a worker grain if one is needed there.
- **`Fleans.WorkerHost`** is the dedicated deployable for the Worker role — a thin Web SDK Exe that boots an Orleans silo with `Fleans:Role=Worker` by default, references the `Fleans.Worker` class library for grain implementations + placement directors, and wires the same persistence/streaming/Redis stack as `Fleans.Api`. It is registered with Aspire **only in publish mode** (`builder.ExecutionContext.IsPublishMode`), so `dotnet run --project Fleans.Aspire` keeps the original 3-process dev topology and `aspire publish -t kubernetes` / `-t docker-compose` emits a fourth `fleans-worker` deployment alongside `fleans-core` (Api), `fleans-management` (Web), and `fleans-mcp` (Mcp). Container image name: `fleans-worker` via `<ContainerRepository>` in `Fleans.WorkerHost.csproj`.
- **BPMN Editor tabs (`/editor`)**: multi-tab state lives in `Editor.razor` (private `tabs: List<TabSession>` + `activeTabId`). Only one `bpmn-js` modeler exists at a time — switching tabs calls `bpmnEditor.getXml` on the outgoing tab and `bpmnEditor.loadXml(incoming.BpmnXml)` on the incoming. Dirty tracking subscribes to bpmn-js `commandStack.changed` via `bpmnEditor.registerDirtyCallback` and flips the active tab's flag (cleared on deploy). Persistence is localStorage-only under key `fleans.editor.tabs.v1` (versioned so future schema changes don't crash old sessions). The cap is 10 tabs; closing the last tab opens a fresh blank one so the editor is never empty.

## Design Constraints

- **Each activity instance executes at most once** — every non-boundary activity instance runs exactly once (completes or fails). An activity definition can be visited multiple times (e.g., in a loop), creating a new instance each time. `TimerCallbackGrain` keying uses `hostActivityInstanceId` to distinguish instances of the same activity.
- **Compensation handlers run in isolated child scopes** — each handler gets a fresh variable scope seeded with the compensable activity's completion-time snapshot, overlaying the enclosing scope. After a handler completes successfully, its variable changes MUST be merged back into the enclosing scope before the next handler spawns. Otherwise: (a) later handlers in the walk see stale variables, and (b) compensation side-effects vanish after the walk finishes. `WorkflowExecution.AdvanceCompensationWalkIfHandlerCompleted` emits a `VariablesMerged` event with the handler's full variable map targeting the parent scope's variables ID (root scope's if the walk is at root). Do not break this invariant when refactoring the compensation path.

## Manual Test Plans

- **Every new feature must have a manual test plan.** After writing the design doc and implementation plan, create a manual test folder in `tests/manual/` with:
  1. `.bpmn` fixture file(s) that exercise the feature
  2. `test-plan.md` with scenario description, prerequisites, numbered steps (deploy, start, trigger events, verify), and a checklist of expected outcomes
- Follow the folder naming convention: `NN-feature-name/` (numbered for ordering)
- Test plans are verified via Chrome (Web UI) + API calls for messages/signals
- See `docs/plans/2026-02-25-manual-test-plan-design.md` for the template and full design

### BPMN Fixture Authoring Rules
- **Always use `<scriptTask>` with `scriptFormat="csharp"`** — never bare `<task>`. The engine does not recognize `<task>` elements.
- **Message events require the Zeebe namespace** — add `xmlns:zeebe="http://camunda.org/schema/zeebe/1.0"` to `<definitions>` and include `<zeebe:subscription correlationKey="= varName" />` inside `<extensionElements>` which is a child of `<message>` (not a direct child of `<message>`). Example: `<message id="..." name="..."><extensionElements><zeebe:subscription correlationKey="= varName" /></extensionElements></message>`.
- **Every fixture must include a `<bpmndi:BPMNDiagram>` section** — the BPMN editor requires diagram info to render. Without it, the import silently produces a blank canvas.
- **Use short timer durations** for test fixtures (PT5S–PT10S) so tests complete quickly.

### API Endpoints for Manual Tests
- Deploy process: `POST https://localhost:7140/Workflow/deploy` — body: `{"BpmnXml":"<raw BPMN XML string>"}` — returns `{"ProcessDefinitionKey":"...","Version":1}` on success, 400 with `{"Error":"..."}` on parse failure
- Start instance: `POST https://localhost:7140/Workflow/start` — body: `{"WorkflowId":"process-id"}` or `{"WorkflowId":"process-id","Variables":{"key":"value"}}` to set initial variables before workflow starts (required for message event sub-processes that use variables as correlation keys)
- Send message: `POST https://localhost:7140/Workflow/message` — body: `{"MessageName":"...", "CorrelationKey":"...", "Variables":{}}`
- Send signal: `POST https://localhost:7140/Workflow/signal` — body: `{"SignalName":"..."}`
- Complete activity: `POST https://localhost:7140/Workflow/complete-activity` — body: `{"WorkflowInstanceId":"guid", "ActivityId":"activity-id", "Variables":{}}`
- Evaluate conditions: `POST https://localhost:7140/Workflow/evaluate-conditions` — body: `{"WorkflowId":"process-id", "Variables":{"key":"value"}}` — `WorkflowId` is optional; evaluates conditional start events against supplied variables
- Instance state: `GET https://localhost:7140/Workflow/instances/{instanceId}/state` — returns per-instance state snapshot (activeActivityIds, completedActivityIds, isStarted, isCompleted). Diagnostics/load-test endpoint; reads from the eventually-consistent EF projection.

## Regression tests

The full regression suite is the union of every plan under `tests/manual/`. Each plan has its own `.bpmn` fixture(s) and a step-by-step `test-plan.md` (deploy, start, trigger events, verify checkbox list).

**Universal prerequisites for every step:**
- Aspire stack running: `dotnet run --project Fleans.Aspire` (from `src/Fleans/`).
- A clean dev DB (delete the SQLite file or set a fresh `FLEANS_SQLITE_CONNECTION`) so prior runs don't leave stale instances.
- Web UI reachable at `https://localhost:7124`.
- API origin used in step bodies: `https://localhost:7140`.

**Reporting convention:** each numbered entry below is one regression "step". For each one, follow the linked `test-plan.md` end-to-end and report `PASSED`, `FAILED`, `BUG` (new regression — file an issue), or `KNOWN BUG` (matches a `> **KNOWN BUG:** …` note inside the linked plan; counts as PASSED for promotion purposes).

1. **Basic Workflow** — `tests/manual/01-basic-workflow/test-plan.md` (`simple-workflow.bpmn`). Start → task → end completes cleanly; verifies the deploy/start/complete lifecycle.
2. **Script Tasks** — `tests/manual/02-script-tasks/test-plan.md`. Chained C# script tasks create and mutate workflow variables; verifies the script engine + variable persistence.
3. **Exclusive Gateway** — `tests/manual/03-exclusive-gateway/test-plan.md`. `x = 7` → exclusive gateway with `x > 5` takes the truthy branch; default branch is not taken.
4. **Parallel Gateway** — `tests/manual/04-parallel-gateway/test-plan.md`. Two parallel branches set distinct variables, join, then end; verifies fork/join synchronization.
5. **Event-Based Gateway** — `tests/manual/05-event-based-gateway/test-plan.md`. Gateway races a 30s timer vs a message; sending the message via API before the timer fires wins.
6. **Call Activity** — `tests/manual/06-call-activity/test-plan.md`. Parent maps `input = 21` into a child that computes `result = input * 2` and maps it back; verifies cross-process variable mapping.
7. **Embedded SubProcess** — `tests/manual/07-subprocess/test-plan.md`. Embedded subprocess with its own start → script → end runs, then the parent continues.
8. **Timer Events** — `tests/manual/08-timer-events/test-plan.md`. Timer catch event (5s) pauses then resumes. **Known bug:** boundary events on `IntermediateCatchEvent` don't register subscriptions.
9. **Message Events** — `tests/manual/09-message-events/test-plan.md`. Workflow waits for a message correlated by `requestId`; sending it via API unblocks. **Known bug:** boundary on `IntermediateCatchEvent` (same root cause as #8).
10. **Signal Events** — `tests/manual/10-signal-events/test-plan.md`. Workflow waits for signal `globalAlert`; broadcasting via API unblocks. **Known bug:** boundary on `IntermediateCatchEvent` (same root cause as #8).
11. **Error Boundary Event** — `tests/manual/11-error-boundary/test-plan.md` (`child-that-fails.bpmn`, `error-on-call-activity.bpmn`). Child throws → parent's CallActivity error boundary catches and routes to handler. **Known bug:** child-process errors don't propagate to the parent error boundary; the CallActivity stays Running.
12. **Variable Scoping** — `tests/manual/12-variable-scoping/test-plan.md`. `shared` is set before a parallel fork; each branch overwrites it independently and the parent merge respects scope isolation.
13. **Multi-Instance Activity** — `tests/manual/13-multi-instance/test-plan.md`. Multi-instance loop spawns N children, each with its own variables; completion waits for all.
14. **Inclusive Gateway** — `tests/manual/14-inclusive-gateway/test-plan.md`. Inclusive (OR) gateway forks all conditions that evaluate true, then joins on all live tokens.
15. **Non-Interrupting Boundary Events** — `tests/manual/15-non-interrupting-boundaries/test-plan.md`. Boundaries fire without cancelling the attached activity; cycle timer boundaries fire repeatedly while the host activity continues.
16. **Message Start Event** — `tests/manual/16-message-start-event/test-plan.md`. Sending a message matching a process's start-event message name auto-creates an instance.
17. **Signal Start Event** — `tests/manual/17-signal-start-event/test-plan.md`. Broadcasting a signal matching a process's start-event signal name auto-creates an instance.
18. **Start Event Undeploy (Disable/Enable)** — `tests/manual/18-start-event-undeploy/test-plan.md`. Disabling a process definition unregisters its start-event listeners; re-enabling re-registers them.
19. **User Task** — `tests/manual/18-user-task/test-plan.md` (`user-task-approval.bpmn`). Full user-task lifecycle: appears in registry, claim/unclaim, complete with required output variables (409 on unauthorized claim or missing required vars), unregistered after completion. This is the regression home for #288 (User Task API status codes / completed-task removal).
20. **Event Sub-Process — Error (Interrupting)** — `tests/manual/19-event-subprocess-error/test-plan.md`. Error event sub-process catches a thrown error, runs its handler, and completes the enclosing scope.
21. **Complex Gateway** — `tests/manual/20-complex-gateway/test-plan.md`. Complex fork (conditional outgoing flows) + join with optional `activationCondition`.
22. **Event Sub-Process — Timer (Interrupting)** — `tests/manual/20-event-subprocess-timer/test-plan.md`. Timer event sub-process fires inside a scope, interrupts siblings, and runs the handler.
23. **Event Sub-Process — Message (Interrupting)** — `tests/manual/21-event-subprocess-message/test-plan.md`. Message event sub-process: scope-entry correlation against enclosing variables; on delivery, interrupts siblings.
24. **Event Sub-Process — Signal (Interrupting)** — `tests/manual/22-event-subprocess-signal/test-plan.md`. Signal event sub-process inside a scope; broadcast delivery interrupts siblings.
25. **Event Sub-Process — Non-Interrupting (Timer)** — `tests/manual/23-event-subprocess-non-interrupting/test-plan.md`. Non-interrupting variant runs in parallel with the host scope, with isolated child variable scope; timer cycles re-arm.
26. **Transaction Sub-Process (Happy Path)** — `tests/manual/26-transaction-subprocess/test-plan.md`. Transaction Sub-Process completes normally: variables merge into parent scope, all tasks inside show Completed. Hazard path is `KNOWN BUG` pending issue #231.
27. **Multiple Event (Catch, Throw, Boundary)** — `tests/manual/24-multiple-event/test-plan.md` (`message-or-signal-catch.bpmn`, `multi-throw.bpmn`, `multiple-boundary.bpmn`). Multiple intermediate catch races message vs signal (first-fires-wins; loser subscription cancelled); multiple intermediate throw fires every defined signal; multiple interrupting boundary (message + timer) cancels the host activity whichever triggers first.
28. **Escalation Event** — `tests/manual/24-escalation-event/test-plan.md` (`child-escalation-end.bpmn`, `child-escalation-throw.bpmn`, `parent-escalation-interrupting.bpmn`, `parent-escalation-non-interrupting.bpmn`). Child CallActivity throws escalation; parent's interrupting boundary cancels the CallActivity and runs the handler; non-interrupting boundary runs the handler while the child continues. Specific escalation codes match before catch-all; uncaught escalations are non-faulting per BPMN spec.
29. **Compensation Events** — `tests/manual/24-compensation-event/test-plan.md` (`compensation-broadcast.bpmn`). Broadcast compensation throw after two script tasks; verifies reverse-order handler execution (cancel_flight before cancel_hotel) and variable mutation by compensation handlers.
30. **Instance State Endpoint** — `tests/manual/27-instance-state-endpoint/test-plan.md`. `GET /Workflow/instances/{id}/state` returns per-instance state snapshot with camelCase JSON keys; verifies active activity tracking through the message-catch lifecycle and 404 for unknown instances.
31. **API JWT Authentication** — `tests/manual/28-api-auth/test-plan.md`. Opt-in JWT bearer authentication; verifies API works unauthenticated by default, returns 401 when auth is configured and no token is provided, and accepts valid tokens.
32. **Conditional Events** — `tests/manual/24-conditional-event/test-plan.md` (`conditional-event-test.bpmn`). Conditional intermediate catch event blocks until condition is true; conditional start event creates instances via evaluate-conditions API; conditional boundary event (interrupting) cancels host.
33. **Editor Tabs** — `tests/manual/29-editor-tabs/test-plan.md`. Multi-tab BPMN editor in the Admin UI: open/switch/close tabs, dirty tracking with confirm-close dialog, 10-tab cap, `localStorage` persistence across refresh, `beforeunload` warning when any tab is dirty.
34. **Management UI Authentication** — `tests/manual/30-web-auth/test-plan.md`. Opt-in OIDC for the Blazor Server admin UI; verifies (a) anonymous browse is allowed when no `Authentication` config is present, (b) `/dashboard` and any cascading-`AuthorizeView` page return 302 → IdP when auth is enabled, (c) login round-trip lands on the requested page (including `?query` parameters), (d) `/Account/Logout` is antiforgery-protected (bare POST is rejected, form-bound POST signs out and clears both schemes).
35. **Kafka Streaming Provider** — `tests/manual/35-kafka-streaming/test-plan.md` (`kafka-streams.bpmn`). Opt-in Kafka stream provider via `FLEANS_STREAMING_PROVIDER=Kafka`; verifies (a) Aspire dashboard provisions a `fleans-kafka` resource and forwards `Fleans__Streaming__Provider`/`Fleans__Streaming__Kafka__Brokers` env vars to the silo, (b) chained-script-task workflow completes after the silo is killed and restarted between tasks (at-least-once delivery), (c) the client-side `AdminClient.CreateTopicsAsync` ensure step auto-creates topics with the configured prefix.
36. **Cancel Event (Transaction Cancellation)** — `tests/manual/30-cancel-event/test-plan.md` (`cancel-transaction.bpmn`). Cancel End Event inside a Transaction triggers: active scope activities cancelled, Cancel Boundary Event fires, recovery flow runs to completion. Verifies transaction outcome is Cancelled.
37. **Custom Task Framework** — `tests/manual/37-custom-task-framework/test-plan.md` (`stub-custom-task.bpmn`). `<serviceTask type="...">` parses to `CustomTaskActivity`; with no plugin registered the activity stays Active indefinitely (manual `complete-activity` API call resumes it); registered plugin via `services.AddCustomTaskPlugin<T>()` claims the event, runs, and completes the activity; `GET /custom-tasks` reflects registered/dropped plugins as silos join/leave.
38. **Custom Task Editor (UI)** — `tests/manual/38-custom-task-editor/test-plan.md`. Management UI BPMN editor: `/admin/custom-tasks` admin page lists registered plugins with 5s auto-refresh; selecting a `<bpmn:serviceTask>` shows a plugin dropdown and typed widgets per `CustomTaskParameterSchema`; defaults seed as `<zeebe:input>` rows on plugin selection; required-field validation; replace-plugin confirmation dialog; unregistered task type shows warning bar; empty-state UI when no plugins registered.
39. **REST Caller plugin** — `tests/manual/39-rest-caller/test-plan.md` (`rest-call.bpmn`). `<serviceTask type="rest-call">` makes an outbound HTTP call: GET happy path returns body to the workflow; POST sends body and headers; 404 outside `successCodes` Fails the activity with code "404" and routes via boundary error event; timeout fails with "504"; `idempotencyKeyHeader="X-Request-Id"` causes the request to carry the activity instance id as the dedup key.
40. **Events Page (Admin UI)** — `tests/manual/31-events-page/test-plan.md`. New `/events` admin page renders five read-only sections (Message / Signal / Conditional Start Events + Active Message / Signal Subscriptions) projected directly from `IWorkflowQueryService.GetRegisteredEventsAsync`. **Refresh** re-queries without a full page reload; conditional listeners with `IsRegistered=false` are filtered out; subscription rows surface delete-on-completion (a row vanishes after Refresh once the engine deletes it). No per-page auth markup — route-level `AuthorizeRouteView` covers it (cross-link `tests/manual/30-web-auth/test-plan.md`). 8-char + ellipsis truncation with `<FluentTooltip>` on workflow / activity instance IDs.

> When adding a new manual test folder under `tests/manual/`, append a numbered entry here so the regression skill picks it up.

## Website regression tests

Website-specific manual tests live under `tests/manual/website/`. These run in a local dev server, not against the .NET stack.

**Universal prerequisites for every website step:**
- `cd website && npm install` has been run at least once.
- `npx playwright install chromium` has been run at least once (only needed for scripts that shell out to Playwright — poster generation + contrast check).
- Dev server NOT already running on port 4321 or 4327 or 4328.

**Reporting convention:** same as the BPMN list — `PASSED`, `FAILED`, `BUG`, or `KNOWN BUG`.

1. **3D Silo Landing Background** — `tests/manual/website/3d-landing/test-plan.md`. Splash page renders birds-eye Three.js silo scene as background; clicking outside the hero enters interactive orbit/zoom/pan mode with a close (×) button; mobile and reduced-motion users see a static WebP poster instead.
2. **Hero BPMN SVG** — `tests/manual/website/hero-bpmn-svg/test-plan.md`. The pre-rendered `public/hero-workflow-{light,dark}.svg` files parse as strict XML (no `</g>` imbalance, no leftover `djs-hit` / `djs-outline` / `djs-dragger` classes, XML prolog + SVG 1.1 DOCTYPE preserved); landing page hero renders in both themes; regeneration is reproducible. Regression home for #366.
3. **Landing Deployment-Posture Cards** — `tests/manual/website/landing-deployment-cards/test-plan.md`. The "Why Fleans?" `<CardGrid>` on `/fleans/` renders 9 cards (six runtime/engine + three deployment-posture); the `setting`, `list-format`, `puzzle` icons resolve to real glyphs in light AND dark themes (not silent Starlight fallbacks); each new card's "Learn how →" link returns 200 to `reference/self-hosting/`, `reference/persistence/`, `reference/streaming/`; mobile (≤ 480 px) single-column flow is readable.

> When adding a new website test folder, append a numbered entry here.

## Persistence Providers

Two providers: **SQLite** (default, local dev) and **PostgreSQL** (production/load testing). Selected via configuration — no code changes needed.

- **Config key:** `Persistence:Provider` (values: `Sqlite` | `Postgres`, case-insensitive, default `Sqlite`)
- **Aspire:** set `FLEANS_PERSISTENCE_PROVIDER=Postgres` env var before launch to auto-provision a Postgres container
- **Connection strings:** SQLite uses `FLEANS_SQLITE_CONNECTION` / `FLEANS_QUERY_CONNECTION` env vars. PostgreSQL uses `ConnectionStrings:fleans` (required) and `ConnectionStrings:fleans-query` (optional read replica).
- **Migration strategy:** SQLite uses `EnsureCreated()`. PostgreSQL uses `MigrateAsync()` (migrations applied automatically by `Fleans.Api` on startup).
- **Migrations live per-provider:** `Fleans.Persistence.Sqlite/Migrations/Command/` and `Fleans.Persistence.PostgreSql/Migrations/Command/`. Only command-context migrations are maintained (command and query share the same database).
- **Provider packages:** `Fleans.Persistence.Sqlite` and `Fleans.Persistence.PostgreSql` — each registers a `RelationalModelCustomizer` subclass via `ReplaceService<IModelCustomizer>` for provider-specific model tweaks (e.g., SQLite stores `DateTimeOffset` as string; PostgreSQL uses native `timestamptz`).
- **Adding a new provider:** Create a new `Fleans.Persistence.<Provider>` project, implement a `<Provider>ModelCustomizer : RelationalModelCustomizer`, add an `Add<Provider>Persistence()` extension, generate initial migrations, and wire into host `Program.cs` files.
- **Custom-task catalog persistence:** `CustomTaskCatalogGrain` uses `IPersistentState<CustomTaskCatalogState>` keyed by `GrainStorageNames.CustomTaskCatalog`, backed by `EfCoreCustomTaskCatalogGrainStorage` and the `CustomTaskCatalogEntries` table (composite PK on `(TaskType, SiloName)`; `ParameterSchemaJson` stored as a JSON text column via `System.Text.Json`). Test clusters must register memory grain storage for this name in `WorkflowTestBase` alongside the other registries.
- **Test parity (Sqlite ↔ PostgreSQL):** every EF/grain-storage class in `Fleans.Persistence.Tests` is parametrised via `[DataTestMethod] [DataRow(PersistenceProvider.Sqlite)] [DataRow(PersistenceProvider.Postgres)]` against the `Infrastructure/PersistenceTestBase` fixture. Default `dotnet test` runs only the SQLite rows (no Docker). To exercise the PG rows locally:
  ```bash
  cd src/Fleans
  FLEANS_PG_TESTS=1 dotnet test --filter "TestCategory=Postgres"
  ```
  Requires Docker (Testcontainers boots `postgres:16-alpine`). Without `FLEANS_PG_TESTS=1` the PG rows surface as `Inconclusive` (non-failing) — never `Failed`. CI runs the dedicated `PostgreSQL tests` job (`.github/workflows/pg-tests.yml`) on every PR. **Bump `PostgresImage` in `Infrastructure/PostgresContainerFixture.cs` whenever the production deploy target moves** — today it tracks Aspire's `Aspire.Hosting.PostgreSQL` default (PG 16).

## Things to Know

- **Aspire is the startup project**, not Api or Web
- **WorkflowInstance uses JournaledGrain with event sourcing** — events are persisted via EfCoreEventStore, read-side state is projected via EfCoreWorkflowStateProjection (CQRS pattern). Other grains (ProcessDefinition, correlations, timers, start event listeners, user tasks) use IPersistentState with EF Core IGrainStorage.
- **BPMN coverage is partial** — see the table in `README.md` for what's implemented
- **Design docs** live in `docs/plans/` — check them before making architectural changes

