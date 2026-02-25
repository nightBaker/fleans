# 05 — Event-Based Gateway

## Scenario
An event-based gateway waits for either a timer (30s) or a message. We send the message via API before the timer fires, so the message path should win.

## Prerequisites
- Aspire stack running
- Note the API port from Aspire dashboard (e.g., `http://localhost:<port>`)

## Steps

### 1. Deploy and start
- Import `timer-vs-message-race.bpmn`, deploy, start `event-based-gateway-test`
- Open Instance Viewer — should show instance as **Running** with `ebg` active

### 2. Send message via API
```bash
curl -X POST http://localhost:<port>/workflow/message \
  -H "Content-Type: application/json" \
  -d '{"messageName": "continueProcess", "correlationKey": "order-123", "variables": {}}'
```
Expected response: `{"delivered": true}`

### 3. Verify outcome (refresh Instance Viewer)
- [ ] Instance status: **Completed**
- [ ] `msgCatch` and `msgPath` appear in completed activities
- [ ] `timerCatch` and `timerPath` do **NOT** appear in completed activities
- [ ] BPMN canvas highlights the message path
- [ ] Variables tab: `orderId` = **"order-123"**
