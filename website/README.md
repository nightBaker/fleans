# Fleans Website

Astro + Starlight site for Fleans. Landing page + documentation.

## Local development

Requires Node.js 20+:

```bash
brew install node   # macOS, if not installed
cd website
npm install
npm run dev
```

Open the URL printed by Astro (usually `http://localhost:4321/fleans/`).

## Build

```bash
npm run build
npm run preview
```

## Deployment

Pushes to `main` that touch `website/**` trigger `.github/workflows/deploy-website.yml`,
which builds the site and deploys it to GitHub Pages.

Before the first deploy:

1. In GitHub repo Settings → Pages, set **Source** to **GitHub Actions**.
2. Update `astro.config.mjs`:
   - Set `site` to your actual `https://<github-user>.github.io`
   - Set `base` to `/<repo-name>` (likely `/fleans`)
   - Update the GitHub social link
3. Update `<github-user>` placeholders in `src/content/docs/` markdown files.

## Structure

```
website/
├── astro.config.mjs          # Starlight config (sidebar, branding, deploy base)
├── src/
│   ├── assets/hero.svg       # Landing page hero illustration
│   ├── content/docs/         # All docs pages
│   │   ├── index.mdx         # Landing page (splash template)
│   │   ├── guides/           # Getting Started section
│   │   ├── concepts/         # Architecture, BPMN support
│   │   └── reference/        # API reference
│   ├── content.config.ts     # Starlight content collection
│   └── styles/custom.css     # Brand color overrides
└── public/favicon.svg
```

## Hero BPMN Diagram

The landing page includes a rendered BPMN workflow diagram between the hero and the "Why Fleans?" cards. Two themed SVG variants (light/dark) are pre-rendered from `tests/manual/04-parallel-gateway/fork-join.bpmn` using bpmn-js in a headless Playwright browser.

- **Prerequisites:** `npx playwright install chromium` (one-time setup)
- **Trigger:** re-run when the source fixture changes or `bpmn-js` version is bumped
- **Command:** `cd website && npm run render-bpmn`
- **Output:** `website/public/hero-workflow-light.svg` and `website/public/hero-workflow-dark.svg`
- **Rule:** visually inspect both SVGs in a browser before committing, AND open each file directly (not embedded in a page) to confirm the browser's XML viewer accepts it without a parse-error banner. The output must begin with `<?xml …?>` + `<!-- created with bpmn-js -->` + `<!DOCTYPE svg …>`.
- **Structural cleanup happens in the DOM, not via regex.** `render-bpmn.mjs` calls `viewer.saveSVG()`, round-trips the result through `DOMParser` + `querySelectorAll('.djs-hit, .djs-outline, .djs-dragger').remove()`, then re-serializes via `XMLSerializer` with the prolog/DOCTYPE re-prepended. Do not re-introduce regex-based element stripping — `<[^>]+>` absorbs the `/` of self-closing `<rect class="djs-hit" .../>` tags, causing a non-greedy `[\s\S]*?</[^>]+>` trailer to consume unrelated `</g>` closers (that is the root cause of #366 — 21 missing `</g>` per file, SVG rejected by strict XML parsers).
- **Known limitation:** interior type-markers (script/user/service icons) are stripped from the SVG — only shapes (rectangles, diamonds, circles, arrows) are rendered. The admin UI (Fleans.Web) shows full markers because it loads the bpmn-font.

## 3D Landing Background

The splash page (`website/src/content/docs/index.mdx`) loads an interactive Three.js silo scene as its background via `src/components/SiloBackground.astro`. Key points:

- **Feature-gated:** loads the scene only on desktop (≥ 768 px), when `prefers-reduced-motion` is not set, and when WebGL2 is available. Otherwise renders `public/silo-poster-{dark,light}.webp`.
- **Theme-reactive:** a `MutationObserver` on `<html data-theme>` recolors the scene in place — no reload, no rebuild.
- **Only imported by `index.mdx`:** doc pages are untouched and pay zero bundle cost.
- **Regenerating posters:** if you change scene visuals, run `cd website && npm run posters` (requires `npx playwright install chromium`). Commit the updated `public/silo-poster-*.webp` files.
- **Contrast guardrail:** `cd website && npm run check:contrast` runs a Playwright check that fails if hero text drops below WCAG AA against the themed composite background. It is *not* wired into `npm run build` (that would force CI to install Chromium on every deploy). Run it manually after any change to the silo-background CSS, silo-scene, or hero styling.

## Documentation rules

**Documentation is part of "done", not a follow-up task.** Any new feature, BPMN element, API endpoint, or user-facing behavior MUST be reflected in the docs site in the same PR. Update the relevant page under `website/src/content/docs/` (e.g. new BPMN activity → `concepts/bpmn-support.md`; new endpoint → `reference/api.md`; new workflow → add a guide). If no suitable page exists, create one and add it to the Starlight sidebar in `astro.config.mjs`.

**Load-test results publishing:** `src/content/docs/reference/load-testing.md` publishes load-test results **only for the current public release version** of Fleans. Each result section MUST be headed with `### Fleans vX.Y.Z — YYYY-MM-DD — <stack description>` so readers can scope numbers to a release + run date. When a new release ships, REMOVE prior-version sections from the website page in the same release PR — do not let stale numbers accumulate publicly. The full historical reports stay in the repo at `tests/load/results/<run-id>/report.md` and are recoverable via the matching git tag; only the website page is curated.
