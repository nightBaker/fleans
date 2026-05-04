# Manual Test Plan — Transaction Sub-Process Phase-1 Status Callout

> Verifies the docs callout shipped by #397 (PR `docs/397-transaction-phase1-callout`).
> The callout lives at a single source-of-truth anchor on the BPMN coverage page;
> two guides deep-link to that anchor. This plan exercises both the rendering
> and the cross-link integrity.

## Scenario

The Transaction Sub-Process (`<bpmn:transaction>`) has three terminal outcomes
per BPMN 2.0 §10.4.3 — Completed ✅, Cancelled ✅, Hazard ❌. Documentation
must communicate this status matrix in **one** authoritative location and have
all related guides defer to it via deep-links. After #397 ships:

- `concepts/bpmn-support.md` is the single source of truth — anchor
  `#transaction-sub-process-status` carries a `:::caution` admonition with the
  ✅/❌ matrix, the workaround pattern, the multi-instance constraint, and the
  nested-transaction caveat.
- `guides/call-activities-and-subprocesses.md` and
  `guides/error-handling.md` deep-link to that anchor — neither duplicates the
  status content.

## Prerequisites

- `cd website && npm install` has been run at least once.
- Dev server NOT already running on port 4321.
- Branch `docs/397-transaction-phase1-callout` (or main, post-merge) checked out.

## Steps

### 1. Build passes

```bash
cd website
npm run build
```

**Expected:** Build succeeds. No broken-link warnings or remark/rehype parse
errors. The Starlight admonition for `:::caution` parses cleanly (no MDX
fallback to a code block).

### 2. Anchor resolves on `bpmn-support.md`

```bash
cd website
npm run dev
```

Open `http://localhost:4321/fleans/concepts/bpmn-support/#transaction-sub-process-status`
in a browser.

**Expected:**
- The `:::caution` admonition is rendered with a yellow/amber Starlight
  caution-icon header reading **"Phase-1 status — Transaction Sub-Process"**.
- Three bullets visible: `✅ Completed outcome`, `✅ Cancelled outcome`,
  `❌ Hazard outcome` — emoji glyphs render (not boxes).
- The `Tracked in [#492]` link inside the Hazard bullet resolves to
  `https://github.com/nightBaker/fleans/issues/492`.
- The page-load scrolls directly to the H3 heading (`### Transaction
  Sub-Process status`) under `## Cancel Events`, and the H3 itself is
  visible above the admonition.

### 3. Both themes render the admonition

Toggle the Starlight theme switcher in the top-right (sun/moon icon).

**Expected:** Admonition background and icon contrast remain readable in
both light and dark mode. No text drops below WCAG-AA contrast against the
admonition body.

### 4. Markdown table row deep-link works

Navigate to `http://localhost:4321/fleans/concepts/bpmn-support/#sub-processes`
(or scroll to the `### Sub-Processes` table). Find the row whose Element
column reads `Transaction Sub-Process`.

**Expected:**
- The Notes column reads
  *"Completed + Cancelled supported; Hazard pending. See [Transaction
  Sub-Process status](…) for the supported/unsupported matrix, workaround
  pattern, and the new Hazard tracker."*
- Clicking the **"Transaction Sub-Process status"** link inside the table cell
  scrolls the same page to `#transaction-sub-process-status`. No 404. The
  scroll target is the H3 we visited in step 2.

### 5. Notes-section cross-link works

Scroll to the `### Notes that span multiple rows` section (just below the
Sub-Processes table). Find the bullet beginning *"Transaction Sub-Process
terminal outcomes (Completed / Cancelled / Hazard)"*.

**Expected:**
- The bullet text reads
  *"… see the [Transaction Sub-Process status](#transaction-sub-process-status)
  callout under Cancel Events for the supported/unsupported matrix and
  workaround pattern."*
- Clicking the link scrolls to the same H3.

### 6. Call-Activities guide deep-link resolves

Navigate to
`http://localhost:4321/fleans/guides/call-activities-and-subprocesses/`
and scroll to the `## Transaction sub-process` H2.

**Expected:**
- The paragraph immediately below the cancel-flow cross-link reads:
  *"For the full phase-1 status (Completed ✅ / Cancelled ✅ / Hazard ❌)
  plus the recommended workaround for Hazard-style cleanup, see the
  [Transaction Sub-Process status](…) callout on the BPMN coverage page."*
- Clicking that link navigates to
  `http://localhost:4321/fleans/concepts/bpmn-support/#transaction-sub-process-status`
  — page changes, anchor scrolls to the H3.

### 7. Limitations bullet deep-link resolves

Same page, scroll to the `## Limitations` (or equivalent) section near the
bottom.

**Expected:**
- The Transaction-Hazard bullet reads
  *"**Transaction Hazard path** — see the [Transaction Sub-Process status](…)
  callout on the BPMN coverage page for the supported/unsupported outcome
  matrix and the workaround pattern."*
- Clicking the link resolves to the same H3 anchor on
  `concepts/bpmn-support/`.

### 8. Error-Handling guide cross-link resolves

Navigate to
`http://localhost:4321/fleans/guides/error-handling/`. Scroll to the
*Limitations* / known-limitations bullet list near the foot of the page.

**Expected:**
- A bullet reads
  *"… See the [Transaction Sub-Process status](…) callout for current
  Hazard-path status and workaround."*
- The link navigates to
  `http://localhost:4321/fleans/concepts/bpmn-support/#transaction-sub-process-status`.

### 9. No stale `#231` references in shipped docs

```bash
cd D:/Projects/fleans
git grep -nE '#231' website/src/content/docs/
```

**Expected:** No matches. (The Hazard-path tracker has moved from #231
to #492 and the docs deep-link instead of citing the issue number.)

### 10. CLAUDE.md regression entry #26 cites #492

```bash
cd D:/Projects/fleans
grep -nE '^26\\. \\*\\*Transaction Sub-Process' CLAUDE.md
```

**Expected:** The entry text contains `pending issue #492` (not `#231`).

### 11. Test-plan #26 cites #492 and references regression #36

```bash
cd D:/Projects/fleans
grep -nE '#492|#36' tests/manual/26-transaction-subprocess/test-plan.md
```

**Expected:** Phase-1 scope note references `#492` for the Hazard path and
`#36` (`30-cancel-event/`) for the verified Cancel path. The stale `#230`
and `#231` references are gone.

## Pass criteria

- [ ] Build passes (`npm run build` exits 0, no broken-link warnings).
- [ ] Admonition renders correctly in both light and dark themes (steps 2, 3).
- [ ] All four deep-link sources (table row, notes bullet, call-activities
      paragraph, call-activities limitations bullet, error-handling bullet)
      navigate to `#transaction-sub-process-status` and scroll to the H3
      (steps 4-8).
- [ ] No stale `#231` references in `website/src/content/docs/` (step 9).
- [ ] CLAUDE.md regression entry #26 and `tests/manual/26-…/test-plan.md`
      both cite `#492` (steps 10, 11).
