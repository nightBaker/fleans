# Event Sub-Process Slice #C — Timer Event Sub-Process (Interrupting) Implementation Plan

**Goal:** Runtime execution of **interrupting, timer-triggered** Event Sub-Processes. When the enclosing scope is entered, any `EventSubProcess` whose start event is a `TimerStartEvent` registers a timer. When the timer fires, the event sub-process activates (cancels siblings, runs handler in an isolated child variable scope). Deregistration happens on scope completion or cancellation.

**Architecture:** Mirrors the slice B error path — same `EventSubProcess` scope-host spawn + child variable scope + start event, but registration is proactive (at scope entry) rather than reactive. Reuses `RegisterTimerEffect` / `UnregisterTimerEffect` and the existing `TimerCallbackGrain`. Scope C ships **one-shot interrupting timers only** (Duration / Date). Cycle timers are deferred to slice #F (non-interrupting variant).

**Parent issue / design:** #227, #265.

## Key decisions

1. **Timer grain keying**: reuse `RegisterTimerEffect(workflowInstanceId, hostActivityInstanceId, timerActivityId, dueTime)`. For event-sub timers:
   - `hostActivityInstanceId` = the **enclosing scope container's `ActivityInstanceId`**, or the workflow instance id (`_state.Id`) at root scope (so the key is unique and stable per scope-instance).
   - `timerActivityId` = the `TimerStartEvent.ActivityId` (the start event inside the `EventSubProcess`).
2. **Registration points**:
   - Workflow start (`Start()` path) — root-scope event-sub timers.
   - `ProcessOpenSubProcess` — nested scope event-sub timers.
3. **Deregistration points**:
   - `CompleteFinishedSubProcessScopes` — when a `SubProcess` host completes, unregister any event-sub timers defined inside it. When a root workflow completes via the event-sub wind-down path (or normal end), unregister root-level event-sub timers.
   - `CancelScopeChildren` — when any scope is cancelled (e.g. by a boundary interrupt or by another interrupting event sub-process), unregister its event-sub timers.
   - `TryActivateErrorEventSubProcess` and the new `TryActivateTimerEventSubProcess` — when an interrupting event sub-process fires, unregister peer event-sub timers in the same enclosing scope.
4. **Firing**: a new path in `HandleTimerFired` detects that the timer activity lives inside an `EventSubProcess` and dispatches to `TryActivateTimerEventSubProcess`, which mirrors `TryActivateErrorEventSubProcess` (cancel siblings in the enclosing scope, spawn the `EventSubProcess` host, create a child variable scope, spawn the `TimerStartEvent` inside it, which will auto-complete and flow to the handler).
5. **Cycle timers**: out of scope for slice C. The firing path will explicitly throw or log-and-ignore if the `TimerDefinition.Type == Cycle`. Slice #F adds cycle support.

## File changes

- `src/Fleans/Fleans.Domain/Definitions/Workflow.cs` — add `FindEventSubProcessTimers()` default interface method enumerating `(EventSubProcess, TimerStartEvent)` pairs directly inside the scope. Add `FindEventSubProcessByStartEvent(string startEventActivityId)` to locate the containing EventSubProcess + its enclosing scope during firing.
- `src/Fleans/Fleans.Domain/Aggregates/WorkflowExecution.cs`:
  - `Start()` — after emitting `ExecutionStarted` and the root start, call a new private `RegisterEventSubProcessTimersForScope(IWorkflowDefinition scope, Guid? scopeContainerId, List<IInfrastructureEffect> effects)` for the root definition.
  - `ProcessOpenSubProcess` — same call against the opened SubProcess definition, passing `hostActivityInstanceId` as the container id.
  - `HandleTimerFired` — before the existing BoundaryTimerEvent branch, detect `TimerStartEvent` whose enclosing definition is an `EventSubProcess`; route to new `TryActivateTimerEventSubProcess` helper.
  - `TryActivateTimerEventSubProcess` — mirror `TryActivateErrorEventSubProcess`. Determine enclosing scope container id (via `FindEventSubProcessByStartEvent` and `FindEnclosingScopeContainerId`). Cancel siblings. Spawn `EventSubProcess` host, create child variable scope, spawn `TimerStartEvent` inside.
  - `CompleteFinishedSubProcessScopes` — when completing a `SubProcess` host (not an event sub-process — those hold no timers yet), enumerate its event-sub timers and emit `UnregisterTimerEffect` for each, keyed to the host's `ActivityInstanceId`. At root completion (`completedRootEventSubProcess` wind-down path), also unregister root-level event-sub timers.
  - `CancelScopeChildren` — after cancelling children, unregister event-sub timers in the cancelled scope.
  - `TryActivateErrorEventSubProcess` — replace the peer-unsubscribe TODO with an actual call: unregister peer event-sub timers in the enclosing scope (excluding the one that just fired — for slice C, the error path has no timer to skip, but this also future-proofs the helper).
- `src/Fleans/Fleans.Domain.Tests/` — two new test files:
  - `WorkflowDefinitionEventSubProcessTimerTests.cs` — definition lookup tests.
  - `WorkflowExecutionTimerEventSubProcessTests.cs` — aggregate tests: registration at start, firing activates, cycle timer rejected, deregistration on parent complete, deregistration on parent cancelled, peer unregister when error event sub-process fires first.
- `src/Fleans/Fleans.Application.Tests/EventSubProcessTimerTests.cs` — TestCluster end-to-end: fire timer externally, verify handler runs.
- `tests/manual/20-event-subprocess-timer/` — BPMN fixture (`PT5S` duration) + `test-plan.md`.
- `website/src/content/docs/concepts/bpmn-support.md` — update the Event Sub-Process bullet.

## Task breakdown

1. **Definition helpers + tests** (`Workflow.cs` + new test file). TDD.
2. **Registration at scope entry** (`Start` + `ProcessOpenSubProcess`). Aggregate test: register effect emitted.
3. **Firing** (`HandleTimerFired` + `TryActivateTimerEventSubProcess`). Aggregate test: siblings cancelled, host spawned, child scope created, TimerStartEvent spawned. Cycle rejected.
4. **Deregistration** (scope complete, scope cancel, peer unregister). Aggregate tests.
5. **Integration test** (TestCluster).
6. **Manual test + docs**.
7. **PR**.

Each task commits separately. Tests first per TDD. Keep slice B patterns where applicable.
