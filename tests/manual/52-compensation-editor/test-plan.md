# Test Plan — #511: Compensation Event Editor (activityRef / waitForCompletion)

## Prerequisite

Aspire stack running: `dotnet run --project Fleans.Aspire` (from `src/Fleans/`).
Navigate to `http://localhost:5104/editor`.

## Fixtures

- `compensation-editor.bpmn` — contains one broadcast compensate throw, one specific-target compensate throw (`activityRef="book_hotel" waitForCompletion="true"`), and two compensation boundary events.

## Steps

### 1. Import fixture

- [ ] Import `compensation-editor.bpmn` via the editor's open/upload control.
- [ ] **Verify**: diagram loads without error ("no diagram to display" must not appear).

### 2. Compensation Boundary Event — Attached To badge

- [ ] Click the boundary event `cb_hotel` (attached to "Book Hotel").
- [ ] **Verify**: panel header shows "Compensation Boundary Event".
- [ ] **Verify**: read-only "Attached To" field shows "Book Hotel" (or `book_hotel` if unnamed).
- [ ] Click `cb_flight` (attached to "Book Flight").
- [ ] **Verify**: "Attached To" field shows "Book Flight".

### 3. Broadcast compensate throw — empty Activity Reference

- [ ] Click `compensate_all` ("Compensate All" intermediate throw event).
- [ ] **Verify**: panel header shows "Compensation Throw Event".
- [ ] **Verify**: "Activity Reference" dropdown is present and shows "Broadcast (all compensable activities)" selected.
- [ ] **Verify**: "Wait For Completion" checkbox is present and checked (default `true`).

### 4. Specific compensate throw — Activity Reference pre-populated

- [ ] Click `compensate_hotel_only` ("Compensate Hotel Only").
- [ ] **Verify**: "Activity Reference" dropdown shows "Book Hotel" selected (pre-loaded from `activityRef="book_hotel"`).
- [ ] **Verify**: "Wait For Completion" checkbox is checked (`waitForCompletion="true"`).

### 5. Edit Activity Reference

- [ ] With `compensate_hotel_only` selected, change the dropdown to "Broadcast (all compensable activities)".
- [ ] Blur / change selection.
- [ ] Open the XML (Save → inspect downloaded file, or use browser devtools on the modeler).
- [ ] **Verify**: `<compensateEventDefinition/>` has no `activityRef` attribute.
- [ ] Change the dropdown back to "Book Hotel".
- [ ] **Verify**: XML shows `<compensateEventDefinition activityRef="book_hotel"/>`.

### 6. Edit Wait For Completion

- [ ] Select `compensate_hotel_only`, uncheck "Wait For Completion".
- [ ] **Verify**: XML shows `waitForCompletion="false"` on the `compensateEventDefinition`.
- [ ] Re-check the checkbox.
- [ ] **Verify**: `waitForCompletion` reverts to `"true"` in XML (or attribute absent, per spec default).

### 7. Activity dropdown population

- [ ] Select `compensate_all` (broadcast throw).
- [ ] Open the "Activity Reference" dropdown.
- [ ] **Verify**: the dropdown lists at least "Book Hotel" and "Book Flight" (all tasks in scope).
- [ ] **Verify**: compensation handler tasks (`cancel_hotel`, `cancel_flight`) are NOT listed (they are ScriptTasks but the dropdown should list all activities including them — confirm actual behavior matches plan).

### 8. Non-compensation events unaffected

- [ ] Click the start event.
- [ ] **Verify**: no "Activity Reference" or "Wait For Completion" fields appear.
- [ ] Click a sequence flow.
- [ ] **Verify**: same — no compensation fields.
