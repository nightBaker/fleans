# 46 — Nested Transaction Sub-Process (cancel paths)

Tests the cancel-path semantics of nested `<transaction>` elements. All four scenarios use `nested-tx-cancel-paths.bpmn`. Scenario F cross-references `tests/manual/26-transaction-subprocess/nested-tx-hazard.bpmn`.

Scenarios D and E (outer cancel while inner active or mid-walk) are covered by domain tests (`NestedTxPhaseBDomainTests`, `NestedTxPhaseCDomainTests`) and excluded from the manual regression suite.

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

2. Start an instance:
   ```
   POST /Workflow/start  { "WorkflowId": "nested-tx-cancel-paths" }
   ```
   Note the `workflowInstanceId`.

### Steps

3. Wait ~1 second, then send `resume-inner` to let the inner TX complete normally:
   ```
   POST /Workflow/message  { "MessageName": "resume-inner", "CorrelationKey": "" }
   ```

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
- `activeActivityIds`: empty
- `completedActivityIds` includes: `inner-work`, `inner-end`, `inner-tx`, `outer-end`, `outer-tx`, `process-end`
- `innerDone = true` in workflow variables
- No `innerCompensated` variable (compensation did not run)
- Transaction outcomes: both inner-tx and outer-tx = `Completed`

---

## Scenario B — Inner TX cancels, outer TX commits

### Setup

Fresh instance (repeat steps 1–2 above).

### Steps

3. Send `trigger-inner-cancel` to route the inner TX to its CancelEndEvent:
   ```
   POST /Workflow/message  { "MessageName": "trigger-inner-cancel", "CorrelationKey": "" }
   ```

4. Wait ~1–2 seconds for the inner TX compensation walk to complete.

5. Send `trigger-outer-complete` to complete the outer TX normally:
   ```
   POST /Workflow/message  { "MessageName": "trigger-outer-complete", "CorrelationKey": "" }
   ```

6. Check instance state.

### Expected results

- `isCompleted: true`
- `completedActivityIds` includes: `inner-work`, `inner-cancel-end`, `inner-compensate`, `inner-tx`, `outer-end`, `outer-tx`, `process-end`
- `innerCompensated = true` (compensation handler ran)
- Transaction outcomes: inner-tx = `Cancelled`, outer-tx = `Completed`
- Cancel Boundary on outer-tx did **not** fire (outer completed normally)

---

## Scenario C — Outer TX cancels after inner TX already completed

### Setup

Fresh instance (repeat steps 1–2 above).

### Steps

3. Send `resume-inner` to let the inner TX complete normally:
   ```
   POST /Workflow/message  { "MessageName": "resume-inner", "CorrelationKey": "" }
   ```

4. Send `trigger-outer-cancel` to fire the outer TX CancelEndEvent:
   ```
   POST /Workflow/message  { "MessageName": "trigger-outer-cancel", "CorrelationKey": "" }
   ```

5. Check instance state.

### Expected results

- `isCompleted: true`
- `completedActivityIds` includes: `inner-work`, `inner-end`, `inner-tx`, `outer-cancel-end`, `cancel-recovery-end`
- Transaction outcomes: inner-tx = `Completed`, outer-tx = `Cancelled`
- Outer Cancel Boundary fired → `cancel-recovery-end` reached
- Outer Error Boundary did **not** fire

---

## Scenario F — Inner Hazard cascades to outer (cross-reference)

Uses `tests/manual/26-transaction-subprocess/nested-tx-hazard.bpmn` and its test plan at `tests/manual/26-transaction-subprocess/test-plan-hazard.md` (Test A: Scenario F).

Expected: outer TX outcome is Hazard (error code 503), outer Error Boundary fires, no compensation walk runs for the outer TX.

---

## Summary table

| Scenario | Inner outcome | Outer outcome | Outer boundary that fires |
|---|---|---|---|
| A | Completed | Completed | (none — normal exit) |
| B | Cancelled | Completed | Cancel on inner-tx only |
| C | Completed | Cancelled | Cancel on outer-tx → `cancel-recovery-end` |
| F | Hazard | Hazard | Error on outer-tx → `hazard-recovery-end` |
