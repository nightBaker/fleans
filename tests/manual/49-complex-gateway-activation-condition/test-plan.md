# Test Plan — #513: Complex Gateway Activation Condition Editor

## Prerequisite

Aspire stack running: `dotnet run --project Fleans.Aspire` (from `src/Fleans/`).
Navigate to `http://localhost:5104/editor`.

## Fixtures

Reuse existing fixture (no new BPMN required):
- `tests/manual/20-complex-gateway/join-activation-condition.bpmn` — has `<bpmn:activationCondition>` with body `_context.activeBranches &gt;= 2`

## Steps

### 1. Import fixture and verify pre-population

- [ ] Import `20-complex-gateway/join-activation-condition.bpmn`.
- [ ] Click the join Complex Gateway (`join`).
- [ ] **Verify**: panel shows an "Activation Condition" text area.
- [ ] **Verify**: text area is pre-populated with `_context.activeBranches >= 2` (or the decoded equivalent of the XML `&gt;=`).

### 2. Edit the activation condition

- [ ] Clear the text area and type a new expression, e.g. `_context.activeBranches >= 3`.
- [ ] Blur (click outside or press Tab).
- [ ] Inspect the XML (Save → view file, or use browser devtools on the BPMN model).
- [ ] **Verify**: `<bpmn:activationCondition>` body updates to the new expression.

### 3. Clear the activation condition

- [ ] Clear the text area entirely and blur.
- [ ] **Verify**: the `<bpmn:activationCondition>` element is removed from the gateway XML.

### 4. New Complex Gateway — empty field

- [ ] Place a new `bpmn:ComplexGateway` on the canvas.
- [ ] Click it.
- [ ] **Verify**: "Activation Condition" text area appears and is empty.

### 5. Other gateway types have no Activation Condition field

- [ ] Click an `bpmn:ExclusiveGateway` on the canvas.
- [ ] **Verify**: no "Activation Condition" field appears.
- [ ] Click an `bpmn:InclusiveGateway`.
- [ ] **Verify**: same — no "Activation Condition" field.
