# Landing page 3D silo background — design

**Status:** Draft (approved in brainstorming, pending spec review)
**Date:** 2026-04-22
**Branch:** `feature/landing-3d-background`
**Scope:** Website only. No changes to `Fleans.*` .NET projects.

## 1. Goal

Replace the static splash hero background with an interactive 3D scene of the
Orleans silo cluster (the scene that currently exists as a Vite sample at
`~/Downloads/orleans-framework-webgl-silo-c`, already partially copied into
`website/scripts/3d-scene.js`). Deliver two distinct experiences on one page:

- **Landing (idle):** birds-eye view of the cluster, quietly animating behind
  the hero. Primary job: evoke "distributed cluster" as a visual metaphor for
  what Fleans runs on, without stealing focus from the copy.
- **Exploration (interactive):** the hero and feature cards fade out, the
  viewer takes control of the camera (rotate / zoom / pan), and a close
  button returns them to the landing.

The background must respect the selected theme (dark ↔ light) and users with
reduced-motion / small viewports / no-WebGL must get a calm static fallback.

## 2. Non-goals

- No 3D on doc pages (`/guides/*`, `/concepts/*`, `/reference/*`).
- No deep-link URL for interactive mode (no `#explore` hash). Reload always
  starts idle.
- No tutorial overlay or coach marks. The × button is the only affordance.
- No new .NET projects, grains, or engine changes.
- No changes to Starlight internal components (no component overrides).

## 3. User-facing behavior

### 3.1 Viewport/capability matrix

| Condition                                                | Experience |
|----------------------------------------------------------|------------|
| Desktop (≥ 768 px), no reduced-motion, WebGL2 available  | Full live scene |
| < 768 px viewport                                        | Static poster |
| `prefers-reduced-motion: reduce`                         | Static poster |
| WebGL2 unavailable                                       | Static poster |
| Non-splash pages (guides / concepts / reference)         | No canvas, no poster, no JS cost |

The decision is made once on page load in `silo-bg-controller.ts`; switching
after load is not supported (users who toggle reduced-motion mid-session see
their choice on the next navigation).

### 3.2 State machine (live-scene path only)

Two states; everything else is transitions.

```
              initial page load
                     │
                     ▼
               ┌──────────┐
               │  IDLE    │◄──────── × button click
               └─┬────────┘          Escape key
                 │
    pointerdown on background   (target not inside
                 │               .hero, .card,
                 │               header.header, .sidebar)
                 ▼
            ┌──────────────┐
            │ INTERACTIVE  │
            └──────────────┘
```

**IDLE:**
- Camera locked at birds-eye pose, OrbitControls disabled.
- Scene still animates (silos pulse, comm lines flow) at ~30% of full rotation
  speed — a "quiet life" background.
- Hero + "Why Fleans?" cards fully visible. Canvas sits behind them with
  `pointer-events: none`.
- Contrast overlay (gradient) sits between canvas and hero copy; see §6.3.
- × button is `hidden` (attribute, not CSS) — not focusable.

**INTERACTIVE:**
- Hero + cards fade out (`opacity → 0`, 240 ms, sharp-out curve from
  `--fleans-ease-sharp-out`) then `visibility: hidden` so they can't trap
  pointer events or focus.
- Contrast overlay also fades with them.
- Canvas gets `pointer-events: auto`. OrbitControls enabled with limits in §5.2.
- Camera animates from birds-eye to exploration pose over ~600 ms.
- × button fades in (top-right, below Starlight nav); focus moves to it.
- Starlight top nav bar stays visible throughout (user can still swap theme,
  hit search, jump to GitHub).

**Escape to IDLE:**
- Any of: click × button, `Enter`/`Space` while × focused, `Escape` key.
- Reverse transitions run: × fades out, camera animates back to birds-eye,
  hero + cards + overlay fade back in. Focus restores to `document.activeElement`
  saved at INTERACTIVE entry.
- Scene animation loop never stops during the transition.

### 3.3 Theme behavior

- Theme toggle is Starlight's default. It flips `data-theme` on
  `document.documentElement`.
- A `MutationObserver` on that attribute calls
  `sceneController.setTheme('dark' | 'light')`, which re-reads CSS custom
  properties and mutates scene materials/lights/bloom in place. No geometry
  rebuild, no canvas re-mount.
- Theme swap works in either state; camera pose and interaction mode are
  preserved.
- The static poster path swaps between `silo-poster-dark.webp` and
  `silo-poster-light.webp` on the same `data-theme` change.

## 4. File layout

```
website/
├── package.json                       # + "three": "^0.183.x"
├── public/
│   ├── silo-poster-dark.webp          # ~1920×1080, ≤ 180 KB
│   └── silo-poster-light.webp         # ~1920×1080, ≤ 180 KB
├── scripts/
│   └── generate-posters.mjs           # Playwright: render scene, screenshot,
│                                      # writes both WebPs. Run locally, not CI.
└── src/
    ├── components/
    │   └── SiloBackground.astro       # markup: canvas, × button, <picture>
    ├── scripts/
    │   ├── silo-scene.ts              # Three.js scene; typed port of
    │   │                              # website/scripts/3d-scene.js
    │   └── silo-bg-controller.ts      # feature detection, state machine,
    │                                  # theme observer, click routing
    ├── styles/
    │   └── silo-background.css        # canvas layer, overlay, × button,
    │                                  # hero/card fade hooks
    └── content/docs/index.mdx         # imports SiloBackground; renders once
```

The current `website/scripts/3d-scene.js` is replaced by
`src/scripts/silo-scene.ts`. The old file is deleted in the same PR.

### 4.1 Module responsibilities

| Module | Knows about | Does NOT know about |
|---|---|---|
| `silo-scene.ts` | Three.js, render loop, camera tween, materials, scene graph | DOM outside its canvas, themes (takes a color bag), feature detection, state machine |
| `silo-bg-controller.ts` | DOM, theme attribute, viewport/motion media queries, state machine, × button, click routing | Three.js internals (imports scene via dynamic `import()` and calls its public API) |
| `SiloBackground.astro` | Markup only: canvas, × button, poster `<picture>`, one inline `<script>` that imports the controller | Three.js, theme, state — all deferred to the controller |
| `index.mdx` | Imports and renders `<SiloBackground />` once above existing content | Everything else |

## 5. Scene composition

### 5.1 Content

Seven silos (one central primary + six in a hex ring), ground disc, reference
grid, animated communication lines between silos, bloom post-processing.
Geometry and animation logic are inherited from `website/scripts/3d-scene.js`
as-is; only the camera pose, colors, lights, and bloom parameters become
theme-driven.

### 5.2 Camera poses

| State | Position | Target | FOV | Polar limits |
|---|---|---|---|---|
| IDLE (birds-eye) | `(0, 55, 0.01)` | `(0, 0, 0)` | 50° | N/A (controls disabled) |
| INTERACTIVE default | `(0, 22, 40)` | `(0, 4, 0)` | 60° | `[0, π/2.05]` |

The `z = 0.01` epsilon in IDLE prevents OrbitControls gimbal-lock when
INTERACTIVE re-enables controls mid-tween.

OrbitControls limits (INTERACTIVE):
- `minDistance: 15`, `maxDistance: 70`
- `minPolarAngle: 0`, `maxPolarAngle: Math.PI / 2.05`
- `enableDamping: true`, `dampingFactor: 0.05`
- `enablePan: true`, target clamped so the user can't pan off the ground disc
- In IDLE: `controls.enabled = false` (instance kept; never added/removed to
  avoid event-listener churn).

### 5.3 Camera tween

- Implemented inline in `silo-scene.ts`. Stores `{fromPos, toPos, fromTarget,
  toTarget, fromFov, toFov, duration, easing, onComplete}` and interpolates
  each frame inside the render loop.
- Easing matches the CSS `--fleans-ease-sharp-out`: `cubic-bezier(.2, .8, 0, 1)`.
- ~20 lines, no GSAP/tween.js dependency.
- Window resize during a tween updates camera aspect only; the tween isn't
  cancelled.

### 5.4 Animation rate

- Overall cluster rotation runs at ~30% speed in IDLE, full speed in
  INTERACTIVE.
- Silo emissive pulse and comm-line dash advance at full speed in both states
  — these are small motions and give the "live" feel behind the hero.

## 6. Theme adaptation

### 6.1 Token-to-scene mapping

Color values are read from `getComputedStyle(document.documentElement)` on
init and on each theme change. No hex literals survive in `silo-scene.ts`.

| Scene role | CSS custom property | Dark value | Light value |
|---|---|---|---|
| Scene background | `--fleans-surface` | `#141411` | `#f5f5f0` |
| Fog color | `--fleans-surface` | same | same |
| Primary silo fill | `--sl-color-accent` | `#4eb5a6` | `#1f6357` |
| Accent / secondary silo | `--fleans-accent-2` | `#9eff00` | `#3a7d00` |
| Comm line color | `--sl-color-accent-high` | `#a8e0d6` | `#175046` |
| Ground disc | `--sl-color-gray-6` | `#1a1a17` | `#e0e0d9` |
| Grid lines | `--sl-color-gray-5` | `#2e2e2a` | `#b8b8b1` |

| Scalar | Dark | Light |
|---|---|---|
| Ambient light intensity | 0.6 (cool) | 0.9 (neutral) |
| Directional light intensity | 1.0 | 0.5 |
| Bloom strength | 0.9 | 0.15 |
| Bloom threshold | 0.8 | 0.95 |
| Material emissiveIntensity | 0.4 | 0.05 |

### 6.2 Apply-on-change mechanics

- `silo-bg-controller.ts` reads the tokens, parses hex/rgb strings into
  `THREE.Color`-compatible values, and calls `sceneController.setTheme(colors)`.
- `silo-scene.ts` mutates existing material/light/bloom properties in place
  (`material.color.set(...)`, `light.intensity = ...`,
  `bloomPass.strength = ...`). No dispose, no recreate.
- A single `MutationObserver` on `<html>` with `attributeFilter: ['data-theme']`
  drives the call. No debouncing — Starlight writes at most once per toggle.

### 6.3 Hero contrast overlay

- A `div.silo-contrast-overlay` sits between the canvas and the hero, inside
  `SiloBackground.astro`. Visible in IDLE, fades out in INTERACTIVE.
- Dark theme:
  `linear-gradient(180deg, rgba(20,20,17,0) 0%, rgba(20,20,17,0.55) 60%, rgba(20,20,17,0.8) 100%)`
- Light theme:
  `linear-gradient(180deg, rgba(245,245,240,0) 0%, rgba(245,245,240,0.65) 55%, rgba(245,245,240,0.9) 100%)`
- With `prefers-reduced-transparency: reduce`: overlay becomes a solid
  `var(--fleans-surface)` at `0.95` alpha — same readability, no gradient.
- Contrast is verified automatically — see §9.2.

## 7. Fallback path

### 7.1 Feature detection

```ts
function shouldUseLiveScene(): boolean {
  if (typeof window === 'undefined') return false;
  if (window.matchMedia('(prefers-reduced-motion: reduce)').matches) return false;
  if (window.matchMedia('(max-width: 767px)').matches) return false;
  if (!supportsWebGL2()) return false;
  return true;
}
```

`supportsWebGL2()` probes a disposable `<canvas>` for `getContext('webgl2')`.

### 7.2 What the fallback renders

A `<picture>` with theme-aware sources. Initial `src` is written by a small
inline script in `SiloBackground.astro` that runs *before* first paint, reading
`document.documentElement.dataset.theme` (Starlight sets this inline during
head processing). This prevents a flash of wrong-theme poster on light mode.

```html
<picture class="silo-poster" aria-hidden="true">
  <img alt=""
       data-theme-src-dark="/fleans/silo-poster-dark.webp"
       data-theme-src-light="/fleans/silo-poster-light.webp" />
</picture>
<script is:inline>
  // runs synchronously before first paint
  const img = document.currentScript.previousElementSibling.querySelector('img');
  const theme = document.documentElement.dataset.theme || 'dark';
  img.src = img.dataset[`themeSrc${theme === 'light' ? 'Light' : 'Dark'}`];
</script>
```

On each subsequent `data-theme` change the controller swaps `img.src` between
the two files. We can't rely on `prefers-color-scheme` media queries because
Starlight's theme toggle overrides the OS preference.

In the fallback path, the controller never imports `silo-scene.ts`, never
attaches click-to-interact, and never creates the × button.

### 7.3 Poster generation

`website/scripts/generate-posters.mjs`:
1. Start `astro dev` (or a one-off `vite preview` of a pre-build) on a random
   port.
2. Launch Playwright headless Chromium at `1920×1080`.
3. Navigate to the splash page, wait for one full rotation cycle (~6 s).
4. For each theme ∈ {dark, light}:
   - `page.evaluate(t => document.documentElement.setAttribute('data-theme', t), theme)`
   - `page.screenshot({ path: 'public/silo-poster-<theme>.webp', type: 'webp', quality: 80 })`
5. Assert each output file ≤ 180 KB; fail otherwise.

Not wired into CI. Run manually (`npm run posters`) when scene visuals change.
Outputs are committed to the repo.

## 8. Accessibility

- Canvas + poster: `aria-hidden="true"`. Screen readers skip the background.
- × button: `role` implicit via `<button>`, `aria-label="Return to landing page"`.
- Focus management:
  - On INTERACTIVE entry: save `document.activeElement`, move focus to ×.
  - On IDLE return: restore focus to the saved element.
- `Escape` key is bound only in INTERACTIVE — IDLE leaves `Escape` free for
  Starlight's search/menu behavior.
- `pointerdown` click-to-interact ignores events whose target is (or has an
  ancestor matching) `.hero`, `.card`, `header.header`, `.sidebar`, or
  `[role="link"]`. Keyboard-only users never accidentally enter INTERACTIVE —
  it's strictly a pointer affordance.
- `prefers-reduced-motion: reduce` → poster path, no transitions, no render
  loop.
- `prefers-reduced-transparency: reduce` → solid contrast overlay (see §6.3).

## 9. Performance

### 9.1 Bundle budget

- Doc pages: **0 B added**. `SiloBackground` is only imported by `index.mdx`.
- Splash page initial JS: controller module only (~2–3 KB gz).
- Three.js (~160 KB gz) loads lazily via `requestIdleCallback` (or
  `setTimeout(fn, 200)` fallback) — only on the live-scene path.
- Posters: ≤ 180 KB each, ≤ 720 KB total committed.

### 9.2 Contrast check

Add `website/scripts/check-landing-contrast.mjs` (sibling to the existing
`check-contrast.mjs`, kept separate so its failure mode is obvious):
1. Launch Playwright, load splash in dark + light themes.
2. Measure hero title and tagline contrast ratio against the composite
   background (canvas + overlay).
3. Fail build if any ratio < 4.5:1 (WCAG AA for normal text) or < 3:1 for
   large headings.

Wire into `npm run build` via an npm `prebuild` script in
`website/package.json`:

```json
"scripts": {
  "prebuild": "node scripts/check-landing-contrast.mjs",
  "build": "astro build"
}
```

`prebuild` runs automatically before `build`, failing the CI step if the
contrast check fails.

### 9.3 Lifecycle

- `pagehide` / `beforeunload`: `sceneController.dispose()` — cancels
  `requestAnimationFrame`, disconnects `MutationObserver`, removes document
  listeners, calls `renderer.dispose()`, iterates scene graph and calls
  `geometry.dispose()` + `material.dispose()` on each mesh.
- Safari bfcache: same handler; on `pageshow` with `persisted === true`, the
  controller re-initializes from scratch.

## 10. Testing

### 10.1 Automated

- `cd website && npm run build` — must pass; gated in CI via
  `.github/workflows/deploy-website.yml`.
- Contrast check runs automatically as the `prebuild` npm hook before
  `astro build`; see §9.2 for wiring.
- No unit tests — scene code is tightly coupled to WebGL and covered by the
  manual plan.

### 10.2 Manual test plan

New folder `tests/manual/website/3d-landing/` with `test-plan.md`:

| # | Prereq / action | Expected |
|---|---|---|
| 1 | `cd website && npm run dev`; open `http://localhost:4321/fleans/` in Chrome desktop. | Birds-eye silo scene animates as background. Hero reads clearly. |
| 2 | Click Starlight's theme toggle. | Scene background + silo tints swap within 1 frame. Camera pose and interactivity state unchanged. |
| 3 | Click on empty space below the hero (outside cards). | Hero + cards fade out over ~240 ms. × button appears top-right. Camera eases to exploration angle over ~600 ms. |
| 4 | Drag inside the canvas; scroll wheel; right-drag to pan. | Orbit, zoom, pan respond. Camera stays within `minDistance/maxDistance`. |
| 5 | Press `Escape`. | × fades out, camera returns to birds-eye, hero + cards fade back in. Focus returns to where it was before INTERACTIVE. |
| 6 | Repeat step 3, then click ×. | Same return as step 5. |
| 7 | Resize browser to 375×812; reload. | Static poster visible, no `<canvas>` in DOM, Network tab shows no `three*.js`. |
| 8 | macOS System Preferences → Accessibility → Display → "Reduce motion" ON; reload at desktop size. | Static poster visible, no canvas, no Three.js fetched. |
| 9 | In poster mode (step 7 or 8), toggle theme. | Poster `<img>` swaps between `silo-poster-dark.webp` and `silo-poster-light.webp`. |
| 10 | Navigate to `/fleans/guides/introduction/`. | No canvas, no poster. Network tab shows no Three.js fetched on this page. |

A new top-level section "Website regression tests" is added to `CLAUDE.md`
(sibling to the existing BPMN "Regression tests" list), with this plan as its
first entry. Future website regression tests append here.

## 11. Acceptance criteria

- [ ] Splash page renders birds-eye silo scene behind hero; hero copy passes
      WCAG AA contrast in both themes.
- [ ] Theme toggle recolors scene live without reload, preserving camera
      pose and interaction state.
- [ ] Click outside hero/cards/nav fades hero + cards, enables orbit/zoom/pan,
      shows × button; focus moves to ×.
- [ ] ×, Escape, or click × returns to idle; hero + cards fade back in; focus
      restores.
- [ ] Viewport < 768 px OR `prefers-reduced-motion: reduce` OR no WebGL2 →
      static WebP poster; no Three.js fetched; × button and click-to-interact
      never created.
- [ ] Doc pages: bundle size unchanged (measured via `npm run build` output);
      no canvas, no poster, no Three.js fetched.
- [ ] `npm run build` passes locally and in CI.
- [ ] `tests/manual/website/3d-landing/test-plan.md` exists; all 10 steps pass.
- [ ] `CLAUDE.md` documentation rule satisfied: a new usage note under the
      Documentation Website section explains what the 3D background does,
      how to regenerate posters, and what feature-detection gates it.
- [ ] Posters regenerated and committed; each ≤ 180 KB.
- [ ] `website/scripts/3d-scene.js` (the current draft) is deleted;
      replaced by `src/scripts/silo-scene.ts`.
- [ ] Branch: work lands on `feature/landing-3d-background`; PR opened
      against `main`, CI green.

## 12. Out-of-scope follow-ups

- Second landing variant (e.g., per-persona landing pages).
- User-adjustable scene parameters (silo count, animation speed).
- Ability to deep-link into INTERACTIVE state via URL hash.
- Replacing poster generation with a render-service-on-CI path.

These are explicitly deferred and must not creep into the implementation PR.
