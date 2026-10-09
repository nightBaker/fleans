# 11 — Error Boundary Event

## Scenario
A parent process calls a child that throws an exception. An error boundary event on the call activity catches the error and routes to an error-handling path. The parent does NOT fail.

## Prerequisites
- Aspire stack running
- **Deploy `child-that-fails.bpmn` FIRST**

## Steps

> **KNOWN BUG:** Child process errors don't propagate to parent error boundary on CallActivity. The CallActivity stays Running indefinitely. See `docs/plans/2026-02-25-manual-test-results.md`.

### 1. Deploy child process
- Import `child-that-fails.bpmn`, deploy

### 2. Deploy parent process
- Import `error-on-call-activity.bpmn`, deploy

### 3. Start the parent
- Start `error-boundary-test`, open Instance Viewer

### 4. Verify outcome
- [ ] Parent instance status: **Completed** (NOT failed)
- [ ] `errorHandler` in completed activities (error boundary caught the exception)
- [ ] `happyEnd` NOT in completed activities
- [ ] Variables: `errorHandled` = **true**
- [ ] Activities tab: `callFailing` shows error details (code 500, message "Something went wrong")
- [ ] BPMN canvas highlights the error path

## Scenario B: Error boundary on a plain script task (error-on-script-task.bpmn)
An error boundary attached directly to a script task (no call activity). The script computes `10 / divisor`; `divisor = 0` throws. Automated by `ErrorBoundaryOnTaskTests`.

### Steps
1. Deploy `error-on-script-task.bpmn`.
2. Start `error-boundary-script-task` with variables `{"divisor": 0}`.
3. Start a second instance with `{"divisor": 2}`.

### Expected — `divisor = 0`
- [ ] Instance **Completed** (not failed, not cancelled)
- [ ] `errorBoundary`, `errorHandler`, `errorEnd` completed; `happyPath` / `happyEnd` NOT completed
- [ ] `riskyScript` shows error code **500**
- [ ] Variables: `errorHandled` = **true**

### Expected — `divisor = 2`
- [ ] Instance **Completed** via `happyPath` → `happyEnd`; boundary not triggered
- [ ] Variables: `quotient` = **5**
