# BPMN editor (`/editor`) UI invariants

## Multi-tab state

Tab state lives in `Editor.razor` (private `tabs: List<TabSession>` + `activeTabId`). Only one `bpmn-js` modeler exists at a time — switching tabs calls `bpmnEditor.getXml` on the outgoing tab and `bpmnEditor.loadXml(incoming.BpmnXml)` on the incoming.

Dirty tracking subscribes to bpmn-js `commandStack.changed` via `bpmnEditor.registerDirtyCallback` and flips the active tab's flag (cleared on deploy).

**Boot readiness.** `bpmnEditor.init` creates the modeler on the *first* interop call of `OnAfterRenderAsync`, but the boot sequence keeps going (restore tabs → `ActivateTab`/`AddBlankTab` → `bpmnEditor.loadXml` → persist). `window.bpmnEditor._modeler` existing therefore does **not** mean the editor is ready: a `loadXml` issued in that window is silently overwritten by the boot's own blank/restored diagram, and later `elementRegistry.get(id)` returns `null` (#773 — looked like registry corruption after `updateProperties`, was this race; deterministic on slower Linux CI). The root `.editor-page` div renders `data-editor-ready="true"` only after the boot sequence finishes. Anything driving `window.bpmnEditor.*` from outside Blazor (E2E `EditorPage.OpenAsync`) MUST wait on that attribute, never on `_modeler`.

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

## Diagram themes (editor + instance viewer)

Two themes, picked in **Settings → Diagram Style** and stored in `localStorage` (`fleans.diagramTheme`): `classic` (default) and `archify`. `wwwroot/js/bpmnTheme.js` owns the choice; `bpmnEditor.js` / `bpmnViewer.js` build their bpmn-js instance through `fleansBpmnTheme.create(...)`, which tags the bpmn-js container with `fl-diagram-<theme>`. The theme is read at creation, so a change applies to the next diagram opened. Blocked storage falls back to `classic` (`get`) and reports `false` (`set`) — never throws into editor/viewer init.

- **Classic** = plain bpmn-js + the "BPMN Theme Adaptation" block in `app.css` (`!important` overrides with Fluent tokens), every selector under `:where(.fl-diagram-classic)`. `:where` keeps the original specificity so the viewer's execution highlights (`.bpmn-active …` etc.) still win — don't drop it, and put new classic-only rules under the same prefix.
- **Archify** ("BPMN diagram theme: archify" block in `app.css`, under `.fl-diagram-archify`): canvas with a 40px line grid (slate in dark mode, near-white in light), monospace labels, tinted nodes by category.
  - **Colours are CSS variables, not `!important` overrides.** The `bpmnRenderer` defaults are `var(--fl-node-fill)` / `var(--fl-node-stroke)` / `var(--fl-text)`; bpmn-js writes them as inline styles, so each shape resolves them from its own `<g>`. Light/dark follow Fluent's `body[data-theme]` live, and DI colours (`bioc:` / `color:`) authored in other modelers still win. `create()` merges the theme's renderer config per key, so caller-supplied `bpmnRenderer` / `textRenderer` options still apply.
  - **Category = `fl-cat-*` class** added by the `fleansCategoryMarkers` module on `shape.added` / `shape.changed`: `human` (user/manual), `compute` (script/service/business-rule/plain task), `process` (subprocess/call activity/transaction), `timer`, `message` (message/signal, send/receive), `error` (error/escalation/compensate/cancel/terminate), `neutral` (everything else). Each class only sets `--fl-cat`; stroke and an opaque fill tint (`color-mix` into `--fl-bg`, so boundary events don't show the host edge through them) derive from it in one rule. A new BPMN element type falls into `neutral` until `categoryOf` maps it.
  - **Run-state colours never reuse a category colour** (`--fl-state-active` blue; failed = red stroke + strong red fill; cancelled = muted dashes; selected = text colour) — otherwise an active script task or a failed error event looks unchanged.
  - **Never force `stroke-width` on shapes** — the thick end-event / call-activity borders are BPMN semantics. The 6px corner override applies to tasks only; containers keep bpmn-js geometry (a transaction's inset inner border).
