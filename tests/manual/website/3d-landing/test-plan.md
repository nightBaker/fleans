# Manual test plan — 3D silo landing background

**Feature:** Splash page (`/fleans/`) renders an interactive Three.js silo cluster as its background.

**Spec:** `docs/superpowers/specs/2026-04-22-landing-3d-background-design.md`

## Prerequisites

- Node 22 installed.
- `cd website && npm install` has been run at least once.
- `npx playwright install chromium` has been run at least once (needed for step 8 locally; safe to skip if step 8 is deferred to CI).
- Dev server NOT already running on port 4321.

## Steps

Each step: note `PASSED`, `FAILED`, `BUG`, or `KNOWN BUG` (with a linked issue for the last two).

1. **Scene renders (dark theme, desktop).**
   From `website/`, run `npm run dev`. Open `http://localhost:4321/fleans/` in Chrome desktop with DevTools Network + Console open.
   **Expected:** No console errors. Birds-eye silo scene visible as background. Hero title "Fleans", tagline, logo, and action buttons are readable. "Why Fleans?" section with 4 cards visible below hero. `three-*.js` chunk appears in Network tab within ~500 ms of page load.

2. **Theme toggle recolors scene.**
   Click Starlight's theme toggle (moon/sun icon in top nav). Toggle back.
   **Expected:** Scene background, silo tints, bloom intensity all swap within 1 frame. Camera pose unchanged. Hero copy still readable.

3. **Click-to-interact: hero fades, close button appears.**
   Click on a blank area below the hero/cards (e.g., near the bottom-middle of the viewport, outside any card).
   **Expected:** Hero block + "Why Fleans?" section fade out over ~240 ms. × button appears top-right below the nav bar. Camera eases from birds-eye to a lower orbit over ~600 ms.

4. **Orbit / zoom / pan work.**
   Left-drag inside the canvas. Scroll the wheel. Right-drag (or two-finger drag on trackpad).
   **Expected:** Camera orbits (but doesn't go below ground), zooms within the configured distance bounds, pans. Damping is smooth.

5. **Escape returns to IDLE.**
   Press `Escape`.
   **Expected:** × button fades out and becomes `hidden`. Camera animates back to birds-eye. Hero + cards fade back in over ~240 ms. Keyboard focus restores to whatever it was before step 3 (document body in most cases).

6. **× button returns to IDLE.**
   Repeat step 3. Then click the × button.
   **Expected:** Same return behavior as step 5.

7. **Mobile viewport → static poster.**
   Open DevTools → Device Toolbar, select a 375×812 profile, reload.
   **Expected:** `<canvas>` hidden; `<picture>` visible showing `silo-poster-dark.webp`. Network tab shows no `three-*.js` chunk. Clicking the background does nothing. × button is never created.

8. **Reduced motion → static poster.**
   Close the mobile emulation. In Chrome DevTools → Rendering tab (three-dots menu → More tools → Rendering), enable "Emulate CSS media feature prefers-reduced-motion: reduce". Reload.
   **Expected:** Same as step 7 — static poster, no canvas, no Three.js fetched.

9. **Poster theme swap.**
   While in poster mode (step 7 or 8), toggle the theme.
   **Expected:** Poster `<img src>` swaps between `silo-poster-dark.webp` and `silo-poster-light.webp`. No Three.js fetched.

10. **Doc pages unaffected.**
    Disable any reduced-motion emulation and device-toolbar. Navigate to `http://localhost:4321/fleans/guides/introduction/`.
    **Expected:** No `<canvas>`, no `<picture>` poster, no `three-*.js` in Network tab. Standard docs layout with sidebar.

## On completion

Stop the dev server. Report results back in the PR description.
