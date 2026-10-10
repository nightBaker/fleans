# Fleans Website Design

Design decisions, rationale, and guidelines for the Fleans documentation website (landing page + every docs page). Tokens live in `src/styles/custom.css`; this file explains them.

## Aesthetic Direction

**Reference:** [The Saddle Framework](https://saddle-framework.webflow.io) ([Made in Webflow listing](https://webflow.com/made-in-webflow/website/the-saddle-framework)). The site borrows its visual grammar — not its content or branding: flat warm-neutral surfaces, one electric-blue accent, oversized light-weight grotesk headings, monospace "class-name" chips, and hairline-bordered note cards.

This supersedes the earlier "brutalist-technical" direction (teal accent, IBM Plex / Space Grotesk, dot-matrix noise, scanlines, code-block corner brackets, Three.js silo hero — issues #257–#261, #327). Those were removed wholesale rather than layered under the new look.

### Audience

Backend .NET engineers and architects who:
- Already know what BPMN is or are willing to learn it
- Are evaluating whether Fleans replaces Camunda, Temporal, or a home-grown state machine
- Work primarily in dark IDEs and terminals
- Distrust tools that hide complexity; respect tools that are honest about it

### Tone

**Precise. Calm. Confident without being loud.** A well-set specification document: generous whitespace, few colors, type doing the work. Code is always shown, never described.

### Principles

1. **Type is the decoration.** Headings are large and *light* (weight 400, negative tracking), never bold. Hierarchy comes from size and color (text vs. muted), not weight.
2. **One accent.** Electric blue is the only saturated color in site chrome. It marks actions (primary button), code identifiers (chips), and links. A violet secondary appears only as the second chip in a chip pair.
3. **Hairlines, not fills.** Structure comes from 1px borders in `--fl-stroke` and 10px radii. No shadows, gradients, textures, or glows.
4. **Dark-first, light-equal.** Dark is the reference; light uses the same layout and scale with inverted neutrals. Neither theme has decoration the other lacks.
5. **Diagrams carry the color.** Archify diagrams keep their semantic node palette; the site re-skins only their canvas so they sit on the page's own background.

### Rejected alternatives

| Alternative | Why rejected |
|---|---|
| Keep the teal brutalist theme | Visual noise (scanlines, noise, brackets, lime hovers) competed with content; the 3D hero cost ~1.6k lines of Three.js for a background. |
| Vercel-style gradient-dark | Signals consumer SaaS, not infrastructure. |
| Stock Starlight | Indistinguishable from hundreds of docs sites. |

---

## Typography

| Role | Family | Weights | Package |
|---|---|---|---|
| Everything (headings, body, UI) | **Geist Sans** | 300, 400, 500 | `@fontsource/geist-sans` |
| Code, chips, table headers, labels | **JetBrains Mono** | 400, 500 | `@fontsource/jetbrains-mono` |

Saddle uses Neue Montreal (commercial). Geist Sans is the closest open-licence neo-grotesk: same tight apertures and flat terminals, and it reads well at both 120px and 15px. JetBrains Mono is what Saddle itself uses for chips.

### Scale

| Element | Size | Weight | Tracking |
|---|---|---|---|
| Landing hero title | `clamp(3.5rem, 9vw, 7.5rem)` | 400 | −0.04em |
| Landing hero lead | `clamp(1.375rem, 2.6vw, 2rem)` | 400 | −0.01em |
| Landing section `h2` | `clamp(2.5rem, 6vw, 4rem)` | 400 | −0.03em |
| Docs page title (`h1#_top`) | `clamp(2.5rem, 6vw, 4.25rem)` | 400 | −0.035em |
| Docs `h2` / `h3` / `h4` | `~2.5rem` / `1.625rem` / `1.25rem` | 400 / 400 / 500 | −0.03 … −0.02em |
| Docs body | `1.0625rem`, line-height 1.65 | 400 | 0 |
| Labels (TOC heading, table `th`, aside titles) | `0.75–0.8125rem` mono, uppercase | 400–500 | +0.06em |

`strong` is weight 500 in `--fl-text` — emphasis by color, not heaviness.

---

## Color & Theme

All site colors are `--fl-*` tokens in `custom.css`; Starlight's `--sl-color-*` tokens are mapped onto them so built-in components follow automatically.

| Token | Dark | Light | Role |
|---|---|---|---|
| `--fl-bg` | `#1b1818` | `#ffffff` | Page, header, sidebar background |
| `--fl-surface-2` | `#221f1f` | `#f7f6f6` | Code blocks, asides, hover fill |
| `--fl-surface-3` | `#2c2828` | `#eceaea` | Code frame tab bar / title bar |
| `--fl-stroke` | `#3a3636` | `#e2e0e0` | Card borders, sidebar card, table rules |
| `--fl-hairline` | `#2c2828` | `#eceaea` | Header rule on scroll, section dividers |
| `--fl-text` | `#fafafa` | `#1b1818` | Headings, active nav, strong |
| `--fl-text-muted` | `#aba9a9` | `#5b5757` | Body prose |
| `--fl-text-faint` | `#878484` | `#757171` | Sidebar/TOC links, captions |
| `--fl-accent` | `#8aa4ff` | `#2652e1` | Link text (lightened on dark for AA) |
| `--fl-chip-bg` | `#2652e1` | `#2652e1` | Inline-code chips, primary button |
| `--fl-chip-alt-bg` | `#9b3ed6` | `#8a2fc4` | Second chip in a pair |
| `--fl-blue` | `#3e6cff` | `#3e6cff` | Focus ring, selection, active code tab marker |

The Saddle blue `#3e6cff` is only 4.3:1 under white text, so filled surfaces (chips, buttons) use the deeper `#2652e1` (Saddle's own hover shade).

### Contrast matrix (verified)

Run `node scripts/check-contrast.mjs` after any token change (exits non-zero on failure); `npm run check:contrast` checks the rendered hero in both themes.

```
--- dark ---
--fl-text        on --fl-bg         = 16.90:1
--fl-text-muted  on --fl-bg         =  7.54:1
--fl-text-muted  on --fl-surface-2  =  6.99:1
--fl-text-faint  on --fl-bg         =  4.76:1
--fl-accent      on --fl-bg         =  7.42:1
--fl-accent      on --fl-surface-2  =  6.88:1
#fff on --fl-chip-bg                =  6.22:1
#fff on --fl-chip-alt-bg            =  5.18:1
--- light ---
--fl-text        on --fl-bg         = 17.64:1
--fl-text-muted  on --fl-bg         =  7.13:1
--fl-text-muted  on --fl-surface-2  =  6.61:1
--fl-text-faint  on --fl-bg         =  4.82:1
--fl-accent      on --fl-bg         =  6.22:1
--fl-accent      on --fl-surface-2  =  5.77:1
#fff on --fl-chip-bg                =  6.22:1
#fff on --fl-chip-alt-bg            =  6.39:1
```

---

## Components

| Piece | File | Notes |
|---|---|---|
| Header | `src/components/Header.astro` | Wordmark · flat text nav (Guides / Concepts / Reference, current section in `--fl-text`) · pill search · theme switch · "GitHub". No bottom border until the page scrolls (CSS `animation-timeline: scroll()`). |
| Theme switch | `src/components/ThemeSelect.astro` | Pill toggle replacing Starlight's `<select>`. Same `localStorage['starlight-theme']` contract, so Starlight's FOUC guard and the Archify iframe sync keep working. No "auto" state in the UI: until the user clicks, the system preference applies. |
| Landing hero | `src/components/Hero.astro` | Text column (chips eyebrow → oversized title → lead → pill actions → "made for … by …" row) left, hero card video right (≥64rem; stacked below that). Fills the first viewport. Driven by `hero` frontmatter in `index.mdx`. |
| Landing section | `src/components/Section.astro` | Large light `h2` + muted intro slot. |
| Spec row | `src/components/SpecRow.astro` | Saddle styleguide row: chips (left, 11rem) · `h3` + body · bordered note card (right, 17.5rem) with optional "Learn how →". Single column under 60rem. In MDX, leave a blank line before `<Fragment slot="note">` — otherwise MDX wraps it in the body paragraph and the slot is lost. |
| Footer | `src/components/Footer.astro` | Licence line + Releases / Issues / GitHub. |

### Docs-page treatments (all in `custom.css`)

- **Sidebar** — a bordered 10px-radius card inset from the viewport edge (≥50rem). Group labels in `--fl-text`; links in `--fl-text-faint`; nested items hang off a 1px tree line that turns solid `--fl-text` beside the current page.
- **Inline code** — solid blue mono chip with white text (the Saddle signature). Chips wrap only at the cell edge (`overflow-wrap: break-word`), so wide tables scroll horizontally instead of splitting identifiers.
- **Code blocks** — ExpressiveCode `styleOverrides` in `astro.config.mjs` point at `--fl-surface-2/3` and `--fl-stroke`, so frames flip with the theme; syntax colors stay Starlight's defaults.
- **Asides** — `--fl-surface-2` card with hairline border and a mono uppercase title in the variant color.
- **Tables** — no fills or zebra; hairline row rules; mono uppercase headers.
- **Tabs** — active tab is an outline pill.
- **Pagination / cards** — hairline cards, `--fl-surface-2` on hover.

### Archify diagrams

`public/diagrams/*.html` are generated artifacts embedded via `<iframe data-arch-src>`. A head script in `astro.config.mjs` (a) keeps their `?theme=` in sync with the site and (b) injects a `#fl-skin` style into each same-origin iframe overriding `--bg`, `--grid`, `--mask`, `--panel` with the site neutrals. Regenerating a diagram therefore needs no hand edits. Node colors are left alone.

---

## Spatial composition

- **Landing** — full-bleed content width (`--sl-content-width: 90rem` under `[data-has-hero]`), 2.5rem gutters matching the header. Sections are separated by `--fl-hairline` rules and 6rem of top padding; spec rows by `--fl-stroke` rules.
- **Docs** — 20rem sidebar, 48rem content column, right-rail TOC with a single hairline. Header height 5.5rem on desktop.
- **Radii** — 4px (chips, kbd), 10px (cards, code, asides, sidebar), full (buttons, search, tabs, switch).

## Motion

Minimal and CSS-only except the theme switch.

| Moment | Technique |
|---|---|
| Hero entrance | Title → lead → actions rise 12px and fade, 600ms, 90ms stagger |
| Hero scroll stage | ≥64rem without reduced motion: the hero is ~2 viewports tall with its content pinned (`position: sticky`); as you scroll, the card grows from its slot to the full viewport under the header (ease-out cubic, radius → 0) and the text fades. The card background is the video's own `#0e0f13` and has no border in dark theme, so card and video read as one surface. |
| Hero card video | `public/hero/`: silent 21s loop, 1600×1000, AV1 (~600 KB) with H.264 fallback (~780 KB), `+faststart`; poster is the blank first frame. Pauses off-screen; under `reduce` it doesn't autoplay and shows the final frame (`fleans-hero-still.jpg`) with controls. Re-encode a new source with `ffmpeg -an -vf scale=1600:-2 -c:v libsvtav1 -preset 4 -crf 38` / `-c:v libx264 -preset veryslow -crf 26 -tune animation -movflags +faststart`. |
| Header rule | Border fades in over the first 4rem of scroll (`animation-timeline: scroll(root)`, behind `@supports`) |
| Hover | 120–200ms color / border / background transitions; "→" nudges 3px in note links |
| Theme switch | Knob slides 220ms |

Every keyframe animation sits inside `@media (prefers-reduced-motion: no-preference)`; the switch drops its transition under `reduce`. `:focus-visible` always shows a 2px `--fl-blue` ring.
