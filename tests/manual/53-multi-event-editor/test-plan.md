# Test 50 — Multi-Event Definition Editor

**Feature:** All event definitions visible and editable in the BPMN editor panel when an element has more than one definition.

**Prerequisite:** Aspire stack running (`dotnet run --project src/Fleans/Fleans.Aspire`).

## Test fixtures

Reuse existing fixtures from `tests/manual/24-multiple-event/`:
- `message-or-signal-catch.bpmn` — IntermediateCatchEvent with Message + Signal definitions
- `multi-throw.bpmn` — IntermediateThrowEvent with two Signal definitions
- `multiple-boundary.bpmn` — BoundaryEvent with Message + Timer definitions

## Steps

1. Open the Web UI at `https://localhost:7124` → **Editor**.
2. Import `message-or-signal-catch.bpmn`. Select the multi-event catch gateway.
   - **Verify**: Properties panel shows "Definition #1 — Message" and "Definition #2 — Signal" as labeled boxes.
   - **Verify**: Message Name and Correlation Key fields are editable and pre-populated.
   - **Verify**: Signal Name field is editable and pre-populated.
3. Edit the Message Name in Definition #1. Click elsewhere.
   - **Verify**: The BPMN XML updates correctly (the message ref's name attribute changes).
4. Import `multi-throw.bpmn`. Select the element with two Signal definitions.
   - **Verify**: Panel shows "Definition #1 — Signal" and "Definition #2 — Signal".
   - **Verify**: Info bar visible: "Two definitions of the same type require hand-editing the XML."
5. Import a single-signal intermediate catch event (from any existing fixture).
   - **Verify**: Panel shows the single Signal Name field as before (no labeled boxes, no info bar).
   - **Verify**: No visual regression for single-definition elements.
