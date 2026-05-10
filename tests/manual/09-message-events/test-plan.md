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

## Scenario D: Correlation key round-trip in editor panel (#428)

Verifies that the Correlation Key field in the BPMN editor panel correctly reads from and writes to the `<zeebe:subscription>` element in message extension elements.

### Prerequisites
- Aspire stack running with editor accessible at `/editor`

### Steps

#### Part 1 — Read from existing Zeebe BPMN
1. Open the editor (`/editor`) and import `message-catch.bpmn`
2. Click the **intermediate catch event** (`waitApproval`)
3. In the properties panel, verify:
   - [ ] **Message Name** field shows `approvalReceived`
   - [ ] **Correlation Key** field shows `requestId` (the `= ` FEEL prefix must be stripped)

#### Part 2 — Edit and save round-trip
4. Change the Correlation Key field value to `newRequestId`
5. Click **Deploy** to save
6. Reload the page, re-open the editor, re-import the same diagram (or use the tab if it auto-saves)
7. Click the intermediate catch event again
8. Verify:
   - [ ] **Correlation Key** field shows `newRequestId` (not `requestId` or empty)
   - [ ] The saved XML contains `<zeebe:subscription correlationKey="newRequestId"/>` inside the `<message>` element (inspect via browser dev tools or raw XML export)

#### Part 3 — Clear correlation key
9. Clear the Correlation Key field (empty string), deploy, reload
10. Verify:
    - [ ] **Correlation Key** field is empty
    - [ ] No `<zeebe:subscription>` element in the message's `<extensionElements>`

---

## Scenario C: Missing correlation variable fails the workflow (#425)

The `<zeebe:subscription correlationKey="= requestId" />` references a variable that the workflow never sets. Building the message-correlation effect throws at build-time inside `WorkflowExecution.PerformEffects` (the `RegisterMessageCommand` branch). Per the registration-vs-cleanup asymmetry rule (CLAUDE.md "Design Constraints"), this MUST surface as a Failed activity + Failed workflow — not silently stay Running.

### 1. Deploy and start
- Import `message-catch-missing-correlation.bpmn`, deploy, start `message-catch-missing-correlation`
- Do NOT supply variables — the missing-`requestId` is the test condition

### 2. Verify outcome (no API message needed)
- [ ] `beforeWait` script task: **Completed** (it ran and set `before=true` before the catch event tried to register)
- [ ] `waitApproval` intermediate catch event: **Failed** (NOT stuck Active) with an `InvalidOperationException` carrying the missing-variable message
- [ ] `afterApproval` is **NOT** in active or completed activities (workflow short-circuited on the failure)
- [ ] Workflow instance status: **Failed**
