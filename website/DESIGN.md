# Fleans Website Design

Design decisions, rationale, and guidelines for the Fleans documentation website. Sections are owned by individual design issues.

## Aesthetic Direction

> **Status note — retrospective brief:** This section codifies aesthetic decisions already validated by sibling issues #257–#261 (typography pair, color palette, hero redesign, motion details, visual details). It does not prescribe new choices; it documents the reasoning behind decisions already in review so future contributors have a coherent reference instead of scattered PR descriptions.
>
> All subsections below are written in the descriptive voice (*"the site is designed to…"*), not the prescriptive voice (*"the site should…"*). Contributors who deviate from this brief should have a conscious reason, not just an absent reference.

### Audience

Backend .NET engineers and architects who:
- Already know what BPMN is or are willing to learn it
- Are evaluating whether Fleans replaces Camunda, Temporal, or a home-grown state machine
- Work primarily in dark IDEs and terminals
- Distrust tools that hide complexity; respect tools that are honest about it

They do not need to be convinced workflows are important. They need to be convinced Fleans is the right implementation.

### Tone

**Precise. Formal. Confident without being loud.**

The tone is that of a well-designed technical specification document — dense when it needs to be, clear when it can afford to be. Not terse to the point of unfriendliness, but never chatty. Headings read like section labels in an RFC. Code is always shown, never described.

Visual corollary: tight typographic rhythm, exact spacing, meaningful contrast. Ornamentation only when it carries meaning (e.g., a BPMN flow diagram as a visual element, not just a screenshot).

### What Makes It Unforgettable

Three things no other .NET workflow engine site is designed to do:

1. **The BPMN diagram as hero art** — A real BPMN flow rendered in the page's own visual language (CSS or inline SVG), not a screenshot. Backend devs immediately recognise it and feel at home. Non-devs see a sophisticated data-flow graphic.

2. **IBM Plex as a tribal signal** — IBM Plex Sans is the font IBM uses for developer-facing products (VS Code extension docs, Cloud platform docs). For backend devs who have worked in IBM/enterprise spaces, it signals 'serious tooling' without a word being said.

3. **Dark-first, light-correct** — Almost all dev-tool sites are designed light with a dark mode bolted on. Fleans inverts this. The dark theme is the reference; light is the polite alternative. The color palette (near-true black + industrial orange accent) is built to be extraordinary in dark mode and merely clean in light.

### Light Mode Constraint

Light mode uses the same type scale, spacing, and layout as dark mode. **Only surface colors and accent lightness change.** The constraint: light mode must read as *specification-grade clean* — white paper with ink, not *bright app*. Concretely:

- Backgrounds stay in the warm off-white range (`#f5f5f0`), no colored gradients
- No decorative fills that appear only in light mode
- Accent uses a darker shade of the same industrial orange used in dark mode (≈ 20–30% darkened for contrast on white; see [Color & Theme](#color--theme) for exact tokens)
- A contributor adding a new page should be able to follow this rule without guessing

### Rejected Alternatives

The following directions were explicitly evaluated and rejected during the design phase (issues #257–#261):

| Alternative | Why Rejected |
|---|---|
| **Vercel-style gradient-dark** (deep background, purple/blue linear gradients, large sans-serif hero) | Signals "premium SaaS product" — Fleans is infrastructure, not a consumer product. The aesthetic would misrepresent the tool's category to its primary audience. |
| **Go.dev minimal white** (sparse white layout, large body text, no visual differentiation) | Too sparse to carry personality. Backend devs associate the style with Google's house style, not the .NET/Orleans ecosystem. Under-signals craft. |
| **Retro-futuristic neon** (dark background, neon green/magenta accents, monospace-heavy) | Decorative over substantive. Signals gaming or crypto tooling, not enterprise workflow execution. The monospace-everywhere approach conflicts with the RFC-document tone. |
| **Starlight stock theme (no customisation)** | Signals "template project" — the exact perception the redesign aims to eliminate. Indistinguishable from hundreds of other Starlight-based docs sites. |

---

## Typography

**Display (headings): Space Grotesk** — `@fontsource/space-grotesk`
**Body (prose): IBM Plex Sans** — `@fontsource/ibm-plex-sans`

### Rationale

| | Space Grotesk (headings) | IBM Plex Sans (body) |
|---|---|---|
| **Character** | Geometric grotesque with distinctive terminals — precise and engineered | Humanist sans with IBM industrial heritage; comfortable at body size |
| **Brutalist-technical fit** | Geometric construction mirrors BPMN diagram notation | Enterprise/backend-dev association without being loud |
| **Weights used** | **700** (h1/h2, .hero h1, .site-title, .group-label .large), **500** (h3/h4) | **400** (body), **400-italic**, **600** (UI labels/strong) |
| **@fontsource** | `@fontsource/space-grotesk` | `@fontsource/ibm-plex-sans` |

### Why not alternatives

- *Syne + DM Sans*: Syne is too art-forward for a BPMN workflow engine site that needs to project reliability.
- *IBM Plex Mono as body*: Monospace for long-form prose degrades readability. Mono stays for code only (handled by `--sl-font-mono`).
- *Space Grotesk throughout*: IBM Plex Sans's humanist proportions read more naturally at body size on long docs pages.

### Implementation notes

- `--sl-font: 'IBM Plex Sans', sans-serif` is set in a theme-agnostic `:root {}` block in `custom.css`, applying the body font globally via Starlight's `--__sl-font` computed variable.
- Space Grotesk is applied to heading selectors with explicit `font-weight` declarations so both weight imports are actively rendered.
- Sidebar group labels (`.group-label .large`) and site title (`.site-title`) use Space Grotesk at weight 700 — verified against `@astrojs/starlight` 0.38.2 source.
- `@fontsource` v5 ships `font-display: swap` in all generated CSS — no additional configuration needed.
- IBM Plex Sans weight 500 is NOT imported — no Starlight 0.38.2 element uses it. All UI emphasis uses weight 600.

---

## Color & Theme

Chosen direction: **Technical-clean with teal accent** (owner decision on [#327](https://github.com/nightBaker/fleans/issues/327), superseding the original orange palette from [#258](https://github.com/nightBaker/fleans/issues/258)). The site signals a workflow engine for backend developers — teal on near-black reads as precise and technical without the industrial harshness of orange.

Owner decisions fixed up front:

1. Aesthetic direction: technical-clean (teal `#4eb5a6` as primary accent).
2. Per-candidate token completeness: option (a) — full token map for every Starlight token plus the two `--fleans-*` tokens, both themes.
3. `--fleans-accent-2` usage: contract-only in this PR; real usage deferred to [#260](https://github.com/nightBaker/fleans/issues/260).

### Dark theme token map

| Token | Hex | Role |
|---|---|---|
| `--sl-color-accent-low` | `#0f2926` | Callout / admonition background (teal tint at L* ≈ 12%) |
| `--sl-color-accent` | `#4eb5a6` | Primary accent — links, focus rings, sidebar hover (teal) |
| `--sl-color-accent-high` | `#a8e0d6` | Admonition/callout titles on accent-low |
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
| `--sl-color-accent-low` | `#d4f0eb` | Callout / admonition background (teal tint at L* ≈ 92%) |
| `--sl-color-accent` | `#1f6357` | Primary accent — darkened teal for AA on light bg |
| `--sl-color-accent-high` | `#175046` | Admonition/callout titles on accent-low |
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

Note on the current light-theme `--sl-color-accent`: the raw teal `#4eb5a6` fails AA on light bg (2.26:1). Darkening to `#1f6357` achieves 6.44:1 on bg while staying in the same hue family. All pairs meet or exceed WCAG AA minimums with comfortable margin.

### Contrast matrix (verified)

WCAG ratios via the sRGB relative-luminance formula (W3C WCAG 2.1 §1.4.3). Verified programmatically (`website/scripts/check-contrast.mjs`). Format: `pair: <fg-token-name> (<#hex>) on <bg-token-name> (<#hex>) = <ratio>:1 (<verdict>)`.

**Dark theme**

```
pair: --sl-color-white (#f5f5f0) on bg (#0a0a0a) = 18.10:1 (AA pass)
pair: --sl-color-accent (#4eb5a6) on bg (#0a0a0a) = 8.00:1 (AA pass)
pair: --sl-color-accent-high (#a8e0d6) on --sl-color-accent-low (#0f2926) = 10.47:1 (AA pass)
pair: --sl-color-accent (#4eb5a6) on --sl-color-accent-low (#0f2926) = 6.21:1 (AA pass)
pair: --sl-color-gray-3 (#8a8a82) on bg (#0a0a0a) = 5.69:1 (AA pass)
pair: --fleans-accent-2 (#9eff00) on bg (#0a0a0a) = 15.80:1 (AA-large pass)
pair: --sl-color-accent (#4eb5a6) on --sl-color-gray-5 (#2e2e2a) = 5.51:1 (AA-large pass)
pair: --sl-color-accent (#4eb5a6) on --sl-color-gray-6 (#1a1a17) = 7.05:1 (AA pass)
pair: --sl-color-accent (#4eb5a6) on --fleans-surface (#141411) = 7.46:1 (AA pass)
```

**Light theme**

```
pair: --sl-color-white (#0a0a0a) on bg (#f5f5f0) = 18.10:1 (AA pass)
pair: --sl-color-accent (#1f6357) on bg (#f5f5f0) = 6.44:1 (AA pass)
pair: --sl-color-accent-high (#175046) on --sl-color-accent-low (#d4f0eb) = 7.69:1 (AA pass)
pair: --sl-color-accent (#1f6357) on --sl-color-accent-low (#d4f0eb) = 5.86:1 (AA pass)
pair: --sl-color-gray-3 (#5a5a53) on bg (#f5f5f0) = 6.35:1 (AA pass)
pair: --fleans-accent-2 (#3a7d00) on bg (#f5f5f0) = 4.67:1 (AA-large pass)
pair: --sl-color-accent (#1f6357) on --sl-color-gray-5 (#b8b8b1) = 3.53:1 (AA-large pass)
pair: --sl-color-accent (#1f6357) on --sl-color-gray-6 (#e0e0d9) = 5.31:1 (AA pass)
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

---

## Spatial Composition

> **Stub** — hero layout and spatial decisions are documented in issue #259. This section will be expanded after PR #259 merges.

The landing hero is designed around a brutalist-technical layout: asymmetric grid, large typographic anchors, and a BPMN diagram rendered as visual art rather than a screenshot.

## Motion

CSS-only motion and hover effects — no JavaScript. Implemented in [#260](https://github.com/nightBaker/fleans/issues/260).

### Principles

1. **Motion is progressive enhancement.** Every animation is inside `@media (prefers-reduced-motion: no-preference)`. Users who prefer reduced motion see the final state instantly — no information is lost.
2. **Hover effects require a pointer.** Hover rules live in `@media (hover: hover)`. `:focus-visible` equivalents are always active (outside the media query) so keyboard users get the same visual feedback.
3. **No JavaScript, no intersection observers.** Scroll-driven animations use the CSS `animation-timeline: view()` spec behind an `@supports` guard — browsers without support see the static fallback (accent markers always visible).

### Three moments

| Moment | Technique | Timing |
|---|---|---|
| **Hero entrance** | `fade-up-clip` stagger (title → tagline → actions) + `clip-reveal-x` on logo | 320–420ms per element, 60–380ms stagger delay, `cubic-bezier(.2,.8,0,1)` (sharp-out) |
| **Scroll accents** | Left-border `border-draw` on `.card`, underline `underline-draw` on `h2::after` | Tied to `animation-timeline: view()`, entry 0–40% |
| **Hover / focus** | Sidebar lime `>` caret + 4px text shift; `.site-title` underline wipe; `.card` lift + hard lime shadow; primary action underline wipe; copy-button blinking lime dot | 120–180ms transitions |

### Keyframes

| Name | From | To | Notes |
|---|---|---|---|
| `fade-up-clip` | `translateY(8px)` + `clip-path: inset(100% 0 0 0)` | `translateY(0)` + `inset(0)` | Hero stagger |
| `clip-reveal-x` | `clip-path: inset(0 100% 0 0)` | `inset(0)` | Logo reveal |
| `underline-draw` | `scaleX(0)` | `scaleX(1)`, origin left | Scroll + hover underlines |
| `caret-slide` | `translateX(-8px)` + opacity 0 | `translateX(0)` + opacity 1 | Sidebar hover (via transition, keyframe reserved) |
| `border-draw` | `scaleY(0)` | `scaleY(1)`, origin top | Scroll-driven card border |
| `cursor-blink` | opacity 1 | opacity 0, `steps(2)` | Copy-button lime dot |

### Accessibility

- All keyframe animations are inside `@media (prefers-reduced-motion: no-preference)`.
- `cursor-blink` is frozen (`animation: none !important`) under `@media (prefers-reduced-motion: reduce)`.
- Hover effects gated on `@media (hover: hover)`; `:focus-visible` mirrors are outside the media query so they always apply for keyboard navigation.
- `will-change: transform, clip-path` is scoped to `.hero > *` only — no blanket GPU promotion.
