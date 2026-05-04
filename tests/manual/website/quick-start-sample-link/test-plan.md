# Manual Test Plan — Quick Start sample BPMN link (Issue #400)

Verifies that the Quick Start guide's link to the sample BPMN file resolves correctly under the deployed `/fleans/` base path, and that the regression-guard catches any future return of the missing-basepath form.

## Prerequisites

- `cd website && npm install` has been run at least once.
- Dev server NOT already running on port 4321.

## Steps

### 1. Build passes

```bash
cd website
npm run build
```

**Expect:** zero errors. Build emits `dist/guides/quick-start/index.html`.

### 2. Corrected URL renders in built HTML

```bash
grep -c 'href="/fleans/samples/my-process.bpmn"' website/dist/guides/quick-start/index.html
```

**Expect:** ≥ 1.

### 3. Regression guard — broken form is fully removed

```bash
grep -c 'href="/samples/my-process.bpmn"' website/dist/guides/quick-start/index.html
```

**Expect:** **0**. If this is ever ≥ 1, the basepath bug has returned. This is the load-bearing assertion of this test plan.

### 4. Asset is deployed alongside the page

```bash
ls website/dist/samples/my-process.bpmn
```

**Expect:** the file exists. Astro's `public/` → `dist/` copy worked.

### 5. End-to-end click-through (manual)

`cd website && npm run dev`, then:

1. Visit `http://localhost:4321/fleans/guides/quick-start/`.
2. Locate the "A sample BPMN file is available to get you started" line.
3. Click the **my-process.bpmn** link.

**Expect:** the browser downloads the file (or opens its content in a viewer); no 404. Compare the downloaded bytes to `website/public/samples/my-process.bpmn` — they should be identical.

### 6. Drift-guard — basepath constant unchanged

```bash
grep -E "^\s*base:\s*'/fleans'" website/astro.config.mjs
```

**Expect:** ≥ 1 match. If the basepath ever changes (e.g. `/fleans` → `/`), every `/fleans/...` literal in markdown rots — sweep `src/content/docs/**/*.{md,mdx}` for the `/fleans/` literal in the same PR that changes `base`.

## Verdict

- **PASSED** — all 6 steps green. Move PR to Review by Human.
- **FAILED / BUG** — file follow-up issue, send PR back to Ready.
