# 46 — Nested Transaction Sub-Process (cancel paths)

Tests the cancel-path semantics of nested `<transaction>` elements.

**Design note:** Two separate BPMN fixtures are used. `nested-tx-normal-inner.bpmn` covers Scenarios A and C
(inner TX completes normally via a linear `start → work → end` flow; outer TX chooses its
exit via EventBasedGateway). `nested-tx-cancel-inner.bpmn`
covers Scenario B (inner TX does work then fires CancelEndEvent; cancel boundary on inner-tx
at outer-tx level routes the flow to an outer ICE — 1-level-deep ICE is supported).
Scenario F cross-references `26-transaction-subprocess/nested-tx-hazard.bpmn`.

**Correlation:** the outer catch events' messages correlate on the `txKey` variable
(`<fleans:subscription correlationKey="= txKey" />`). Intermediate message catch events
require a correlation key — an uncorrelated message only reaches message start events, and a
catch event whose message has no correlation key is failed when reached (#761). Start every
instance with a unique `txKey` and send the messages with `"CorrelationKey": "<txKey>"`.

Scenarios A and B are automated in `src/Fleans/Fleans.E2E.Tests/Specs/NestedTransactionTests.cs`.

## Universal prerequisite

Aspire stack running: `dotnet run --project src/Fleans/Fleans.Aspire` (from `src/Fleans/`). Use a clean dev DB for each scenario.

API base: `https://localhost:7140`

---

## Scenario A — Both transactions commit (happy path)

Uses `nested-tx-normal-inner.bpmn`.

### Setup

1. Deploy the workflow:
   ```
   POST /Workflow/deploy  { "BpmnXml": "<contents of nested-tx-normal-inner.bpmn>" }
   ```

2. Start an instance with a correlation key:
   ```
   POST /Workflow/start  { "WorkflowId": "nested-tx-normal-inner", "Variables": { "txKey": "nested-a-1" } }
   ```
   Note the `workflowInstanceId`.

### Steps

3. Wait ~1–2 seconds for the inner TX to complete (`inner-start → inner-work → inner-end`).

4. Send `trigger-outer-complete` to complete the outer TX normally:
   ```
   POST /Workflow/message  { "MessageName": "trigger-outer-complete", "CorrelationKey": "<txKey>" }
   ```

5. Check instance state:
   ```
   GET /Workflow/instances/{workflowInstanceId}/state
   ```

### Expected results

- `isCompleted: true`
- `completedActivityIds` includes: `inner-work`, `inner-end`, `inner-tx`, `trigger-outer-complete-catch`, `outer-end`, `outer-tx`, `process-end`
- `innerDone = true` in workflow variables
- No `innerCompensated` variable
- Transaction outcomes: inner-tx = Completed, outer-tx = Completed

---

## Scenario B — Inner TX cancels, outer TX commits

Uses `nested-tx-cancel-inner.bpmn`.

### Setup

Fresh instance with a correlation key (inner TX always cancels in this fixture):
```
POST /Workflow/start  { "WorkflowId": "nested-tx-cancel-inner", "Variables": { "txKey": "nested-b-1" } }
```

### Steps

3. Wait ~1–2 seconds. The inner TX runs `inner-work` (sets `innerDone=true`) then fires
   `inner-cancel-end` (CancelEndEvent) → inner TX runs compensation (`inner-compensate`,
   sets `innerCompensated=true`) → inner TX outcome = Cancelled → cancel boundary on
   inner-tx fires → outer TX proceeds to `trigger-outer-complete-catch`.

4. Send `trigger-outer-complete`:
   ```
   POST /Workflow/message  { "MessageName": "trigger-outer-complete", "CorrelationKey": "<txKey>" }
   ```

5. Check instance state.

### Expected results

- `isCompleted: true`
- `completedActivityIds` includes: `inner-work`, `inner-cancel-end`, `inner-compensate`, `inner-tx`, `trigger-outer-complete-catch`, `outer-end`, `outer-tx`, `process-end`
- `innerDone = true` (inner-work ran before cancel)
- `innerCompensated = true` (compensation handler ran)
- Transaction outcomes: inner-tx = Cancelled, outer-tx = Completed
- Cancel boundary on outer-tx did NOT fire

---

## Scenario C — Outer TX cancels after inner TX already completed

Uses `nested-tx-normal-inner.bpmn`.

### Setup

Fresh instance with a correlation key:
```
POST /Workflow/start  { "WorkflowId": "nested-tx-normal-inner", "Variables": { "txKey": "nested-c-1" } }
```

### Steps

3. Wait ~1–2 seconds for inner TX to complete normally.

4. Send `trigger-outer-cancel`:
   ```
   POST /Workflow/message  { "MessageName": "trigger-outer-cancel", "CorrelationKey": "<txKey>" }
   ```

5. Check instance state.

### Expected results

- `isCompleted: true`
- `completedActivityIds` includes: `inner-work`, `inner-end`, `inner-tx`, `trigger-outer-cancel-catch`, `outer-cancel-end`, `cancel-recovery-end`
- `innerDone = true`
- Transaction outcomes: inner-tx = Completed, outer-tx = Cancelled
- Outer Cancel Boundary fired → `cancel-recovery-end` reached
- Outer Error Boundary did NOT fire

---

## Scenario F — Inner Hazard cascades to outer (cross-reference)

Uses `tests/manual/26-transaction-subprocess/nested-tx-hazard.bpmn` and the test plan at `tests/manual/26-transaction-subprocess/test-plan-hazard.md` (Test A — Scenario F).

Expected: outer TX outcome is Hazard (error code 503), outer Error Boundary fires, no compensation walk runs for the outer TX.

---

## Summary table

| Scenario | BPMN fixture | Start variables | Outer message | Inner outcome | Outer outcome | Outer boundary |
|---|---|---|---|---|---|---|
| A | `nested-tx-normal-inner.bpmn` | `txKey` | `trigger-outer-complete` | Completed | Completed | (none) |
| B | `nested-tx-cancel-inner.bpmn` | `txKey` | `trigger-outer-complete` | Cancelled | Completed | Cancel on inner-tx only |
| C | `nested-tx-normal-inner.bpmn` | `txKey` | `trigger-outer-cancel` | Completed | Cancelled | Cancel on outer-tx → `cancel-recovery-end` |
| F | `nested-tx-hazard.bpmn` | — | — | Hazard | Hazard | Error on outer-tx → `hazard-recovery-end` |
