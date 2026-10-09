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

## Scenario C: Timer Start Event — `timeDate` (timer-start-date.bpmn)
Deploying a process whose start event has a `timeDate` timer creates exactly one instance at that moment — no `start` call. Automated by `TimerStartEventTests.TimerStartEvent_TimeDate_CreatesOneInstanceAtTheConfiguredMoment`.

### Steps
1. Edit `timer-start-date.bpmn`: set `<timeDate>` to an ISO-8601 UTC instant ~30 s in the future (e.g. `2026-10-09T12:00:30Z`).
2. Deploy it. Do **not** start an instance.
3. Poll `GET https://localhost:7140/Definitions/timer-start-date-test/instances` until an instance appears.

### Expected
- [ ] No instance exists before the configured instant; exactly one appears after it
- [ ] The instance is **Completed** with `timerStart`, `afterTimerStart`, `end` completed
- [ ] Variables: `startedByTimer` = **true**
- [ ] No further instances are created afterwards

## Scenario D: Timer Start Event — `timeCycle` (timer-start-cycle.bpmn)
`R2/PT5S` creates one instance every 5 seconds, twice, then stops. Automated by `TimerStartEventTests.TimerStartEvent_TimeCycle_CreatesOneInstancePerRepetition`.

### Steps
1. Deploy `timer-start-cycle.bpmn`. Do **not** start an instance.
2. Poll `GET https://localhost:7140/Definitions/timer-start-cycle-test/instances` for ~20 s.
3. Disable the definition (`POST /Definitions/disable`) when done; re-deploying a disabled key keeps it disabled — call `POST /Definitions/enable` to re-arm the timer.

### Expected
- [ ] Exactly 2 instances are created, ~5 s apart, both **Completed** with `startedByTimer` = **true**
- [ ] Sub-minute cycles work (regression: the scheduler used to pass the cycle interval as the Orleans reminder period, which Orleans rejects below 1 minute)
- [ ] Re-deploying / re-enabling the definition arms a fresh `R2` budget (regression: the fire counter used to carry over)
