# Manual Test Plan — Sidebar restructure / Patterns group (Issue #405)

Verifies that the sidebar reorganization moves 3 BPMN-pattern guides from Getting Started into a new Patterns group without breaking any URLs or cross-links.

## Prerequisites

- `cd website && npm install` has been run at least once.
- Dev server NOT already running on port 4321.

## Steps

### 1. Build passes

```bash
cd website
npm run build
```

**Expect:** zero errors. Page count = 26 (unchanged from prior build — sidebar reorg is config-only, no pages added or removed).

### 2. Sidebar config has 4 groups

```bash
grep -cE "label: '(Getting Started|Concepts|Patterns|Reference)'" website/astro.config.mjs
```

**Expect:** 4. The four sidebar groups in order: Getting Started, Concepts, Patterns, Reference.

### 3. Getting Started item count

```bash
sed -n "/label: 'Getting Started'/,/label: 'Concepts'/p" website/astro.config.mjs | grep -cE "^\s+\{ label:"
```

**Expect:** 9. (Was 12 before this PR; 3 items moved to Patterns.) The 9 remaining: Introduction, Quick Start, Service Tasks, User Tasks, Writing Custom-Task Plugins, Hosting Plugins (Custom Worker Host), BPMN Editor, Events Page, Add to Existing Project.

### 4. Patterns group has 3 items

```bash
sed -n "/label: 'Patterns'/,/label: 'Reference'/p" website/astro.config.mjs | grep -cE "^\s+\{ label:"
```

**Expect:** 3. The 3 items: Variables and Scope, Error Handling, Multi-Instance Activities.

### 5. The 3 moved pages still build to their unchanged dist URLs

```bash
ls website/dist/guides/variables-and-scope/index.html
ls website/dist/guides/error-handling/index.html
ls website/dist/guides/multi-instance-activities/index.html
```

**Expect:** all three exist. Slugs are unchanged so any existing cross-links from other docs pages continue to resolve.

### 6. Reference group still uses autogenerate

```bash
grep -cE "autogenerate: \{ directory: 'reference' \}" website/astro.config.mjs
```

**Expect:** 1. Reference is unchanged.

### 7. Sidebar renders 4 groups in browser (manual)

`cd website && npm run dev`, then visit `https://localhost:4321/fleans/`.
- The left sidebar shows 4 collapsible groups in order: **Getting Started**, **Concepts**, **Patterns**, **Reference**.
- Click into Patterns; verify it contains exactly Variables and Scope, Error Handling, Multi-Instance Activities (in that order).
- Click into Getting Started; verify Variables and Scope, Error Handling, Multi-Instance Activities are NOT present.

### 8. Both themes render (manual)

Toggle light/dark via the navbar; sidebar groups should render correctly in both themes (no theme-specific CSS introduced — sidebar reorg is purely structural).

## Verdict

- **PASSED** — all 8 steps green. Move PR to Review by Human.
- **FAILED / BUG** — file follow-up issue, send PR back to Ready.
