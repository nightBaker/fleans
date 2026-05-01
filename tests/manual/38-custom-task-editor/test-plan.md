# Manual Test Plan #38 — Custom Task Editor (UI)

Verifies the management UI BPMN editor's typed parameter editor for custom-task plugins: catalog page, plugin selector, typed widgets, default seeding, replace-confirmation, and zeebe-moddle round-trip.

## Prerequisites
- Aspire stack running: `dotnet run --project Fleans.Aspire` (from `src/Fleans/`).
- Clean dev DB.
- At least one custom-task plugin registered. If `Fleans.Plugins.RestCaller` is wired into the API host, that satisfies it; otherwise add a stub plugin via `services.AddCustomTaskPlugin<StubHandler>("stub-task", "Stub", schema)` to the API host before starting.
- Web UI reachable at `https://localhost:7124`.

## Scenario 1 — Admin catalog page

1. Navigate to `https://localhost:7124/admin/custom-tasks`.
2. **Verify** at least one row is displayed showing the registered plugin's task type, display name, hosting silo names, and parameter count.
3. Wait 6 seconds. The page should auto-refresh; no page-jump or loss of scroll position.
4. Stop the Worker silo (or kill its process). Within ~30 seconds the row should drop to "No silos" (catalog reconcile pruned the silo).

## Scenario 2 — Plugin selector on a fresh ServiceTask

1. Navigate to `/editor`. Drag a Service Task onto the canvas.
2. Click the new Service Task element to select it; the properties panel opens.
3. **Verify** a "Plugin (custom task)" dropdown appears at the bottom of the panel, listing the registered plugins.
4. Select a plugin from the dropdown.
5. **Verify** the typed parameter widgets appear under the dropdown, one row per `CustomTaskParameterSpec` in the schema.

## Scenario 3 — Default value seeding

1. From Scenario 2, after selecting a plugin with at least one `DefaultValue`-bearing parameter (e.g. REST caller's `method=GET` or `timeoutSec=30`):
2. **Verify** the widget displays the default value pre-filled.
3. Save the BPMN (Editor → Save). Open the saved XML.
4. **Verify** the XML contains `<bpmn:extensionElements><zeebe:taskDefinition type="..."/><zeebe:ioMapping><zeebe:input source="<DefaultValue>" target="<Name>"/>...</zeebe:ioMapping></bpmn:extensionElements>` — defaults seeded as `<zeebe:input>` rows.

## Scenario 4 — Editing a typed widget

1. From Scenario 3's state, edit the `url` field (`String` type) to `https://api.example.com/x`.
2. Wait ~500 ms (debounce + flush).
3. Click another element on the canvas, then click back. Open the saved BPMN XML.
4. **Verify** `<zeebe:input source="https://api.example.com/x" target="url"/>` is present.
5. Repeat for `Boolean` (checkbox) and `Integer` (number field) widget types where present.

## Scenario 5 — Required-field validation

1. From Scenario 3, clear a required field (e.g. `url`).
2. **Verify** a red border + "Required" helper text appears below the field.
3. Re-enter a value; verify the error styling clears.

## Scenario 6 — Replace-plugin confirmation

1. Select an existing Service Task with one plugin set.
2. Change the dropdown to a different registered plugin.
3. **Verify** a Fluent confirmation dialog appears: "Replace plugin? Existing parameters will be cleared because targets don't match between plugins."
4. Click **Cancel**. Verify the dropdown reverts to the original value and parameters are unchanged.
5. Repeat the change; click **Replace**.
6. **Verify** all `<zeebe:input>` rows are cleared, the new plugin's defaults are seeded, and parameter widgets switch to the new schema.

## Scenario 7 — Unregistered task type

1. Edit the BPMN XML directly (via "Open XML" or external tool) to set a `<zeebe:taskDefinition type="not-real"/>` on a Service Task.
2. Re-open the file in the editor; click the element.
3. **Verify** the dropdown displays the unregistered type with a `(unregistered)` warning bar below: "Task type 'not-real' is not registered…"
4. Selecting a real plugin from the dropdown triggers the replace-confirmation flow per Scenario 6.

## Scenario 8 — No plugins registered

1. Stop all Worker silos (so the catalog is empty after reconcile).
2. Navigate to the editor; select a Service Task.
3. **Verify** the dropdown is disabled with "No plugins registered" placeholder, and an info message references `services.AddCustomTaskPlugin<T>()` below.

## Expected outcomes (checklist)

- [ ] Scenario 1 — admin page renders, auto-refreshes, drops stopped silos within 30 s.
- [ ] Scenario 2 — dropdown populated; widgets render per schema.
- [ ] Scenario 3 — defaults seeded as `<zeebe:input>` rows in saved XML.
- [ ] Scenario 4 — typed widget edits round-trip into `<zeebe:input>` source values.
- [ ] Scenario 5 — required-field red border + helper text appear/disappear correctly.
- [ ] Scenario 6 — replace-confirmation dialog blocks until Cancel/Replace; Replace clears + seeds.
- [ ] Scenario 7 — unregistered type shows warning bar; dropdown selection routes through confirmation.
- [ ] Scenario 8 — empty-state UI displayed when no plugins.

## Known limitations (v1)

- `Map`/`List` parameters render a "workflow variable name" text field (no inline editor). Authors set the variable via `POST /Workflow/start` Variables or a preceding script task. Inline literal editing is a v2 follow-up tied to a mapping-grammar extension.
- No live validation against the actual workflow scope (e.g. the editor doesn't know what variable names exist).
- No variable autocomplete in the `Expression` widget.
