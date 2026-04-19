# Fleans Website Design

Design decisions, rationale, and guidelines for the Fleans documentation website.

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

3. **Dark-first, light-correct** — Almost all dev-tool sites are designed light with a dark mode bolted on. Fleans inverts this. The dark theme is the reference; light is the polite alternative. The color palette (deep space → electric blue accent) is built to be extraordinary in dark mode and merely clean in light.

### Light Mode Constraint

Light mode uses the same type scale, spacing, and layout as dark mode. **Only surface colors and accent lightness change.** The constraint: light mode must read as *specification-grade clean* — white paper with ink, not *bright app*. Concretely:

- Backgrounds stay in the off-white range (≈ `#f5f6f8`), no colored gradients
- No decorative fills that appear only in light mode
- Accent uses a darker shade of the same electric blue used in dark mode (≈ 20–30% darkened for contrast on white)
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

> **Stub** — full color palette decisions are documented in issue #258 and codified in `website/src/styles/custom.css`. This section will be expanded after PR #258 merges.

The site uses a dark-first palette: deep space backgrounds with an electric blue accent. Light mode applies the same accent at ≈ 20–30% darker lightness for contrast on off-white surfaces (see [Light Mode Constraint](#light-mode-constraint) above).

---

## Spatial Composition

> **Stub** — hero layout and spatial decisions are documented in issue #259. This section will be expanded after PR #259 merges.

The landing hero is designed around a brutalist-technical layout: asymmetric grid, large typographic anchors, and a BPMN diagram rendered as visual art rather than a screenshot.
