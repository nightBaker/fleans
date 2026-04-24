# Manual Test Plan: Compensation Events

## Scenario

A workflow reserves a hotel and books a flight (both are compensable activities), then immediately triggers a **broadcast compensation throw**. The compensation handlers should run in **reverse completion order** — cancel the flight first, then cancel the hotel — and the workflow should end cleanly.

This plan covers:
- `CompensationBoundaryEvent` (non-interrupting, with handler association)
- `CompensationIntermediateThrowEvent` (broadcast — no `activityRef`)
- Compensation log recording on activity completion
- Reverse-order handler execution
- Variable mutation by compensation handlers

## Prerequisites

- Aspire stack running: `dotnet run --project Fleans.Aspire` (from `src/Fleans/`)
- Clean dev DB (delete the SQLite file or set a fresh `FLEANS_SQLITE_CONNECTION`)
- Web UI reachable at `https://localhost:7140`

## BPMN Fixtures

| File | Description |
|------|-------------|
| `compensation-broadcast.bpmn` | Broadcast compensation: reserve_hotel → book_flight → compensate_all → end |

## Steps

### Step 1 — Deploy the BPMN

1. Open the Web UI at `https://localhost:7140`.
2. Navigate to **Process Definitions** → **Upload**.
3. Upload `compensation-broadcast.bpmn`.
4. Confirm the process `compensation-broadcast-process` appears in the definitions list with status **Active**.

- [ ] Definition uploaded successfully
- [ ] Status is Active

---

### Step 2 — Start a workflow instance

```bash
curl -k -X POST https://localhost:7140/Workflow/start \
  -H "Content-Type: application/json" \
  -d '{"WorkflowId":"compensation-broadcast-process"}'
```

Note the returned `workflowInstanceId`.

- [ ] Instance created, `workflowInstanceId` returned

---

### Step 3 — Wait for automatic completion

The workflow contains only script tasks and automatic events — no user tasks or external messages. The engine should:

1. Execute `reserve_hotel` → sets `hotelStatus = "reserved"`
2. Execute `book_flight` → sets `flightStatus = "booked"`
3. Record both activities in the compensation log
4. Trigger `compensate_all` (broadcast throw)
5. Run `cancel_flight` handler (most recent completion first) → sets `flightStatus = "cancelled"`
6. Run `cancel_hotel` handler → sets `hotelStatus = "cancelled"`
7. Resume after the throw event and proceed to `end`
8. Workflow completes

Wait 3–5 seconds, then check the instance in the Web UI.

- [ ] Workflow status is **Completed** (not Running or Faulted)

---

### Step 4 — Verify compensation variables via API

```bash
# Replace <instanceId> with the actual workflow instance ID
curl -k https://localhost:7140/Workflow/<instanceId>/variables
```

Expected response contains:

```json
{
  "hotelStatus": "cancelled",
  "flightStatus": "cancelled"
}
```

- [ ] `hotelStatus` is `"cancelled"` (set by `cancel_hotel` compensation handler)
- [ ] `flightStatus` is `"cancelled"` (set by `cancel_flight` compensation handler)

---

### Step 5 — Verify completed activities in the Web UI

Open the instance detail view in the Web UI. In the activity history / completed activities list:

- [ ] `reserve_hotel` appears as **Completed**
- [ ] `book_flight` appears as **Completed**
- [ ] `cancel_hotel` appears as **Completed** (compensation handler)
- [ ] `cancel_flight` appears as **Completed** (compensation handler)
- [ ] `compensate_all` appears as **Completed**
- [ ] `end` appears as **Completed**
- [ ] No activities in **Failed** or **Running** state

---

## Expected Outcomes Checklist

- [ ] Workflow reaches Completed status without errors
- [ ] `hotelStatus = "cancelled"` (compensation handler ran)
- [ ] `flightStatus = "cancelled"` (compensation handler ran)
- [ ] Both compensation handlers executed (6 total completed activities including start/end)
- [ ] No orphaned Running activities remain

## Notes

- Compensation handlers (`cancel_hotel`, `cancel_flight`) are **not on the main sequence flow** — they are connected only via `<association>` from the boundary event. They should not appear as regular activities in the flow but should appear in the completed activities list after compensation.
- The compensation walk runs in **reverse completion order**: `book_flight` was completed after `reserve_hotel`, so `cancel_flight` runs before `cancel_hotel`.
