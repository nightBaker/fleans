# 73 — Orbit Admin UI and BPMN editor

**Feature:** the Orbit redesign of the Admin UI: top-bar shell, the Orbit diagram style (now the
default), the editor's left rail (palette, structure, validation), canvas tools, XML mode, inspector
empty state, status bar, and right-click context menus.

**Automated coverage (not repeated here):** `SettingsPageTests` (Orbit is the default diagram style,
the editor container gets `fl-diagram-orbit`, tasks get the `USER` / `SCRIPT` caption, archify still
applies), `EventsPageTests.NavEntry_NavigatesToEventsPage` and the Settings round-trip (top-bar
navigation), `EditorPropertiesTests` (properties panel round-trips). Everything below is
pointer/keyboard interaction or visual and stays manual.

## Prerequisites

- Aspire stack running: `dotnet run --project Fleans.Aspire` (from `src/Fleans/`).
- Web UI at the `web` resource URL from the Aspire dashboard.
- Fixture: `orbit-editor-sample.bpmn` (this folder). It deliberately contains two lint errors
  (a gateway branch without a condition, a call activity without a called process) and two warnings.

## Scenarios

### 1. Shell and themes

1. Open `/workflows`. **Expected:** top bar with the Fleans mark, `FLEANS` wordmark, `BPMN 2.0` chip,
   the segmented nav (Workflows · Editor · Custom Tasks · Events) with the current section highlighted
   in the accent colour, a sun/moon toggle and the gear link on the right. No left app bar.
2. Click the sun/moon toggle. **Expected:** the whole UI switches light ↔ dark (Orbit palettes: near-black
   navy in dark, cool white in light); Fluent grids, buttons and inputs follow. Settings → Theme Mode
   now shows the explicit mode.
3. Settings → Accent Color → *Excel*. **Expected:** nav highlight, Deploy button glow, selection rings
   and canvas accents all turn green.
4. Every page (`/workflows`, `/events`, `/admin/custom-tasks`, `/settings`, an instance page) shows the
   header as a mono uppercase eyebrow (`FLEANS · …`) above a 28px title.

### 2. Editor layout and diagram style

1. Open `/editor`, **Import BPMN** → `orbit-editor-sample.bpmn`.
2. **Expected:** left rail with *Elements* (12 tiles), *Structure* (one row per flow node, ordered left
   to right, dot coloured by kind) and *Validation* (`2 errors`, `2 warnings`); the canvas has a dotted
   grid, tasks are 12px cards with a mono caption (`SERVICE`, `SCRIPT`, `USER`, `CALL`); start event
   green, end event red, gateway amber, timer/message events violet.
3. The faulty branch flow and *Fraud check* show a dashed red outline and a `!` badge; the timer boundary
   shows an amber `?` badge.
4. **Show problems** lists 4 entries; clicking one selects and scrolls to that element.
5. Status bar: `Unsaved/Saved`, `N elements · M flows`, live `X · Y` under the pointer, `Zoom NN%`.
   The bpmn.io watermark stays visible in the bottom-right corner.
6. With nothing selected the inspector shows *Nothing selected*, the element/flow/error/warning
   counters and the keyboard list. Select an element: the inspector header shows the type caption with
   a coloured dot, the name in 22px and the id in mono accent.

### 3. Palette and canvas tools

1. Click the *User task* tile, move onto the canvas, click. **Expected:** a user task is placed there.
2. Drag the *XOR* tile onto the canvas. **Expected:** the gateway follows the pointer and drops.
3. Floating toolbar: *Pan*, *Connect*, *Lasso*, *Make space* each show the pressed state while active and
   return to *Select* afterwards; *Undo*/*Redo* are disabled when there is nothing to undo/redo.
4. Zoom bar (bottom-left): −, +, fit; the percentage follows mouse-wheel zoom too.

### 4. Context menus

1. Right-click *Manual review*. **Expected:** menu titled `USER TASK · Task_ManualReview` with Cut,
   Copy, Duplicate · Change type ›, Attach boundary event › · Add comment, Edit label, Open properties ·
   Delete (red). Shortcut chips show `⌘` on macOS, `Ctrl` elsewhere.
2. *Change type* opens the bpmn-js replace menu (searchable) next to the menu; picking *Service task*
   replaces the element.
3. Right-click empty canvas → *Add element* › *Timer event*: the timer appears at the click point.
   *Paste here* (after a Copy) pastes at the click point. *Export* › *Download .svg* / *.bpmn* downloads
   files named after the process id. *Show XML* switches to XML mode.
4. Right-click the *low* flow → *Make default flow*: the default slash moves to it; *Reverse direction* on
   the first flow (out of the start event) is disabled with a tooltip.
5. Select three tasks, right-click one → title `3 elements selected`, *Align* › *Top* aligns them.
6. Open a menu near the right or bottom window edge: it (and its submenu) opens to the left / moves up
   instead of overflowing.
7. Keyboard: select an element, press **Shift F10** → the menu opens with the first item focused;
   ↑/↓/Home/End move, → opens a submenu, ← / Esc closes it, Enter runs the item, Esc closes the menu
   and focus returns to the canvas. **Ctrl D** duplicates, **Ctrl I** focuses the first inspector field,
   **F2** edits the label, **Shift 1** fits the diagram, **Ctrl E** opens XML mode.

### 5. XML mode

1. Click **XML** in the mode switch. **Expected:** the canvas is replaced by the formatted XML with an
   *In sync with the diagram* pill.
2. Edit a task name in the XML. **Expected:** pill turns to *Edited · not applied*; New Diagram, Import,
   Deploy and the other tabs are disabled ("Apply or discard the XML changes first").
3. **Apply to diagram** (or click **Modeling**). **Expected:** back on the canvas, fitted, with the
   renamed task; the tab is marked dirty.
4. Back in XML mode, break the XML (delete a closing tag) and apply. **Expected:** stays in XML mode
   with an *Invalid XML* pill and an error bar; the diagram still shows its last good state.
5. **Discard changes** restores the diagram's XML; **Copy** puts the text on the clipboard;
   **Download XML** in XML mode downloads the text as shown.

### 6. Responsive

1. Narrow the window to ~1280px: palette 196px, inspector 300px.
2. ~1024px: the inspector floats over the canvas only while something is selected.
3. < 768px: the left rail is hidden, the inspector stacks under the canvas, the nav scrolls horizontally.
