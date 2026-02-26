# Manual Test Results — 2026-02-25

## Summary

**14 passed, 1 bug remaining** across 15 test scenarios covering all implemented BPMN features. Bug 1 fixed in PR #105, Bug 2 fixed in `fix/error-boundary-on-call-activity`.

## Results

| # | Test | Fixture | Status |
|---|------|---------|--------|
| 01 | Basic Workflow | `01-basic-workflow/simple-start-end.bpmn` | PASSED |
| 02 | Script Variables | `02-script-task/script-variables.bpmn` | PASSED |
| 03 | Exclusive Gateway | `03-exclusive-gateway/exclusive-branch.bpmn` | PASSED |
| 04 | Parallel Gateway | `04-parallel-gateway/parallel-fork-join.bpmn` | PASSED |
| 05 | Event-Based Gateway | `05-event-based-gateway/timer-vs-message-race.bpmn` | PASSED |
| 06 | Call Activity | `06-call-activity/{parent,child}-process.bpmn` | PASSED |
| 07 | SubProcess (Embedded) | `07-subprocess/embedded-subprocess.bpmn` | PASSED |
| 08a | Timer Intermediate Catch | `08-timer-events/timer-catch.bpmn` | PASSED |
| 08b | Timer Boundary on Message Catch | `08-timer-events/timer-boundary.bpmn` | PASSED (fixed PR #105) |
| 09a | Message Intermediate Catch | `09-message-events/message-catch.bpmn` | PASSED |
| 09b | Message Boundary on Timer Catch | `09-message-events/message-boundary.bpmn` | PASSED (fixed PR #105) |
| 10a | Signal Intermediate Catch | `10-signal-events/signal-catch-throw.bpmn` | PASSED |
| 10b | Signal Boundary on Timer Catch | `10-signal-events/signal-boundary.bpmn` | PASSED (fixed PR #105) |
| 11 | Error Boundary on Call Activity | `11-error-boundary/error-on-call-activity.bpmn` | PASSED (fixed) |
| 12 | Variable Scoping (Parallel Isolation) | `12-variable-scoping/parallel-variable-isolation.bpmn` | PASSED |

---

## Bug 1: Boundary events on IntermediateCatchEvents don't register subscriptions ✓ FIXED

**Status:** Fixed in PR #105 (commit 0009d9f)

**Affects:** Tests 08b, 09b, 10b

**Symptom:** When a boundary event (timer, message, or signal) is attached to an intermediate catch event, the boundary event's subscription is never registered. The host activity starts waiting but the boundary never becomes active.

- **08b (Timer Boundary on Message Catch):** Timer boundary attached to a message catch event. The message catch waits correctly, but the 5-second timer boundary never fires. The instance hangs indefinitely on the message catch.
- **09b (Message Boundary on Timer Catch):** Message boundary attached to a 60s timer catch event. Sending the correct message (`cancelRequest` with correlationKey `req-789`) returns `"No subscription found"`. The boundary message subscription was never registered.
- **10b (Signal Boundary on Timer Catch):** Signal boundary attached to a 60s timer catch event. Broadcasting signal `emergencyStop` returns `deliveredCount: 0`. The boundary signal subscription was never registered.

**Root cause (likely):** `IntermediateCatchEvent` activities (`TimerIntermediateCatchEvent`, `MessageIntermediateCatchEvent`, `SignalIntermediateCatchEvent`) do not call boundary registration logic when they begin executing. The boundary registration path exists for tasks and call activities but is missing for intermediate catch events.

**Expected behavior:** When an intermediate catch event with a boundary event starts waiting, the boundary event subscription should be registered so that the boundary can interrupt the host if triggered.

---

## Bug 2: Child process errors don't propagate to parent error boundary ✓ FIXED

**Status:** Fixed in branch `fix/error-boundary-on-call-activity`

**Affects:** Test 11

**Symptom:** A CallActivity (`callFailing`) invokes a child process (`child-that-fails`) whose script task throws `new System.Exception("Something went wrong")`. The child process fails, but the parent's error boundary event (`errorBoundary`) is never triggered. The CallActivity remains in `Running` state indefinitely.

**Test setup:**
- `child-that-fails` process: `start -> failingScript (throws Exception) -> end`
- `error-boundary-test` process: `start -> callFailing (calledElement="child-that-fails") -> happyEnd -> end1`, with `errorBoundary -> errorHandler -> end2`

**Expected behavior:** When the child process fails with an error, the error should propagate back to the parent CallActivity, which should then trigger the attached error boundary event, routing execution to `errorHandler`.

**Root cause (likely):** The CallActivity grain does not handle the child process failure callback or does not translate child failure into a boundary event trigger on the parent workflow instance.

---

## Common Pattern

All 4 bugs involved boundary events that failed to activate. Both have been fixed:

| Host Activity Type | Boundary Type | Status |
|---|---|---|
| Task / ScriptTask | Timer | Not tested (no fixture) |
| IntermediateCatchEvent (Message) | Timer | Fixed (PR #105) |
| IntermediateCatchEvent (Timer) | Message | Fixed (PR #105) |
| IntermediateCatchEvent (Timer) | Signal | Fixed (PR #105) |
| CallActivity | Error | Fixed |
