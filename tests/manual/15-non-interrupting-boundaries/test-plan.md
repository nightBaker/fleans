# 15 — Non-Interrupting Boundary Events

## Scenario
Non-interrupting boundary events fire without cancelling the attached activity. The attached activity continues running while the boundary spawns a parallel branch. Timer cycle boundaries fire multiple times.

## Prerequisites
- Aspire stack running (`dotnet run --project Fleans.Aspire`)

---

## Test 1: Non-Interrupting Timer Boundary

### Deploy
- Import `non-interrupting-timer.bpmn`, deploy

### Steps
1. Start `ni-timer-boundary-test`, open Instance Viewer
2. Verify `longTask` is active (waiting for external completion)
3. Wait ~5 seconds for timer to fire

### Expected
- [ ] `longTask` still active after timer fires (NOT cancelled)
- [ ] `sendReminder` executed on the boundary path
- [ ] `reminderEnd` reached
- [ ] Variable `reminderSent` = **true**
- [ ] BPMN canvas shows dashed boundary event circle (non-interrupting visual)

### Notes
- `longTask` uses `<task>` (TaskActivity) — it waits for external `CompleteActivity` call
- No API endpoint for external task completion yet, so `longTask` remains active indefinitely

---

## Test 2: Non-Interrupting Message Boundary

### Deploy
- Import `non-interrupting-message.bpmn`, deploy

### Steps
1. Start `ni-message-boundary-test`, open Instance Viewer
2. Verify `longTask` is active
3. Send message via API:
   ```
   POST https://localhost:{PORT}/Workflow/message
   {"MessageName":"reminderMessage", "CorrelationKey":"order-123", "Variables":{}}
   ```

### Expected
- [ ] `longTask` still active after message arrives (NOT cancelled)
- [ ] `handleMessage` executed on the boundary path
- [ ] Variable `messageReceived` = **true**
- [ ] `longTask` can still be completed normally (if CompleteActivity API existed)

---

## Test 3: Timer Cycle (R3/PT5S)

### Deploy
- Import `timer-cycle.bpmn`, deploy

### Steps
1. Start `ni-timer-cycle-test`, open Instance Viewer
2. Wait ~15 seconds (timer fires 3 times at 5s intervals)
3. Check completed activities and variables

### Expected
- [ ] `longTask` still active throughout all timer fires
- [ ] `sendReminder` executed 3 times (3 boundary path instances)
- [ ] Variable `reminderCount` = **3** (incremented each fire)
- [ ] After 3 fires, no more timer re-registrations
- [ ] Timer boundary does NOT fire a 4th time

### Notes
- Cycle format: `R3/PT5S` = repeat 3 times, every 5 seconds
- Each fire creates a new parallel branch with cloned variable scope
- The `reminderCount` variable may show different values per scope (each branch starts from the same clone)
