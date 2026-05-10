# 26 — Transaction Sub-Process: Hazard Path (Scenario F)

Tests the Phase C behavior: when an outer Transaction fires a CancelEndEvent but a descendant Transaction already ended in Hazard, the outer TX outcome must be Hazard (not Cancelled) and no compensation walk must run for the outer TX.

## Universal prerequisite

Aspire stack running: `dotnet run --project src/Fleans/Fleans.Aspire` (from `src/Fleans/`).

---

## Test A — Scenario F: Outer cancel aborted by inner Hazard

Uses `nested-tx-hazard.bpmn`.

### Setup

1. Deploy the workflow:
   ```
   POST /api/workflow/deploy  { "bpmnXml": "<contents of nested-tx-hazard.bpmn>" }
   ```
2. Start an instance:
   ```
   POST /api/workflow/start  { "workflowId": "nested-tx-hazard" }
   ```
   Note the `instanceId`.

### Steps

3. **Trigger the inner TX Hazard** — send a message named `trigger-inner-fail`:
   ```
   POST /api/workflow/message  { "messageName": "trigger-inner-fail", "correlationKey": "" }
   ```
   This causes the inner TX's script task to throw an error that is NOT caught by any boundary event on the inner TX (it escapes the inner TX scope → Hazard).

4. **Trigger the outer TX cancel** — send a message named `trigger-outer-cancel`:
   ```
   POST /api/workflow/message  { "messageName": "trigger-outer-cancel", "correlationKey": "" }
   ```
   This fires the outer TX's CancelEndEvent.

5. **Verify state**:
   ```
   GET /api/workflow/instances/{instanceId}/state
   ```

### Expected results

- The workflow instance is **in error state** — the outer TX ended in Hazard and the outer Error/Hazard boundary event activated.
- The error code on the workflow state is `503` (propagated from Phase C).
- No compensation activities ran (no compensating service tasks in the state history).
- The outer TX's Cancel boundary event did **not** fire.

---

## Test B — Baseline: Normal cancel (no inner Hazard)

Uses `nested-tx-hazard.bpmn`, same workflow.

### Steps

1. Start a fresh instance (step 2 above).
2. **Trigger the outer TX cancel directly** (skip triggering inner fail):
   ```
   POST /api/workflow/message  { "messageName": "trigger-outer-cancel", "correlationKey": "" }
   ```
3. **Verify state**: the workflow should route via the Cancel boundary event (NOT the Error boundary event), and the outer TX's compensation walk should run (compensating service tasks in state history).

### Expected results

- Workflow continues via the **Cancel Boundary Event** path (not the Error/Hazard boundary).
- Compensation walk runs — compensating tasks are recorded in state history.
- No error state on the workflow instance.

---

## BPMN fixture description (`nested-tx-hazard.bpmn`)

The fixture contains:
- **Outer TX** with:
  - A CancelEndEvent (triggered by `trigger-outer-cancel` message catch event)
  - A CancelBoundaryEvent (attached to outer TX)
  - An ErrorBoundaryEvent (attached to outer TX, catches error code `503`)
  - An inner TX (nested inside outer TX)
- **Inner TX** with:
  - A ScriptTask that throws when `trigger-inner-fail` message is received
  - No boundary events catching the error → error escapes inner TX → inner TX ends in Hazard
- Sequence flows routing Cancel boundary → end, Error boundary → error end
