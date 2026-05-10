# 46 — Nested Transaction Sub-Process (cancel paths)

Tests the cancel-path semantics of nested `<transaction>` elements.

**Design note:** The inner TX uses an ExclusiveGateway driven by the `innerShouldCancel` startup variable. This is required because the Fleans engine does not register message subscriptions for events inside a doubly-nested Transaction (EventBasedGateway inside inner-tx inside outer-tx). The outer TX uses an EventBasedGateway with two messages — this works because the gateway is only 1 level deep inside a Transaction. Scenario F cross-references `26-transaction-subprocess/nested-tx-hazard.bpmn`.

## Universal prerequisite

Aspire stack running: `dotnet run --project src/Fleans/Fleans.Aspire` (from `src/Fleans/`). Use a clean dev DB for each scenario.

API base: `https://localhost:7140`

---

## Scenario A — Both transactions commit (happy path)

### Setup

1. Deploy the workflow:
   ```
   POST /Workflow/deploy  { "BpmnXml": "<contents of nested-tx-cancel-paths.bpmn>" }
   ```

2. Start an instance (no special variables — inner TX takes normal path):
   ```
   POST /Workflow/start  { "WorkflowId": "nested-tx-cancel-paths" }
   ```
   Note the `workflowInstanceId`.

### Steps

3. Wait ~1–2 seconds for the inner TX to complete (ExclusiveGateway takes default path → `inner-work` → `inner-end`).

4. Send `trigger-outer-complete` to complete the outer TX normally:
   ```
   POST /Workflow/message  { "MessageName": "trigger-outer-complete", "CorrelationKey": "" }
   ```

5. Check instance state:
   ```
   GET /Workflow/instances/{workflowInstanceId}/state
   ```

### Expected results

- `isCompleted: true`
- `completedActivityIds` includes: `inner-fork`, `inner-work`, `inner-end`, `inner-tx`, `trigger-outer-complete-catch`, `outer-end`, `outer-tx`, `process-end`
- `innerDone = true` in workflow variables
- `innerShouldCancel` not set or false
- No `innerCompensated` variable
- Transaction outcomes: inner-tx = Completed, outer-tx = Completed

---

## Scenario B — Inner TX cancels, outer TX commits

### Setup

Fresh instance — start with `innerShouldCancel=true`:
```
POST /Workflow/start  { "WorkflowId": "nested-tx-cancel-paths", "Variables": {"innerShouldCancel": true} }
```

### Steps

3. Wait ~1–2 seconds. The inner TX ExclusiveGateway takes the cancel branch → `inner-cancel-end` (CancelEndEvent) fires → inner TX runs compensation → inner TX Cancelled → Cancel Boundary on inner-tx fires → outer TX proceeds to outer-decision gateway.

4. Send `trigger-outer-complete`:
   ```
   POST /Workflow/message  { "MessageName": "trigger-outer-complete", "CorrelationKey": "" }
   ```

5. Check instance state.

### Expected results

- `isCompleted: true`
- `completedActivityIds` includes: `inner-fork`, `inner-cancel-end`, `inner-compensate`, `inner-tx`, `trigger-outer-complete-catch`, `outer-end`, `outer-tx`, `process-end`
- `innerCompensated = true` (compensation handler ran)
- Transaction outcomes: inner-tx = Cancelled, outer-tx = Completed
- Cancel Boundary on outer-tx did NOT fire

---

## Scenario C — Outer TX cancels after inner TX already completed

### Setup

Fresh instance (default variables, no `innerShouldCancel`):
```
POST /Workflow/start  { "WorkflowId": "nested-tx-cancel-paths" }
```

### Steps

3. Wait ~1–2 seconds for inner TX to complete normally.

4. Send `trigger-outer-cancel`:
   ```
   POST /Workflow/message  { "MessageName": "trigger-outer-cancel", "CorrelationKey": "" }
   ```

5. Check instance state.

### Expected results

- `isCompleted: true`
- `completedActivityIds` includes: `inner-fork`, `inner-work`, `inner-end`, `inner-tx`, `trigger-outer-cancel-catch`, `outer-cancel-end`, `cancel-recovery-end`
- Transaction outcomes: inner-tx = Completed, outer-tx = Cancelled
- Outer Cancel Boundary fired → `cancel-recovery-end` reached
- Outer Error Boundary did NOT fire

---

## Scenario F — Inner Hazard cascades to outer (cross-reference)

Uses `tests/manual/26-transaction-subprocess/nested-tx-hazard.bpmn` and the test plan at `tests/manual/26-transaction-subprocess/test-plan-hazard.md` (Test A — Scenario F).

Expected: outer TX outcome is Hazard (error code 503), outer Error Boundary fires, no compensation walk runs for the outer TX.

---

## Summary table

| Scenario | Start variables | Outer message | Inner outcome | Outer outcome | Outer boundary |
|---|---|---|---|---|---|
| A | (none) | `trigger-outer-complete` | Completed | Completed | (none) |
| B | `innerShouldCancel=true` | `trigger-outer-complete` | Cancelled | Completed | Cancel on inner-tx only |
| C | (none) | `trigger-outer-cancel` | Completed | Cancelled | Cancel on outer-tx → `cancel-recovery-end` |
| F | (see nested-tx-hazard.bpmn) | — | Hazard | Hazard | Error on outer-tx → `hazard-recovery-end` |
