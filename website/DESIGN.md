# Fleans design doc

## Overview

Design doc for the Fleans marketing + docs site. Sections owned by individual design issues.

## Color palette

Chosen direction: **Candidate 1 — Brutalist-technical** (owner decision on [#258](https://github.com/nightBaker/fleans/issues/258)). The site signals a workflow engine for backend developers — industrial orange on near-black reads like CLI output and industrial signage, not like default Starlight with a blue tint.

Owner decisions fixed up front:

1. Aesthetic direction: Candidate 1 — brutalist-technical.
2. Per-candidate token completeness: option (a) — full token map for every Starlight token plus the two `--fleans-*` tokens, both themes.
3. `--fleans-accent-2` usage: contract-only in this PR; real usage deferred to [#260](https://github.com/nightBaker/fleans/issues/260).

### Dark theme token map

| Token | Hex | Role |
|---|---|---|
| `--sl-color-accent-low` | `#2a1609` | Callout / admonition background |
| `--sl-color-accent` | `#ff5f1f` | Primary accent — links, focus rings, sidebar hover (industrial orange) |
| `--sl-color-accent-high` | `#ffb38f` | Admonition/callout titles on accent-low |
| `--sl-color-white` | `#f5f5f0` | Body text (Starlight "max-contrast-vs-bg") |
| `--sl-color-gray-1` | `#e5e5df` | Text-adjacent |
| `--sl-color-gray-2` | `#b8b8b1` | |
| `--sl-color-gray-3` | `#8a8a82` | Muted text |
| `--sl-color-gray-4` | `#5a5a53` | |
| `--sl-color-gray-5` | `#2e2e2a` | Panel / card background |
| `--sl-color-gray-6` | `#1a1a17` | Near-bg |
| `--sl-color-black` | `#0a0a0a` | Page bg — Starlight derives `--sl-color-bg` from this |
| `--fleans-accent-2` | `#9eff00` | Sharp secondary accent — terminal lime (contract-only this PR) |
| `--fleans-surface` | `#141411` | Elevated card / code-block surface (dark-only cue) |

Narrative page bg: `#0a0a0a` (near-true black).

### Light theme token map

| Token | Hex | Role |
|---|---|---|
| `--sl-color-accent-low` | `#fbe6d9` | Callout / admonition background |
| `--sl-color-accent` | `#ad2f08` | Primary accent — darker industrial orange for AA on light bg |
| `--sl-color-accent-high` | `#8a2706` | Admonition/callout titles on accent-low |
| `--sl-color-white` | `#0a0a0a` | Body text (Starlight "max-contrast-vs-bg", flipped) |
| `--sl-color-gray-1` | `#1a1a17` | Text-adjacent |
| `--sl-color-gray-2` | `#2e2e2a` | |
| `--sl-color-gray-3` | `#5a5a53` | Muted text |
| `--sl-color-gray-4` | `#8a8a82` | |
| `--sl-color-gray-5` | `#b8b8b1` | Panel / card background |
| `--sl-color-gray-6` | `#e0e0d9` | Near-bg |
| `--sl-color-gray-7` | `#efefea` | Very near-bg |
| `--sl-color-black` | `#f5f5f0` | Page bg — Starlight derives `--sl-color-bg` from this |
| `--fleans-accent-2` | `#3a7d00` | Sharp secondary accent — deeper lime for AA on light bg |
| `--fleans-surface` | `#f5f5f0` | Collapses to bg in light mode (dark-only elevation cue) |

Narrative page bg: `#f5f5f0` (warm off-white).

### Derivation rule

Any future palette tweak follows this rule rather than ad-hoc picks:

- `--sl-color-accent-low` = dominant accent blended toward bg to **L\* ≈ 12%** (dark) / **L\* ≈ 92%** (light) in OKLCH. Produces a background-like tint that stays recognizably the accent hue.
- `--sl-color-accent-high` = dominant accent shifted toward text-color end until it hits **AA (≥4.5:1)** against `--sl-color-accent-low`. Used for admonition titles sitting on callout backgrounds.
- `--sl-color-gray-1 … -6/-7` = perceptually even OKLCH L\* steps, anchored as follows:
  - `gray-1` at **L\*(text) minus 2%** (text-adjacent, moved toward bg)
  - `gray-6` (dark) / `gray-7` (light) at **L\*(bg) plus 2%** (bg-adjacent, moved toward text)
  - intermediate stops at **equal L\* intervals** between the two endpoints
  - the ramp inherits any warm/cool bias the bg carries (Candidate 1 light's bg `#f5f5f0` gives a faintly warm ramp)
- `--sl-color-white` = the theme's text color (Starlight semantic: "max-contrast-vs-bg").
- `--sl-color-black` = the theme's page background color. Starlight's `--sl-color-bg` derives from this token, so setting it to the narrative bg value (instead of a "one step beyond" extreme) is what actually renders the intended background. The derivation rule gives "bg or one step beyond"; we pick "bg" to match the brutalist-technical narrative.
- `--fleans-accent-2` = secondary accent verbatim (no derivation).
- `--fleans-surface` = one step off `--sl-color-gray-6` in dark mode (elevation cue); collapses to bg in light mode (elevation via color is counter-productive on light surfaces).

Re-tuning during implementation stays within **±5% OKLCH L\*** of the table values. Anything beyond the envelope is a design change and needs a fresh review.

Note on the current light-theme `--sl-color-accent`: the plan's starting value `#c4380b` (OklabL = 54.7) missed AA on `accent-on-accent-low` (4.45:1, need 4.5:1) and AA-large on `accent-on-gray-5` (2.69:1, need 3.0:1). Re-tuning darker to `#ad2f08` (OklabL = 49.7, ΔL\* = −4.98, within envelope) lands all pairs above their minimums with comfortable margin.

### Contrast matrix (verified)

WCAG ratios via the sRGB relative-luminance formula (W3C WCAG 2.1 §1.4.3). Verified programmatically (`website/scripts/check-contrast.mjs`). Format: `pair: <fg-token-name> (<#hex>) on <bg-token-name> (<#hex>) = <ratio>:1 (<verdict>)`.

**Dark theme**

```
pair: --sl-color-white (#f5f5f0) on bg (#0a0a0a) = 18.10:1 (AA pass)
pair: --sl-color-accent (#ff5f1f) on bg (#0a0a0a) = 6.51:1 (AA pass)
pair: --sl-color-accent-high (#ffb38f) on --sl-color-accent-low (#2a1609) = 9.94:1 (AA pass)
pair: --sl-color-accent (#ff5f1f) on --sl-color-accent-low (#2a1609) = 5.68:1 (AA pass)
pair: --sl-color-gray-3 (#8a8a82) on bg (#0a0a0a) = 5.69:1 (AA pass)
pair: --fleans-accent-2 (#9eff00) on bg (#0a0a0a) = 15.80:1 (AA-large pass)
pair: --sl-color-accent (#ff5f1f) on --sl-color-gray-5 (#2e2e2a) = 4.48:1 (AA-large pass)
pair: --sl-color-accent (#ff5f1f) on --sl-color-gray-6 (#1a1a17) = 5.74:1 (AA-large pass)
pair: --sl-color-accent (#ff5f1f) on --fleans-surface (#141411) = 6.07:1 (AA-large pass)
```

**Light theme**

```
pair: --sl-color-white (#0a0a0a) on bg (#f5f5f0) = 18.10:1 (AA pass)
pair: --sl-color-accent (#ad2f08) on bg (#f5f5f0) = 6.03:1 (AA pass)
pair: --sl-color-accent-high (#8a2706) on --sl-color-accent-low (#fbe6d9) = 7.34:1 (AA pass)
pair: --sl-color-accent (#ad2f08) on --sl-color-accent-low (#fbe6d9) = 5.47:1 (AA pass)
pair: --sl-color-gray-3 (#5a5a53) on bg (#f5f5f0) = 6.35:1 (AA pass)
pair: --fleans-accent-2 (#3a7d00) on bg (#f5f5f0) = 4.67:1 (AA-large pass)
pair: --sl-color-accent (#ad2f08) on --sl-color-gray-5 (#b8b8b1) = 3.30:1 (AA-large pass)
pair: --sl-color-accent (#ad2f08) on --sl-color-gray-6 (#e0e0d9) = 4.97:1 (AA-large pass)
```

Row 7b (accent on `--fleans-surface`) is dark-only; in light theme `--fleans-surface = bg`, so the pair degenerates to row 2 which already passes.

## Atmospheric details

Added in [#261](https://github.com/nightBaker/fleans/issues/261). Implements four atmospheric techniques that push the brutalist-technical aesthetic beyond palette alone — controlled surface texture, sharp rules, and direction-carrying light/shadow.

### Techniques

#### A. H2 accent rule
A 2px linear-gradient bar beneath each `<h2>`: `linear-gradient(to right, accent 0 3.5rem, transparent 3.5rem)`. Reads as a printer registration mark / CLI prompt bar. Applied in both themes via `.sl-markdown-content h2::after`.

#### B. Dot-matrix noise (dark theme only)
A static SVG (`public/texture-dotmatrix.svg`, ~120×120 viewBox, staggered 1px dots at ~1% coverage) applied as `background-image` on `body::before` at `opacity: 0.04`, `background-size: 240px`. Fixed positioning covers the viewport without scrolling.

Light theme is intentionally texture-free: dot-matrix on warm `#f5f5f0` bg fights the bg tint rather than reinforcing it.

#### C. Decorative borders
Three primitives used sparingly:

1. **Section rule** — `.sl-markdown-content hr` becomes a 1px dashed rule in `--sl-color-gray-4` centered on a `■` glyph in accent color. Reads like a page-break marker.
2. **Code-block corner brackets** — `div.expressive-code::before` uses eight stacked background gradients to paint four 12×12px L-shapes in accent color at each corner. No full border — terminal-panel feel.
3. **Sidebar active-item tick** — `nav a[aria-current="page"]` overrides Starlight's thick left border with a 2px accent bar (`border-inline-start-width: 2px`).

#### D. Scanline overlay (dark theme only)
A `repeating-linear-gradient(0deg, rgba(245,245,240,0.015) 0 1px, transparent 1px 3px)` applied via `main::before` in dark theme. Fixed positioning, `z-index: 1`, `pointer-events: none`. At 1.5% opacity the effect is imperceptible over body text but adds horizontal line rhythm over near-black code-block surfaces — the CRT-over-CLI cue.

### Token additions

| Token | Role | Dark | Light |
|---|---|---|---|
| `--fleans-rule-glyph` | `■` glyph color in section dividers | `= --sl-color-accent` | `= --sl-color-accent` |
| `--fleans-bracket` | Code-block corner bracket color | `= --sl-color-accent` | `= --sl-color-accent` |
| `--fleans-noise-opacity` | Dot-matrix layer opacity | `0.04` | `0` (no noise) |
| `--fleans-scanline-base` | Scanline tint for content area | `rgba(245,245,240,0.015)` | *(undefined — no scanlines)* |

### CSS selector stability

| Target | Selector | Stability |
|---|---|---|
| H2 accent rule | `.sl-markdown-content h2::after` | Stable — documented Starlight override point since v0.15 |
| Section divider | `.sl-markdown-content hr` | Stable — same documented override point |
| Code-block brackets | `div.expressive-code::before` | Stable — ExpressiveCode public API class, v0.41.7 |
| Scanline overlay | `main::before` | Stable — semantic HTML element, won't be renamed |
| Sidebar active tick | `nav a[aria-current="page"]` | Stable — ARIA attribute, part of a11y contract |

No undocumented Starlight internal class selectors. Any future selector targeting a Starlight internal must carry a `/* Verified: Starlight <version> */` comment.

## Hero

Added in [#259](https://github.com/nightBaker/fleans/issues/259). Redesigns the landing hero to inherit the brutalist-technical aesthetic established by the palette (#258) and atmospheric details (#261).

### Tagline

Replaced the default repetitive tagline with two declarative clauses that name the engine's two-layer architecture:

> **Workflow definitions in BPMN.  
> Execution as Orleans grains.**

Uses `<br>` HTML tag in the YAML `tagline:` value. Starlight renders the tagline via `set:html`, so HTML tags parse correctly — no CSS `white-space` trick needed.

### Layout

Starlight's splash hero already implements an asymmetric two-column grid at `min-width: 50rem` (text left, image right). No layout override was needed. Additions are:

- **Accent bar on text column** — `border-inline-start: 3px solid var(--sl-color-accent)` on `.hero .stack` (desktop only, inside `@media (min-width: 50rem)`). CLI-prompt visual cue.
- **H1 typography** — `font-size: clamp(2.5rem, 6vw, 4.5rem)`, `letter-spacing: -0.03em`, `line-height: 1.05`. Increases size contrast without requiring a new font face.
- **Tagline** — rendered in `var(--sl-font-mono, monospace)`, `font-size: 1rem`, `opacity: 0.85`, `max-width: 28rem` to create deliberate negative space to the right.
- **Vertical padding** — `padding-block: 8rem` (dark) / `6rem` (light) for controlled negative space around the hero.

### Hero image

Switched from `image.file: ../../assets/logo.svg` (Astro image pipeline) to `image.html:` with a static `<img src="/fleans/logo.svg">`. The SVG is served from `public/logo.svg` at the predictable static path `/fleans/logo.svg` — consistent with `favicon.svg` and `texture-dotmatrix.svg`. SVG logos need no format conversion or responsive breakpoints, so skipping the image pipeline is appropriate.

Switching to `image.html:` is required to get the `.hero-html` element hook for the corner brackets.

### Atmospheric details (hero-specific)

#### E. Orange-tinted hero scanline (dark theme only)

`.hero::before` — same `repeating-linear-gradient` technique as the body scanline (§ D) but with `rgba(255, 95, 31, 0.04)` instead of the neutral `rgba(245,245,240,0.015)`. The orange tint gives the hero landing zone a warmer atmospheric feel vs. the body. Applied `position: absolute` (not `fixed`) so it clips to the `.hero` area. `.hero` gets `position: relative` as the positioning anchor.

#### F. Corner brackets on hero image (both themes)

`.hero-html::before` — reuses the same eight-gradient technique as code-block brackets (§ C2) with 16px L-shapes (vs. 12px on code blocks) to signal visual prominence. Applied in both themes via the `--fleans-bracket` token.

### CSS selector stability

| Target | Selector | Stability |
|---|---|---|
| Hero section | `.hero` | Starlight stable since v0.10 |
| Text+actions column | `.hero .stack` | Verified: Starlight 0.38.2 — emitted as `class="sl-flex stack"`; scoped to `.hero` |
| Tagline | `.hero .tagline` | Starlight stable since v0.10 |
| H1 | `.hero h1` | Starlight stable since v0.10 |
| Image container | `.hero-html` | Verified: Starlight 0.38.2 — emitted by `Hero.astro` when `image.html:` is used |
