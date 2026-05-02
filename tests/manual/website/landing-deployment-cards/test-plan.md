# Landing page — Deployment-posture hero cards

## Scenario

The website splash (`/fleans/`) communicates Fleans's deployment posture — self-hosted runtime, pluggable persistence, pluggable streaming — directly in the "Why Fleans?" `<CardGrid>`. Three additional cards are appended after the existing six (positions 7–9), each linking to the relevant reference page (`reference/self-hosting/`, `reference/persistence/`, `reference/streaming/`).

This plan verifies that the cards render in both themes, that their icons resolve to real glyphs (not silently substituted fallbacks), that each link target returns 200, and that the 9-card single-column flow is acceptable on a narrow viewport.

> **Note:** issue #406 (release pipeline + container builds) governs whether the `ghcr.io/nightbaker/fleans-*` images referenced by `reference/self-hosting/` are actually published. The card copy itself is unconditionally correct ("ships as a container image"), but if a tester clicks through to the linked page and follows the `docker pull` instructions before #406 lands, the pull will 404 — that is a known, out-of-scope dependency.

## Prerequisites

- `cd website && npm install` has been run at least once.
- Dev server NOT already running on ports 4321/4327/4328.

## Steps

1. **Start the dev server.**
   ```bash
   cd website
   npm run dev
   ```
   Open `http://localhost:4321/fleans/` in a desktop browser.

2. **Verify card count and order in light theme.**
   - Toggle theme to light if not already.
   - Scroll to "Why Fleans?".
   - Count cards. Expect **9** total.
   - The new cards appear in positions 7, 8, 9 in this order:
     - "Run in your infrastructure"
     - "Bring your own database"
     - "Plug in your stream provider"
   - The original six cards (Actors / Scale reads / Event-sourced / Typed BPMN / In-memory / Materialized projections) are unchanged and appear first.

3. **Verify icons render as real glyphs in light theme.**
   - For each of the three new cards, inspect the icon at the top of the card.
   - Each icon must be a real glyph (gear/cog for `setting`, list-rows for `list-format`, puzzle piece for `puzzle`).
   - **Reject any** icon that renders as a generic placeholder (e.g., a default fallback square / open-book / question mark) — this indicates Starlight silently substituted because the requested name does not resolve. `npm run build` does NOT catch silent fallbacks.

4. **Switch to dark theme and re-verify.**
   - Toggle theme to dark.
   - All 9 cards still render.
   - All three new icons remain real glyphs (no fallback rectangles, no broken-image symbol, no inverted-color artifacts).
   - Card body copy is readable against the dark background.

5. **Verify each new card's link target.**
   - Click "Learn how →" on **"Run in your infrastructure"** → lands on `/fleans/reference/self-hosting/` (page title: "Self-Hosting on Kubernetes" or similar). Returns 200.
   - Back to splash. Click "Learn how →" on **"Bring your own database"** → lands on `/fleans/reference/persistence/`. Returns 200.
   - Back to splash. Click "Learn how →" on **"Plug in your stream provider"** → lands on `/fleans/reference/streaming/`. Returns 200.

6. **Mobile-viewport scroll-length sanity check.**
   - Resize browser to ≤ 480 px width (or use DevTools mobile emulation).
   - Scroll the "Why Fleans?" section.
   - The grid collapses to a single column (existing behaviour, inherited).
   - Each card's body copy fits inside the card — no horizontal overflow, no clipped text.
   - Total scroll length is acceptable (no card stretches absurdly tall, no excessive whitespace).

7. **Production build sanity.**
   - `Ctrl+C` the dev server.
   - Run `npm run build` from `website/`.
   - Build must complete without errors. (Build does **not** catch silent icon fallback — that is what step 3/4 covers.)

## Expected outcomes (checklist)

- [ ] Splash shows 9 cards in light theme, in the correct order.
- [ ] Splash shows 9 cards in dark theme, in the correct order.
- [ ] `setting`, `list-format`, `puzzle` icons render as real glyphs (not fallbacks) in light theme.
- [ ] `setting`, `list-format`, `puzzle` icons render as real glyphs (not fallbacks) in dark theme.
- [ ] "Run in your infrastructure" link → `/fleans/reference/self-hosting/` (200).
- [ ] "Bring your own database" link → `/fleans/reference/persistence/` (200).
- [ ] "Plug in your stream provider" link → `/fleans/reference/streaming/` (200).
- [ ] Mobile (≤ 480 px) viewport: 9-card single-column flow is readable; no overflow; no clipped copy.
- [ ] `npm run build` completes without errors.
