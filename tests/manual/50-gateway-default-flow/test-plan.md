# Test Plan — #512: Gateway Default Flow Editor

## Prerequisite

Aspire stack running: `dotnet run --project Fleans.Aspire` (from `src/Fleans/`).
Navigate to `http://localhost:5104/editor`.

## Fixtures

Reuse existing fixtures (no new BPMN required):
- `tests/manual/03-exclusive-gateway/conditional-branching.bpmn` — has `<bpmn:exclusiveGateway id="gateway" default="defaultFlow">`
- `tests/manual/14-inclusive-gateway/default-flow.bpmn` — has an inclusive gateway with a default flow

## Steps

### 1. Import exclusive-gateway fixture

- [ ] Import `03-exclusive-gateway/conditional-branching.bpmn`.
- [ ] Click the exclusive gateway element.
- [ ] **Verify**: panel shows "Default Flow" dropdown.
- [ ] **Verify**: dropdown pre-selects the flow whose ID matches `default="defaultFlow"` in the XML (shows the flow ID or its name label).

### 2. Change the default flow

- [ ] With the gateway selected, change the "Default Flow" dropdown to a different outgoing sequence flow.
- [ ] **Verify**: the XML `default=` attribute on the gateway updates to the selected flow's ID.

### 3. Clear the default flow

- [ ] Change the dropdown to "(none)".
- [ ] **Verify**: the `default` attribute is removed from the gateway element in the XML.

### 4. Sequence flow panel is unaffected

- [ ] Click one of the outgoing sequence flows.
- [ ] **Verify**: sequence flow panel shows ID, Name, Condition Expression — no "Default Flow" field appears on the flow itself.

### 5. Inclusive gateway fixture

- [ ] Import `14-inclusive-gateway/default-flow.bpmn`.
- [ ] Click the inclusive gateway.
- [ ] **Verify**: "Default Flow" dropdown appears and pre-populates correctly.
- [ ] Change and clear as in steps 2–3 — same behavior expected.

### 6. Parallel gateway has no Default Flow field

- [ ] Place a `bpmn:ParallelGateway` on the canvas.
- [ ] Click it.
- [ ] **Verify**: no "Default Flow" dropdown appears (parallel gateways do not support the `default` attribute).
