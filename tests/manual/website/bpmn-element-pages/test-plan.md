# Manual Test Plan: BPMN Element Pages (Website #17)

## Scope

Verifies the `concepts/activities/tasks` documentation page introduced by issue #420:
- Page renders in both light and dark themes
- All 8 activity SVGs load (4 tasks × 2 themes)
- Sidebar group wired correctly
- BPMN Support matrix cross-link resolves
- Source-line drift guards

## Prerequisites

- `cd website && npm install` run at least once
- `cd website && npm run dev` running (dev server at `https://localhost:4321/fleans/`)
- `node scripts/render-bpmn.mjs` run to generate SVGs (requires `npx playwright install chromium` once)

---

## Steps

### 1 — Generate SVGs

```bash
cd website
node scripts/render-bpmn.mjs
```

Expected output (among others):
```
task-plain-light: .../public/activities/task-plain-light.svg
task-plain-dark:  .../public/activities/task-plain-dark.svg
task-script-light: ...
task-script-dark:  ...
task-user-light:  ...
task-user-dark:   ...
task-service-light: ...
task-service-dark:  ...
```

- [ ] Script exits 0 with no errors
- [ ] `public/activities/` directory created with 8 SVG files

### 2 — SVG XML validity

Open each of the 8 SVG files directly in a browser (`file:///.../public/activities/*.svg`).

- [ ] `task-plain-light.svg` — no XML parse-error banner
- [ ] `task-plain-dark.svg` — no XML parse-error banner
- [ ] `task-script-light.svg` — no XML parse-error banner
- [ ] `task-script-dark.svg` — no XML parse-error banner
- [ ] `task-user-light.svg` — no XML parse-error banner
- [ ] `task-user-dark.svg` — no XML parse-error banner
- [ ] `task-service-light.svg` — no XML parse-error banner
- [ ] `task-service-dark.svg` — no XML parse-error banner

### 3 — Hero diagram unchanged

Open `https://localhost:4321/fleans/` and confirm the hero BPMN diagram still renders in both themes.

- [ ] Hero light SVG visible on light theme
- [ ] Hero dark SVG visible on dark theme

### 4 — Tasks page renders (light theme)

Navigate to `https://localhost:4321/fleans/concepts/activities/tasks/`.

- [ ] Page loads with title "Tasks"
- [ ] All 4 variant sections visible: Plain Task, Script Task, User Task, Service Task
- [ ] Light-theme SVG renders for each variant (no broken image icon)
- [ ] Dark-theme SVG is not visible (hidden by CSS)
- [ ] Send Task deferred note visible at the bottom

### 5 — Tasks page renders (dark theme)

Switch to dark theme using the Starlight theme toggle.

- [ ] Dark-theme SVG renders for each variant (no broken image icon)
- [ ] Light-theme SVG is not visible (hidden by CSS)

### 6 — No 404 on SVG requests

Open browser DevTools → Network tab, reload the Tasks page in both themes.

- [ ] No 404 responses for any `/fleans/activities/*.svg` request
- [ ] All 8 SVG URLs resolve (4 × light/dark, though only 4 are visible per theme)

### 7 — Sidebar group

On any docs page, inspect the left sidebar.

- [ ] "BPMN Elements" group appears after the "Concepts" group
- [ ] "Tasks" link is visible under "BPMN Elements"
- [ ] Clicking "Tasks" navigates to `concepts/activities/tasks`

### 8 — BPMN Support matrix cross-link

Navigate to `https://localhost:4321/fleans/concepts/bpmn-support/`.

- [ ] "Tasks" sub-table header reads "Tasks (details)" with a working hyperlink
- [ ] Clicking "details" navigates to `concepts/activities/tasks`

### 9 — Drift-guard pins

```bash
grep -n "." src/Fleans/Fleans.Infrastructure/Bpmn/BpmnConverter.cs | sed -n '303p;313p;333p;354p'
```

Run from `src/Fleans/`.

- [ ] Line 303 — non-empty (Plain Task registration)
- [ ] Line 313 — non-empty (User Task registration)
- [ ] Line 333 — non-empty (Service Task registration)
- [ ] Line 354 — non-empty (Script Task registration)

### 10 — Build check

```bash
cd website && npm run build
```

- [ ] Build exits 0 with no errors
- [ ] No broken link warnings for `/fleans/activities/*.svg` (SVGs must be committed to `public/activities/`)

---

## Expected Outcomes

- [ ] All 8 SVGs generated and valid XML
- [ ] Tasks page renders correctly in both themes
- [ ] No 404s on image requests
- [ ] Sidebar "BPMN Elements → Tasks" wired
- [ ] BPMN Support Tasks header cross-links correctly
- [ ] All 4 BpmnConverter.cs drift-guard line pins resolve
- [ ] `npm run build` green
