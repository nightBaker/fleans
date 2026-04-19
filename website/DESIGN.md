# Fleans Website Design

Design decisions, rationale, and guidelines for the Fleans documentation website.

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
