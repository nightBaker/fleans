# BPMN editor (`/editor`) UI invariants

## Multi-tab state

Tab state lives in `Editor.razor` (private `tabs: List<TabSession>` + `activeTabId`). Only one `bpmn-js` modeler exists at a time — switching tabs calls `bpmnEditor.getXml` on the outgoing tab and `bpmnEditor.loadXml(incoming.BpmnXml)` on the incoming.

Dirty tracking subscribes to bpmn-js `commandStack.changed` via `bpmnEditor.registerDirtyCallback` and flips the active tab's flag (cleared on deploy).

Persistence is **localStorage-only** under key `fleans.editor.tabs.v1` (versioned so future schema changes don't crash old sessions). The cap is 10 tabs; closing the last tab opens a fresh blank one so the editor is never empty.

## Custom-task properties panel: input/output asymmetry

`ElementPropertiesPanel.razor`.

`CustomTaskParameterSchema` models **inputs only** — output shapes are dynamic (HTTP response bodies, SQL query shapes, etc.) so plugin authors don't enumerate them.

Therefore the panel renders inputs in two mutually-exclusive modes:

- **typed** `CustomTaskParameterEditor` when `currentSchema is not null`,
- **raw** `Element.InputMappings` editor when `currentSchema is null`.

…but it **always renders the Output Mappings raw editor** whenever a plugin is selected. When a typed editor is showing inputs, an info bar above the output section explains the `__response` convention (plugin handlers expose their result under this reserved key).

**State invariant:** typed-editor values live in `currentParameterValues` (a `Dictionary<string,string>` separate from `Element.InputMappings`); outputs always live in `Element.OutputMappings`. The two surfaces share no state and no writer code.

## Plugin-switch confirm-dialog gate

`ElementPropertiesPanel.razor:OnPluginTypeChange`.

The gate condition is `currentParameterValues.Count > 0 || Element.OutputMappings.Count > 0`.

The JS `bpmnEditor.setServiceTaskType` only swaps the `TaskDefinition` extension entry — it does NOT touch `<zeebe:input>` / `<zeebe:output>` elements. So the gate-skipped path silently *carries* outputs across plugin switches (worse than wiping them, because plugin-A's `=__response.body`-shaped output expressions survive under plugin-B's TaskDefinition with no UI signal).

Only `bpmnEditor.clearAllIoMappings`, invoked exclusively from `ConfirmReplacePlugin` after the user confirms the dialog, wipes IO mappings.

The dialog is therefore the **only** mechanism gating "carry vs. clear" on plugin switch — **any user-visible authored state must gate the dialog or it gets silently carried** (root cause of PR #585).
