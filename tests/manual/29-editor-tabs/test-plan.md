# 29 — Editor Tabs

**Feature:** #352 — multi-tab BPMN editor.

**Scope:** The `/editor` page now hosts a tab bar between its toolbar and the
BPMN canvas. Users can have up to 10 open documents at once, switch between
them, receive dirty-tracking feedback, confirm-close tabs with unsaved changes,
and have tabs persisted across browser refreshes via `localStorage`.

## Prerequisites

- Aspire stack running: `dotnet run --project Fleans.Aspire` (from `src/Fleans/`).
- Chrome / Chromium (the test relies on `localStorage`).
- Web UI reachable at `https://localhost:7140`.
- A clean browser profile (or an **Application → Storage → Clear site data**
  on `https://localhost:7140`) so the prior run's tabs don't bleed in.

## Two sample BPMN fixtures

For multi-tab scenarios you need at least two distinct workflows. Reuse the
existing fixtures under `tests/manual/`:

- `tests/manual/01-basic-workflow/simple-workflow.bpmn`
- `tests/manual/02-script-tasks/` (any `.bpmn` in that folder)

Deploy both via the Editor **Import BPMN → Deploy** flow before running the
scenarios that assume they are already deployed.

## Scenarios

### 1. Open two tabs from the Workflows list

1. Deploy both fixtures (above). The Workflows list at `/workflows` should show
   both process keys.
2. Click the first process row → Editor opens with one tab labeled by the
   process key, showing its diagram.
3. Click **Back** to return to `/workflows`, then click the second process row.
4. **Expected:** Editor now shows **two** tabs, with the second active and
   showing the second workflow's diagram. Switching between the tabs swaps the
   canvas content accordingly. Each tab's process-ID badge reflects its own
   process key.

### 2. Dirty indicator + confirm-close

1. Starting from scenario 1 (two tabs open).
2. On the active tab, drag any element slightly. A filled dot `●` should appear
   next to the tab's label within a render cycle.
3. Click the tab's `×` close button.
4. **Expected:** a dialog appears titled *"Discard unsaved changes?"*. Click
   **Cancel** — the dialog closes, the tab stays open, still dirty.
5. Click `×` again and this time click **Discard**. The tab closes; the other
   tab becomes active with its own diagram intact.

### 3. New Diagram + process-ID edit updates tab label

1. Click **New Diagram** on the toolbar.
2. **Expected:** a new tab appears with label `Untitled-N` and a blank canvas
   (single Start event).
3. Click the process-ID span on the toolbar, enter `my_new_process`, press Enter.
4. **Expected:** the active tab's label updates to `my_new_process`.

### 4. Ten-tab cap

1. From a clean editor, open 10 distinct diagrams (mix of **New Diagram** and
   **Import BPMN** is fine).
2. Click **New Diagram** again (attempting an 11th).
3. **Expected:** a red error bar appears with text
   *"Tab limit reached (10). Close a tab to open another."* — no new tab is
   created. Close any tab, then retry **New Diagram** → succeeds.

### 5. `localStorage` persistence across refresh

1. Open 3 tabs (any mix of import / new / workflows-list sources). Make small
   edits so at least one tab is dirty.
2. Switch to a specific tab so it's the active one.
3. Refresh the browser (`Cmd+R` / `F5`) and **Cancel** the browser's
   "unsaved changes" prompt if it appears — **this verifies the warning fires**.
4. Refresh again and **Confirm** this time.
5. **Expected:** all 3 tabs come back after reload. The previously-active
   tab is still active. The dirty dot on the dirty tab is preserved.

### 6. Deploy clears the dirty flag

1. Open any fixture and make an edit — dirty dot appears.
2. Click **Deploy** → **Deploy**.
3. **Expected:** deploy succeeds, the tab's dirty dot disappears, the tab label
   remains the process key, and a `vN` version badge reflects the new version.

## Reporting

For each numbered scenario report `PASSED`, `FAILED`, `BUG` (new regression —
file an issue), or `KNOWN BUG` (matches a `> **KNOWN BUG:** …` note in this
plan; none at time of writing).
