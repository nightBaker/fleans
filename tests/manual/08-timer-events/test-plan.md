# 08 — Timer Events

## Scenario A: Timer Intermediate Catch
A workflow pauses at a timer catch event (5s), then continues. Verifies timer scheduling and resumption.

## Scenario B: Timer Boundary Event
A blocking activity (message catch that never receives a message) has a 5s boundary timer. The timer fires and interrupts, taking the timeout path.

## Prerequisites
- Aspire stack running

## Steps — Scenario A (timer-intermediate-catch.bpmn)

### 1. Deploy and start
- Import `timer-intermediate-catch.bpmn`, deploy, start `timer-catch-test`

### 2. Observe waiting state
- Open Instance Viewer immediately — instance should be **Running**
- `waitTimer` should be an active activity

### 3. Wait ~5 seconds and refresh
- [ ] Instance status: **Completed**
- [ ] `waitTimer` and `afterTimer` in completed activities
- [ ] Variables tab: `timerFired` = **true**

## Steps — Scenario B (timer-boundary.bpmn)

> **KNOWN BUG:** Boundary events on IntermediateCatchEvents don't register subscriptions. The timer boundary will not fire. See `docs/plans/2026-02-25-manual-test-results.md`.

### 1. Deploy and start
- Import `timer-boundary.bpmn`, deploy, start `timer-boundary-test`

### 2. Observe waiting state
- Instance should be **Running** with `blockingWait` active

### 3. Wait ~5 seconds and refresh (do NOT send the message)
- [ ] Instance status: **Completed**
- [ ] `timeoutPath` in completed activities (boundary timer fired)
- [ ] `normalEnd` NOT in completed activities (message path was interrupted)
- [ ] Variables tab: `timedOut` = **true**
