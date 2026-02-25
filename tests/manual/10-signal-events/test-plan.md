# 10 — Signal Events

## Scenario A: Signal Catch
A workflow waits for signal `globalAlert`. Broadcasting the signal via API unblocks it.

## Scenario B: Signal Boundary
A long timer has a boundary signal `emergencyStop`. Broadcasting the signal interrupts the timer.

## Prerequisites
- Aspire stack running
- Note the API port

## Steps — Scenario A (signal-catch-throw.bpmn)

### 1. Deploy and start
- Import `signal-catch-throw.bpmn`, deploy, start `signal-catch-test`
- Instance **Running** with `waitSignal` active

### 2. Broadcast signal via API
```bash
curl -X POST http://localhost:<port>/workflow/signal \
  -H "Content-Type: application/json" \
  -d '{"signalName": "globalAlert"}'
```
Expected: `{"deliveredCount": 1}`

### 3. Verify outcome
- [ ] Instance status: **Completed**
- [ ] `afterSignal` in completed activities
- [ ] Variables: `signalReceived` = **true**

## Steps — Scenario B (signal-boundary.bpmn)

### 1. Deploy and start
- Import `signal-boundary.bpmn`, deploy, start `signal-boundary-test`
- Instance **Running** with `longWait` active

### 2. Broadcast signal via API
```bash
curl -X POST http://localhost:<port>/workflow/signal \
  -H "Content-Type: application/json" \
  -d '{"signalName": "emergencyStop"}'
```

### 3. Verify outcome
- [ ] Instance status: **Completed**
- [ ] `emergencyPath` completed (signal boundary fired)
- [ ] `normalPath` NOT completed
- [ ] Variables: `emergency` = **true**
