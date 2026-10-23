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

## Orbit layout and chrome

`Editor.razor` renders the Orbit layout (`wwwroot/css/orbit.css`): toolbar (process crumbs + version pill, *Modeling | XML* switch, file actions) → tab bar → `[left rail | canvas or XML | inspector]` → status bar.

- **Chrome is Razor, behaviour is bpmn-js, wired by data attributes** in `bpmnEditor.bindChrome("editor-root")`: palette tiles `data-bpmn-create="<catalog key>"`, floating tools `data-bpmn-tool="<name>"`, read-outs `data-orbit-zoom` / `data-orbit-cursor`. Palette drags and tool activation need the *native* DOM event (bpmn-js starts its drag session from it), which `@onclick` can't hand to JS — so don't move these to Blazor handlers. Palette keys and the canvas menu's *Add element* list share `window.orbitElementCatalog` (`js/orbitContextMenu.js`); add an element there and as a tile in `Editor.Palette.cs`.
- **JS-owned text nodes:** the zoom/cursor spans are rendered empty and filled by JS; never give them Blazor content, or the next render fights JS.
- **The canvas div is focusable** (`tabindex="0"`). bpmn-js binds its keyboard to that element, so without focus inside it no shortcut (Delete, Ctrl+Z, …) fires. `focusElement` and closing a context menu put focus back via `keyboard.getBinding()`.
- **Diagram summary → .NET:** after every `import.done` / `commandStack.changed` (debounced 200 ms) `getSummary()` pushes `OnDiagramSummary` with counts, the outline and the lint problems. The payload is capped (120 outline rows, 60 problems) to stay under the SignalR receive limit (32 KB default) — keep any new field small.
- **Lint is a modelling hint, not engine validation** (`_lint` in `bpmnEditor.js`): errors = can't run as drawn (no start event, unconditioned non-default gateway branch, timer/message/signal without definition, call activity without process key), warnings = probably wrong (unreachable step, dead end, empty script). Deploy is *not* blocked by it — the real check is still `BpmnConverter` at deploy time; the deploy dialog only reports the counts. Problems get `orbit-problem-<severity>` markers and an `orbit-problem` overlay badge.

## XML mode

The XML text never round-trips through .NET: `enterXmlMode` fills the `<textarea id="editor-xml">` (rendered **without** a bound value) from `saveXML`, an input listener reports only *edited / not edited* (`OnXmlEdited`), and `applyXmlFromEditor` imports it. Reason: a diagram's XML can exceed the SignalR message limit.

- The canvas is `display:none` in XML mode, and `fit-viewport` on a 0×0 container corrupts the viewbox. So leaving XML mode is two-phase: `ShowModel` flips the flag, `OnAfterRenderAsync` (`CompleteXmlModeExitAsync`) then calls `refreshCanvas` and, if edited, `applyXmlFromEditor`. `loadXml` itself defers the fit (`_fitPending`) when the container has no size — which is what makes tab switches from XML mode safe.
- While the text has unapplied edits, tab switching, New Diagram, Import and Deploy are disabled (`TryLeaveXmlMode`); a failed apply returns to XML mode with the parser error and leaves the diagram at its last good state.

## Context menus (`js/orbitContextMenu.js`)

A diagram-js module (`window.orbitContextMenuModule`, added to the modeler's `additionalModules`) with three menus: element, canvas (root) and connection. Rules:

- **Reuse the stock popup menus for "all elements".** *Change type* opens bpmn-js' `bpmn-replace` popup, *Attach boundary event* the `boundary-event` provider from `boundaryEventContextPad.js` — don't hand-maintain a type list.
- **Connection hit-testing:** a hovered connection is covered by its bendpoint/segment handles, which sit outside its graphics, so diagram-js reports the root as the `element.contextmenu` target. `_resolveTarget` maps the handle back through its `data-element-id`; keep it when touching the handler.
- **Shortcuts** are a keyboard listener at priority 1500 (above the stock 1000 bindings, and returning `true` suppresses the browser default, e.g. Ctrl+D bookmarking). `C` stays bpmn-js' global-connect key — the menu's *Add comment* has no shortcut on purpose.
- Menus append to `document.body` (`position: fixed`), take their colours from the `--o-*` tokens on `body`, flip at the window edge, and support ↑/↓/Home/End, →/← for the one submenu level, Enter, Esc and Shift+F10 / the ContextMenu key.
- Actions that change the selected element outside the panel fire `orbit.elementChanged` so `bpmnEditor` re-sends the element data; *Open properties* / *Set condition…* fire `orbit.openProperties` and focus `[data-field="<field>"]` in `.properties-panel` (the condition text area carries `data-field="condition"`).

## Diagram themes (editor + instance viewer)

Three themes, picked in **Settings → Diagram Style** and stored in `localStorage` (`fleans.diagramTheme`): `orbit` (default), `classic` and `archify`. `wwwroot/js/bpmnTheme.js` owns the choice; `bpmnEditor.js` / `bpmnViewer.js` build their bpmn-js instance through `fleansBpmnTheme.create(...)`, which tags the bpmn-js container with `fl-diagram-<theme>`. The theme is read at creation, so a change applies to the next diagram opened. Blocked storage falls back to `orbit` (`get`) and reports `false` (`set`) — never throws into editor/viewer init.

- **Orbit** ("Diagram style: orbit" block in `css/orbit.css`): dotted canvas, Space Grotesk labels, 12px task cards with a mono kind caption (`USER`, `SERVICE`, `SCRIPT`, `CALL`, …), kind colours from the page's `--o-*` tokens (start green, end red, gateway amber, timer/message events violet, error events red). Same mechanism as archify: renderer defaults are `var(--o-node-fill)` / `var(--o-node-stroke)`, and the `orbitKinds` module tags each shape with an `o-kind-*` class (`start`, `end`, `event`, `gateway`, `task`, `container`, `pool`, `annotation`, `data`) on top of archify's `fl-cat-*` categories.
  - **The caption renderer draws on top of the stock bpmn-js renderer** (`OrbitRenderer`): a `render.shape` listener at priority 1500 calls `bpmnRenderer.drawShape` and appends the caption, so every BPMN element keeps its standard notation. `BaseRenderer` isn't exported by the UMD bundle — this is the hook it would register. Returning the visual stops the stock listener (priority 1000) from drawing a second copy.
  - The editor hides bpmn-js' own palette (`.editor-canvas .djs-palette`) for every theme — the left rail replaces it. The bpmn.io watermark (bottom-right) must stay visible; the zoom bar sits bottom-left for that reason.
  - Viewer run states (`bpmn-completed` / `-active` / `-error` / `-cancelled` / `-selected`) are restyled under `.fl-diagram-orbit`: done green, active accent glow, failed red, cancelled muted, picked accent ring; execution-path lines animate (disabled under `prefers-reduced-motion`).

- **Classic** = plain bpmn-js + the "BPMN Theme Adaptation" block in `app.css` (`!important` overrides with Fluent tokens), every selector under `:where(.fl-diagram-classic)`. `:where` keeps the original specificity so the viewer's execution highlights (`.bpmn-active …` etc.) still win — don't drop it, and put new classic-only rules under the same prefix.
- **Archify** ("BPMN diagram theme: archify" block in `app.css`, under `.fl-diagram-archify`): canvas with a 40px line grid (slate in dark mode, near-white in light), monospace labels, tinted nodes by category.
  - **Colours are CSS variables, not `!important` overrides.** The `bpmnRenderer` defaults are `var(--fl-node-fill)` / `var(--fl-node-stroke)` / `var(--fl-text)`; bpmn-js writes them as inline styles, so each shape resolves them from its own `<g>`. Light/dark follow Fluent's `body[data-theme]` live, and DI colours (`bioc:` / `color:`) authored in other modelers still win. `create()` merges the theme's renderer config per key, so caller-supplied `bpmnRenderer` / `textRenderer` options still apply.
  - **Category = `fl-cat-*` class** added by the `fleansCategoryMarkers` module on `shape.added` / `shape.changed`: `human` (user/manual), `compute` (script/service/business-rule/plain task), `process` (subprocess/call activity/transaction), `timer`, `message` (message/signal, send/receive), `error` (error/escalation/compensate/cancel/terminate), `neutral` (everything else). Each class only sets `--fl-cat`; stroke and an opaque fill tint (`color-mix` into `--fl-bg`, so boundary events don't show the host edge through them) derive from it in one rule. A new BPMN element type falls into `neutral` until `categoryOf` maps it.
  - **Run-state colours never reuse a category colour** (`--fl-state-active` blue; failed = red stroke + strong red fill; cancelled = muted dashes; selected = text colour) — otherwise an active script task or a failed error event looks unchanged.
  - **Never force `stroke-width` on shapes** — the thick end-event / call-activity borders are BPMN semantics. The 6px corner override applies to tasks only; containers keep bpmn-js geometry (a transaction's inset inner border).
