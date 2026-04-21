# Fleans design doc

## Overview

Design doc for the Fleans marketing + docs site. Sections owned by individual design issues.

## Color palette

Chosen direction: **Candidate 1 — Brutalist-technical** (owner decision on [#258](https://github.com/nightBaker/fleans/issues/258)). The site signals a workflow engine for backend developers — industrial orange on near-black reads like CLI output and industrial signage, not like default Starlight with a blue tint.

Owner decisions fixed up front:

1. Aesthetic direction: Candidate 1 — brutalist-technical.
2. Per-candidate token completeness: option (a) — full token map for every Starlight token plus the two `--fleans-*` tokens, both themes.
3. `--fleans-accent-2` usage: contract-only in this PR; real usage deferred to [#259](https://github.com/nightBaker/fleans/issues/259) / [#260](https://github.com/nightBaker/fleans/issues/260).

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
