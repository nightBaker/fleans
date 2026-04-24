---
title: BPMN Editor
description: Multi-tab BPMN editor in the Admin UI — open, edit, and deploy multiple diagrams side-by-side.
---

The Admin UI ships with an embedded BPMN editor (powered by [bpmn-js](https://bpmn.io/toolkit/bpmn-js/))
that lets you open several diagrams at once, switch between them without losing work,
and deploy any open diagram as a new process version.

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
