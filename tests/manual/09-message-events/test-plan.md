# 09 — Message Events

## Scenario A: Message Intermediate Catch
A workflow waits for an external message (correlated by `requestId`). Sending the message via API unblocks the workflow.

## Scenario B: Message Boundary Event
A long timer (60s) has a boundary message event. Sending a cancel message interrupts the timer and takes the cancel path.

## Prerequisites
- Aspire stack running
- Note the API port from Aspire dashboard

## Steps — Scenario A (message-catch.bpmn)

### 1. Deploy and start
- Import `message-catch.bpmn`, deploy, start `message-catch-test`
- Instance should be **Running** with `waitApproval` active

### 2. Send message via API
```bash
curl -X POST http://localhost:<port>/workflow/message \
  -H "Content-Type: application/json" \
  -d '{"messageName": "approvalReceived", "correlationKey": "req-456", "variables": {}}'
```
Expected: `{"delivered": true}`

### 3. Verify outcome (refresh)
- [ ] Instance status: **Completed**
- [ ] `waitApproval` and `afterApproval` in completed activities
- [ ] Variables: `requestId` = **"req-456"**

## Steps — Scenario B (message-boundary.bpmn)

> **KNOWN BUG:** Boundary events on IntermediateCatchEvents don't register subscriptions. The message boundary will not fire. See `docs/plans/2026-02-25-manual-test-results.md`.

### 1. Deploy and start
- Import `message-boundary.bpmn`, deploy, start `message-boundary-test`
- Instance should be **Running** with `longWait` active

### 2. Send cancel message via API
```bash
curl -X POST http://localhost:<port>/workflow/message \
  -H "Content-Type: application/json" \
  -d '{"messageName": "cancelRequest", "correlationKey": "req-789", "variables": {}}'
```

### 3. Verify outcome (refresh)
- [ ] Instance status: **Completed**
- [ ] `cancelPath` in completed activities (boundary message interrupted the timer)
- [ ] `normalPath` NOT in completed activities
- [ ] Variables: `cancelled` = **true**
