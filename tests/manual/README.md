# Manual Test Plans

Each `NN-feature-name/` subfolder contains a manual test plan: one or more `.bpmn` fixtures plus a step-by-step `test-plan.md` (deploy, start, trigger events, verify). The union of every plan here is the project's full manual regression suite.

See `docs/plans/2026-02-25-manual-test-plan-design.md` for the template and rationale.

## Universal prerequisites (BPMN suite)

- Aspire stack running: `dotnet run --project Fleans.Aspire` (from `src/Fleans/`).
- A clean dev DB (delete the SQLite file or set a fresh `FLEANS_SQLITE_CONNECTION`) so prior runs don't leave stale instances.
- Web UI reachable at `https://localhost:7124`.
- API origin used in step bodies: `https://localhost:7140`.

## Universal prerequisites (website suite)

- `cd website && npm install` has been run at least once.
- `npx playwright install chromium` has been run at least once (only needed for scripts that shell out to Playwright — poster generation + contrast check).
- Dev server NOT already running on port 4321 or 4327 or 4328.

## Reporting convention

Each numbered entry below is one regression "step". For each one, follow the linked `test-plan.md` end-to-end and report `PASSED`, `FAILED`, `BUG` (new regression — file an issue), or `KNOWN BUG` (matches a `> **KNOWN BUG:** …` note inside the linked plan; counts as PASSED for promotion purposes).

## BPMN regression suite

1. **Basic Workflow** — `01-basic-workflow/test-plan.md` (`simple-workflow.bpmn`). Start → task → end completes cleanly; verifies the deploy/start/complete lifecycle.
2. **Script Tasks** — `02-script-tasks/test-plan.md`. Chained C# script tasks create and mutate workflow variables; verifies the script engine + variable persistence.
3. **Exclusive Gateway** — `03-exclusive-gateway/test-plan.md`. `x = 7` → exclusive gateway with `x > 5` takes the truthy branch; default branch is not taken.
4. **Parallel Gateway** — `04-parallel-gateway/test-plan.md`. Two parallel branches set distinct variables, join, then end; verifies fork/join synchronization.
5. **Event-Based Gateway** — `05-event-based-gateway/test-plan.md`. Gateway races a 30s timer vs a message; sending the message via API before the timer fires wins.
6. **Call Activity** — `06-call-activity/test-plan.md`. Parent maps `input = 21` into a child that computes `result = input * 2` and maps it back; verifies cross-process variable mapping.
7. **Embedded SubProcess** — `07-subprocess/test-plan.md`. Embedded subprocess with its own start → script → end runs, then the parent continues.
8. **Timer Events** — `08-timer-events/test-plan.md`. Timer catch event (5s) pauses then resumes. **Known bug:** boundary events on `IntermediateCatchEvent` don't register subscriptions.
9. **Message Events** — `09-message-events/test-plan.md`. Workflow waits for a message correlated by `requestId`; sending it via API unblocks. **Known bug:** boundary on `IntermediateCatchEvent` (same root cause as #8).
10. **Signal Events** — `10-signal-events/test-plan.md`. Workflow waits for signal `globalAlert`; broadcasting via API unblocks. **Known bug:** boundary on `IntermediateCatchEvent` (same root cause as #8).
11. **Error Boundary Event** — `11-error-boundary/test-plan.md` (`child-that-fails.bpmn`, `error-on-call-activity.bpmn`). Child throws → parent's CallActivity error boundary catches and routes to handler. **Known bug:** child-process errors don't propagate to the parent error boundary; the CallActivity stays Running.
12. **Variable Scoping** — `12-variable-scoping/test-plan.md`. `shared` is set before a parallel fork; each branch overwrites it independently and the parent merge respects scope isolation.
13. **Multi-Instance Activity** — `13-multi-instance/test-plan.md`. Multi-instance loop spawns N children, each with its own variables; completion waits for all.
14. **Inclusive Gateway** — `14-inclusive-gateway/test-plan.md`. Inclusive (OR) gateway forks all conditions that evaluate true, then joins on all live tokens.
15. **Non-Interrupting Boundary Events** — `15-non-interrupting-boundaries/test-plan.md`. Boundaries fire without cancelling the attached activity; cycle timer boundaries fire repeatedly while the host activity continues.
16. **Message Start Event** — `16-message-start-event/test-plan.md`. Sending a message matching a process's start-event message name auto-creates an instance.
17. **Signal Start Event** — `17-signal-start-event/test-plan.md`. Broadcasting a signal matching a process's start-event signal name auto-creates an instance.
18. **Start Event Undeploy (Disable/Enable)** — `18-start-event-undeploy/test-plan.md`. Disabling a process definition unregisters its start-event listeners; re-enabling re-registers them.
19. **User Task** — `18-user-task/test-plan.md` (`user-task-approval.bpmn`). Full user-task lifecycle: appears in registry, claim/unclaim, complete with required output variables (409 on unauthorized claim or missing required vars), unregistered after completion. This is the regression home for #288 (User Task API status codes / completed-task removal).
20. **Event Sub-Process — Error (Interrupting)** — `19-event-subprocess-error/test-plan.md`. Error event sub-process catches a thrown error, runs its handler, and completes the enclosing scope.
21. **Complex Gateway** — `20-complex-gateway/test-plan.md`. Complex fork (conditional outgoing flows) + join with optional `activationCondition`.
22. **Event Sub-Process — Timer (Interrupting)** — `20-event-subprocess-timer/test-plan.md`. Timer event sub-process fires inside a scope, interrupts siblings, and runs the handler.
23. **Event Sub-Process — Message (Interrupting)** — `21-event-subprocess-message/test-plan.md`. Message event sub-process: scope-entry correlation against enclosing variables; on delivery, interrupts siblings.
24. **Event Sub-Process — Signal (Interrupting)** — `22-event-subprocess-signal/test-plan.md`. Signal event sub-process inside a scope; broadcast delivery interrupts siblings.
25. **Event Sub-Process — Non-Interrupting (Timer)** — `23-event-subprocess-non-interrupting/test-plan.md`. Non-interrupting variant runs in parallel with the host scope, with isolated child variable scope; timer cycles re-arm.
26. **Transaction Sub-Process (Happy Path)** — `26-transaction-subprocess/test-plan.md`. Transaction Sub-Process completes normally: variables merge into parent scope, all tasks inside show Completed. Hazard path is `KNOWN BUG` pending issue #231.
27. **Multiple Event (Catch, Throw, Boundary)** — `24-multiple-event/test-plan.md` (`message-or-signal-catch.bpmn`, `multi-throw.bpmn`, `multiple-boundary.bpmn`). Multiple intermediate catch races message vs signal (first-fires-wins; loser subscription cancelled); multiple intermediate throw fires every defined signal; multiple interrupting boundary (message + timer) cancels the host activity whichever triggers first.
28. **Escalation Event** — `24-escalation-event/test-plan.md` (`child-escalation-end.bpmn`, `child-escalation-throw.bpmn`, `parent-escalation-interrupting.bpmn`, `parent-escalation-non-interrupting.bpmn`). Child CallActivity throws escalation; parent's interrupting boundary cancels the CallActivity and runs the handler; non-interrupting boundary runs the handler while the child continues. Specific escalation codes match before catch-all; uncaught escalations are non-faulting per BPMN spec.
29. **Compensation Events** — `24-compensation-event/test-plan.md` (`compensation-broadcast.bpmn`). Broadcast compensation throw after two script tasks; verifies reverse-order handler execution (cancel_flight before cancel_hotel) and variable mutation by compensation handlers.
30. **Instance State Endpoint** — `27-instance-state-endpoint/test-plan.md`. `GET /Workflow/instances/{id}/state` returns per-instance state snapshot with camelCase JSON keys; verifies active activity tracking through the message-catch lifecycle and 404 for unknown instances.
31. **API JWT Authentication** — `28-api-auth/test-plan.md`. Opt-in JWT bearer authentication; verifies API works unauthenticated by default, returns 401 when auth is configured and no token is provided, and accepts valid tokens.
32. **Conditional Events** — `24-conditional-event/test-plan.md` (`conditional-event-test.bpmn`). Conditional intermediate catch event blocks until condition is true; conditional start event creates instances via evaluate-conditions API; conditional boundary event (interrupting) cancels host.
33. **Editor Tabs** — `29-editor-tabs/test-plan.md`. Multi-tab BPMN editor in the Admin UI: open/switch/close tabs, dirty tracking with confirm-close dialog, 10-tab cap, `localStorage` persistence across refresh, `beforeunload` warning when any tab is dirty.
34. **Management UI Authentication** — `30-web-auth/test-plan.md`. Opt-in OIDC for the Blazor Server admin UI; verifies (a) anonymous browse is allowed when no `Authentication` config is present, (b) `/dashboard` and any cascading-`AuthorizeView` page return 302 → IdP when auth is enabled, (c) login round-trip lands on the requested page (including `?query` parameters), (d) `/Account/Logout` is antiforgery-protected (bare POST is rejected, form-bound POST signs out and clears both schemes).
35. **Kafka Streaming Provider** — `35-kafka-streaming/test-plan.md` (`kafka-streams.bpmn`). Opt-in Kafka stream provider via `FLEANS_STREAMING_PROVIDER=Kafka`; verifies (a) Aspire dashboard provisions a `fleans-kafka` resource and forwards `Fleans__Streaming__Provider`/`Fleans__Streaming__Kafka__Brokers` env vars to the silo, (b) chained-script-task workflow completes after the silo is killed and restarted between tasks (at-least-once delivery), (c) the client-side `AdminClient.CreateTopicsAsync` ensure step auto-creates topics with the configured prefix.
36. **Cancel Event (Transaction Cancellation)** — `30-cancel-event/test-plan.md` (`cancel-transaction.bpmn`). Cancel End Event inside a Transaction triggers: active scope activities cancelled, Cancel Boundary Event fires, recovery flow runs to completion. Verifies transaction outcome is Cancelled.
37. **Custom Task Framework** — `37-custom-task-framework/test-plan.md` (`stub-custom-task.bpmn`). `<serviceTask type="...">` parses to `CustomTaskActivity`; with no plugin registered the activity stays Active indefinitely (manual `complete-activity` API call resumes it); registered plugin via `services.AddCustomTaskPlugin<T>()` claims the event, runs, and completes the activity; `GET /custom-tasks` reflects registered/dropped plugins as silos join/leave.
38. **Custom Task Editor (UI)** — `38-custom-task-editor/test-plan.md`. Management UI BPMN editor: `/admin/custom-tasks` admin page lists registered plugins with 5s auto-refresh; selecting a `<bpmn:serviceTask>` shows a plugin dropdown and typed widgets per `CustomTaskParameterSchema`; defaults seed as `<zeebe:input>` rows on plugin selection; required-field validation; replace-plugin confirmation dialog; unregistered task type shows warning bar; empty-state UI when no plugins registered.
39. **REST Caller plugin** — `39-rest-caller/test-plan.md` (`rest-call.bpmn`). `<serviceTask type="rest-call">` makes an outbound HTTP call: GET happy path returns body to the workflow; POST sends body and headers; 404 outside `successCodes` Fails the activity with code "404" and routes via boundary error event; timeout fails with "504"; `idempotencyKeyHeader="X-Request-Id"` causes the request to carry the activity instance id as the dedup key.
40. **Custom Worker Host** — `40-custom-worker-host/test-plan.md` (`rest-call.bpmn`). `Fleans.CustomWorkerHost` is the worked-example plugin-host deployable. Two modes: (i) standalone — `dotnet run --project Fleans.CustomWorkerHost` against dev-Aspire's Redis claims a `<serviceTask type="rest-call">` activity end-to-end; (ii) docker-compose — `aspire publish -t docker-compose` emits a `fleans-custom-worker` service and the same workflow runs through the published stack. Verifies the host references ONLY `Fleans.Worker` + plugin assemblies (no `Fleans.Application` / `Fleans.Domain` references).
41. **NuGet publish on release** — `41-nuget-publish/test-plan.md` (no BPMN — release-infrastructure plan). `.github/workflows/nuget-publish.yml` triggers on `release.published` and `workflow_dispatch`; packs `Fleans.Application.Abstractions`, `Fleans.Worker`, and `Fleans.Plugins.RestCaller`; pushes per-file so `.snupkg` symbols auto-pair to the nuget.org symbol server. Verifies (a) local pack smoke produces 3 `.nupkg` + 3 `.snupkg`, (b) `workflow_dispatch` with `version=0.0.0-ci-test` is a dry-run that uploads artifacts but skips push, (c) a real `gh release create v<VERSION>` run publishes to nuget.org with README + MIT license + repo URL, (d) re-runs are idempotent under `--skip-duplicate`, (e) external consumers can `dotnet add package` and build, (f) `.snupkg` symbols are reachable, (g) two consecutive `dotnet pack` runs produce bit-identical packages.

## Website regression suite

Website-specific manual tests live under `website/`. These run in a local dev server, not against the .NET stack.

1. **3D Silo Landing Background** — `website/3d-landing/test-plan.md`. Splash page renders birds-eye Three.js silo scene as background; clicking outside the hero enters interactive orbit/zoom/pan mode with a close (×) button; mobile and reduced-motion users see a static WebP poster instead.
2. **Hero BPMN SVG** — `website/hero-bpmn-svg/test-plan.md`. The pre-rendered `public/hero-workflow-{light,dark}.svg` files parse as strict XML (no `</g>` imbalance, no leftover `djs-hit` / `djs-outline` / `djs-dragger` classes, XML prolog + SVG 1.1 DOCTYPE preserved); landing page hero renders in both themes; regeneration is reproducible. Regression home for #366.
3. **Landing Deployment-Posture Cards** — `website/landing-deployment-cards/test-plan.md`. The "Why Fleans?" `<CardGrid>` on `/fleans/` renders 9 cards (six runtime/engine + three deployment-posture); the `setting`, `list-format`, `puzzle` icons resolve to real glyphs in light AND dark themes (not silent Starlight fallbacks); each new card's "Learn how →" link returns 200 to `reference/self-hosting/`, `reference/persistence/`, `reference/streaming/`; mobile (≤ 480 px) single-column flow is readable.
4. **Error Handling guide** — `website/error-handling-guide/test-plan.md`. The `guides/error-handling/` page renders under the *Getting Started* sidebar between *Hosting Plugins (Custom Worker Host)* and *BPMN Editor* in both themes; both `:::caution` admonitions (child-process error propagation KNOWN BUG, compensation variable-scope invariant) display correctly; all four cited fixture folders (#11, #19, #24-escalation, #24-compensation) are referenced by name; drift-guard line pins (`WorkflowExecution.cs:723-784`, `BpmnConverter.cs:132,209-269,665-710,759-815`, `BadRequestActivityException.cs:5-13`, `CustomTaskFailedActivityException.cs:5-22`, `DynamicExpressoScriptExpressionExecutor.cs:46`) still resolve to the named symbols at the current branch SHA.

## API endpoints for manual tests

- Deploy process: `POST https://localhost:7140/Workflow/deploy` — body: `{"BpmnXml":"<raw BPMN XML string>"}` — returns `{"ProcessDefinitionKey":"...","Version":1}` on success, 400 with `{"Error":"..."}` on parse failure
- Start instance: `POST https://localhost:7140/Workflow/start` — body: `{"WorkflowId":"process-id"}` or `{"WorkflowId":"process-id","Variables":{"key":"value"}}` to set initial variables before workflow starts (required for message event sub-processes that use variables as correlation keys)
- Send message: `POST https://localhost:7140/Workflow/message` — body: `{"MessageName":"...", "CorrelationKey":"...", "Variables":{}}`
- Send signal: `POST https://localhost:7140/Workflow/signal` — body: `{"SignalName":"..."}`
- Complete activity: `POST https://localhost:7140/Workflow/complete-activity` — body: `{"WorkflowInstanceId":"guid", "ActivityId":"activity-id", "Variables":{}}`
- Evaluate conditions: `POST https://localhost:7140/Workflow/evaluate-conditions` — body: `{"WorkflowId":"process-id", "Variables":{"key":"value"}}` — `WorkflowId` is optional; evaluates conditional start events against supplied variables
- Instance state: `GET https://localhost:7140/Workflow/instances/{instanceId}/state` — returns per-instance state snapshot (activeActivityIds, completedActivityIds, isStarted, isCompleted). Diagnostics/load-test endpoint; reads from the eventually-consistent EF projection.

## BPMN fixture authoring rules

- **Always use `<scriptTask>` with `scriptFormat="csharp"`** — never bare `<task>`. The engine does not recognize `<task>` elements.
- **Message events require the Zeebe namespace** — add `xmlns:zeebe="http://camunda.org/schema/zeebe/1.0"` to `<definitions>` and include `<zeebe:subscription correlationKey="= varName" />` inside `<extensionElements>` which is a child of `<message>` (not a direct child of `<message>`). Example: `<message id="..." name="..."><extensionElements><zeebe:subscription correlationKey="= varName" /></extensionElements></message>`.
- **Every fixture must include a `<bpmndi:BPMNDiagram>` section** — the BPMN editor requires diagram info to render. Without it, the import silently produces a blank canvas.
- **Use short timer durations** for test fixtures (PT5S–PT10S) so tests complete quickly.
- **`<script>` content is a DynamicExpresso expression, not a C# statement.** `_context.x = value` is valid; `return …;` and `var x = …;` are not. Silent script failures auto-complete the enclosing Event Sub-Process scope and manifest as the inner `EndEvent` missing from `CompletedActivities` (root cause of #285 — see `EventSubProcess*Tests` for regression guards across all four ESP trigger variants).

## Adding a new manual test plan

1. Create `tests/manual/NN-feature-name/` (next sequential number).
2. Add `.bpmn` fixture(s) and a `test-plan.md` (deploy / start / trigger / verify checklist — see existing folders for shape).
3. Append a numbered entry to the appropriate suite above (BPMN or website) so the regression skill picks it up.
