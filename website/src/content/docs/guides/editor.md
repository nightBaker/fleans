---
title: BPMN Editor
description: Multi-tab BPMN editor in the Admin UI — palette, structure, validation hints, context menus, XML mode, and deploy.
---

The Admin UI ships with an embedded BPMN editor (powered by [bpmn-js](https://bpmn.io/toolkit/bpmn-js/))
that lets you open several diagrams at once, switch between them without losing work,
and deploy any open diagram as a new process version.

## Layout

| Area | What it does |
|---|---|
| **Toolbar** | Process ID (click to rename) with a version pill — `v3` when the tab matches a deployed version, `v3 · draft` once edited, `draft` before the first deploy. The **Modeling / XML** switch, then New Diagram, Import BPMN, Download XML and Deploy. |
| **Elements** (left) | Start, end, user/service/script task, call activity, exclusive and parallel gateway, timer and message event, sub-process, pool, data object and comment. Click a tile and then the canvas, or drag the tile onto the canvas. Every other BPMN element is one *Change type* away (right-click menu). Pools and data objects are drawn for documentation: the engine runs the first pool's process and ignores data objects. |
| **Structure** (left) | Every flow node, left to right, indented inside sub-processes. Click a row to select and scroll to it. |
| **Validation** (left) | Modelling hints counted as errors and warnings; **Show problems** lists them, and the elements get a `!` / `?` badge on the canvas. |
| **Canvas tools** | Select, pan, connect, lasso, make space, undo/redo; a zoom bar (−, %, +, fit) bottom-left and a minimap bottom-right (click or drag it to move the view). |
| **Inspector** (right) | The selected element's properties. With nothing selected: element/flow/problem counts and the keyboard shortcuts. |
| **Status bar** | *Draft · kept in this browser* (changed since opened or deployed), *In sync with vN*, element and flow counts, pointer coordinates, zoom. |

An empty diagram shows an *Empty process* card with **Add start event** and **Import .bpmn**. If the
connection to the server drops, a *No connection to the server* dialog shows the retry countdown; your
open tabs are kept in the browser. On screens narrower than 768px the editor is view-only (browse,
simulate, compare); edit on a wider screen.

The validation hints catch common modelling mistakes while you draw — a process without a start event, a
gateway branch with no condition that isn't the default flow, a timer/message/signal without a definition,
a call activity without a process key (errors); unreachable steps, dead ends, empty scripts (warnings).
They don't block a deploy: the engine still validates the BPMN when you click **Deploy**, and the deploy
dialog shows the hint counts so you can double-check first.

## Context menus and shortcuts

Right-click anything on the canvas:

- **An element** — Cut, Copy, Duplicate · **Change type** (searchable list of every BPMN type the element
  can become) · **Attach boundary event** (tasks, call activities, sub-processes) · **Align** (several
  elements selected) · Add comment, Edit label, Open properties, **Simulate from here** · Delete.
- **Empty canvas** — Paste, **Paste here** (at the click point) · **Add element** at the click point ·
  Select all · Fit to screen, Zoom to 100% · **Export** as `.bpmn` or `.svg` · Show XML.
- **A sequence flow** — Set condition…, Make/unset default flow · Re-route, Edit label · Reverse direction
  (greyed out when BPMN rules forbid it, e.g. into a start event) · Delete.

The canvas is a single tab stop; once it has focus:

| Keys | Action |
|---|---|
| `Shift F10` / Menu key | Open the context menu for the selection — `↑ ↓` move, `→ ←` open/close a submenu, `Enter` runs, `Esc` closes |
| `Ctrl D` | Duplicate |
| `Ctrl X` / `Ctrl C` / `Ctrl V` | Cut / copy / paste |
| `Ctrl I` | Focus the inspector |
| `F2` | Edit the label |
| `Ctrl E` | XML mode |
| `Shift 1` | Fit to screen |
| `H` / `L` / `S` / `C` | Pan / lasso / make space / connect (bpmn-js defaults) |

On macOS use `⌘` for `Ctrl`.

## Simulation

**Simulation** (mode switch, or *Simulate from here* in an element's right-click menu) does a dry run of
the open diagram in your browser: a token walks the sequence flows, finished steps turn green, the flows
it took animate, and steps waiting for input glow amber. Nothing is deployed and scripts and conditions
are not evaluated, so where the process branches or waits, **you** decide in the right-hand panel: pick a
gateway branch, complete a user task, trigger a catch event, or fire a boundary event. Parallel gateways
wait for all their branches; expanded sub-processes run their contents. Play / pause, step, restart and
1× / 2× / 5× speed are in the bar above the canvas; the log lists every step. The diagram is read-only
while simulating — **Stop** or **Modeling** returns to editing.

## History

**History** lists the deployed versions of the tab's process and compares the tab with one of them
(by default the version the tab was opened from). Added elements get a green `+`, changed ones an amber
`~`, and the panel lists every change with old → new values (name, condition, script, timer, message,
called process, default flow, …); removed elements are listed too. **Open** opens a version in a new tab.
The diagram is read-only in this mode.

## XML mode

**XML** in the mode switch shows the diagram's BPMN XML. Edit it and click **Apply to diagram** (or switch
back to **Modeling**) to import your changes; **Discard changes** reverts the text. While the XML has
unapplied edits, switching tabs, New Diagram, Import and Deploy are disabled so nothing gets lost. If the
XML doesn't parse, the editor stays in XML mode with the parser's error and the diagram keeps its last
good state. **Download XML** in XML mode downloads the text exactly as shown.

## Why tabs?

Most real workflows are built as a family of processes — a parent plus a handful of
called children, or a set of variants that share conventions. Jumping between them by
opening and closing the single-diagram editor page forced constant context switches
and made accidentally losing in-progress edits easy.

Tabs solve that: each diagram you open stays in a tab until you explicitly close it,
unsaved changes are flagged, and the browser warns you if you try to leave with dirty
tabs. Tabs also survive a page refresh — they are persisted to the browser's
`localStorage` (key `fleans.editor.tabs.v1`).

## How to use it

<figure class="arch-diagram" style="--arch-ratio: 580 / 718; max-width: 620px; margin-inline: auto">
  <iframe data-arch-src="/fleans/diagrams/editor-deploy.html" src="/fleans/diagrams/editor-deploy.html?embed=1" title="BPMN editor deploy sequence" loading="lazy"></iframe>
  <figcaption>Open a diagram in a tab, edit it with tabs saved to localStorage, then deploy it to create a new process version and clear the tab's dirty flag. <a href="/fleans/diagrams/editor-deploy.html" target="_blank" rel="noopener">Open interactive diagram ↗</a></figcaption>
</figure>

### Open a diagram

You can open a diagram in a new tab three ways:

1. **New Diagram** — button in the editor toolbar, creates a blank canvas in a fresh tab.
2. **Import BPMN** — button in the editor toolbar, opens a file picker and imports
   the selected `.bpmn` / `.xml` file into a new tab.
3. **From the Workflows list** — clicking a workflow row from `/workflows` opens that
   workflow's BPMN XML in a new tab. If the same `(processKey, version)` is already
   open, the existing tab is activated instead of duplicated.

### Switch, close, and dirty indicator

- **Click a tab** to switch to it. The editor saves the outgoing tab's XML into memory,
  then loads the incoming tab's XML.
- **Click the `×`** on a tab to close it. If the tab has unsaved changes (indicated by
  the `●` dirty dot next to the label), you'll be asked to confirm before discarding.
- **The editor never shows zero tabs** — closing the last one falls back to a fresh
  blank diagram.
- **Deploying a tab** clears its dirty flag, because the deployed XML becomes the
  new "last saved" snapshot for that tab.

### Limit

You can keep up to **10 tabs** open at once. Attempting to open an 11th shows an
error bar (*"Tab limit reached (10). Close a tab to open another."*) and is a no-op.
This cap keeps browser memory use bounded — `bpmn-js` diagrams are cheap in localStorage,
but each live in-memory tab also carries its most recent XML snapshot.

### Persistence

- Tabs are saved to `localStorage` under `fleans.editor.tabs.v1` after every mutation
  (open / close / switch / dirty flip / deploy / process-ID rename).
- On reload, the editor restores every tab with its last-known XML and re-activates
  whichever tab was active. The URL parameter (`/editor/{processKey}`) still takes
  precedence — it opens that workflow on top of the restored set (or activates it
  if already open).
- Persistence is per-browser, not per-user — tabs do **not** roam across devices.
- `beforeunload` shows the browser's standard "unsaved changes" warning whenever
  any tab is dirty.

## Diagram style

**Settings → Diagram Style** switches how the editor and the instance viewer draw diagrams:

- **Orbit** (default) — a dotted canvas with rounded task cards labelled by kind (`USER`, `SERVICE`,
  `SCRIPT`, `CALL`, …): start events green, end events red, gateways amber, timer and message events
  violet, error events red. It follows the Admin UI's light/dark setting and accent colour. In the
  instance viewer, completed steps turn green, the active step glows in the accent colour, failed steps
  are red and the execution path animates.
- **Classic** — the standard bpmn-js look, tinted to match the Admin UI theme.
- **Archify** — a gridded canvas, monospace labels, and nodes coloured by kind. It follows the
  Admin UI's light/dark setting: dark slate in dark mode, near-white in light mode.

The choice is stored in your browser and applies the next time a diagram is opened.
It only affects how diagrams are displayed and is never written to the BPMN XML.

In the Archify style, node colour shows what kind of step it is:

| Colour | Elements |
|---|---|
| Cyan | User and manual tasks |
| Green | Script, service, business-rule and plain tasks |
| Violet | Subprocesses, call activities, transactions |
| Amber | Timer events |
| Orange | Message and signal events, send/receive tasks |
| Red | Error, escalation, compensation, cancel and terminate events |
| Grey | Gateways, plain start/end events, conditional and link events, data and annotations |

Run state in the instance viewer uses colours outside this table: blue for the active step, a
solid red fill for a failed one, grey dashes for a cancelled one.

Colours set on an element by another modeler (for example Camunda Modeler's colour picker) are kept
and override the scheme.

## Best practice

- Open a **parent CallActivity process and its child in separate tabs** before
  wiring `input`/`output` mappings. Swapping between tabs lets you verify both
  sides of the contract without losing either diagram.
- **Deploy before closing.** The dirty dot makes an unsaved tab obvious, but it's
  easier to never produce one — deploy a stable version as soon as you're happy
  with edits, then keep iterating.
- **Name your process ID first.** The tab label follows the process ID, so a
  meaningful ID gives you a recognizable tab without the auto-generated
  `Untitled-N` label.

## Known limitations

- bpmn-js `importXML` rebuilds the diagram-js graph each time you switch tabs.
  For moderately sized diagrams (~100 elements) the swap is imperceptible; very
  large diagrams may have a perceptible hitch. Pre-warming is out of scope for
  this release.
- Opening a **CallActivity's called process** in a new tab via double-click inside
  the editor is not yet wired up — for now, navigate back to `/workflows` and
  open the child from there. Tracked as a follow-up.
