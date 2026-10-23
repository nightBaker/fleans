# Admin UI (`Fleans.Web`) conventions

Read before editing any `.razor` file or anything under `src/Fleans/Fleans.Web/`. Editor-specific invariants (multi-tab, plugin-switch dialog, properties panel) live in [`bpmn-editor.md`](bpmn-editor.md).

## Data access

The admin UI calls Orleans grains directly via the `WorkflowEngine` service, not through HTTP. Back new admin-UI features with grain calls; the REST API is for external callers only.

## Orbit look (`wwwroot/css/orbit.css`)

The shell (`MainLayout` top bar, `NavMenu` pill nav, `PageHeader` eyebrow + title), the editor chrome and the default diagram style all come from one token set:

- **Tokens are `--o-*` on `body`**: light values by default, dark under `body[data-theme$="dark"]` (Fluent writes `data-theme`). Use them for any new surface instead of hard-coded colours.
- **The accent is the Fluent accent** (`--o-accent: var(--accent-fill-rest)`, `--o-accentink: var(--accent-foreground-rest)`), so Settings → Accent Color recolours Orbit and Fluent alike. Soft/line/glow variants are `color-mix` of it.
- **Fluent neutral tokens are re-pointed at the Orbit palette on `.orbit-app`** (the layout wrapper), not on `body` — FAST may write its root tokens inline on the document/body, and for inherited custom properties the nearest ancestor wins. Components that set their own fill (e.g. `fluent-card`) override locally; see the "Fluent surfaces" rules.
- Page content inside the standard `FluentStack` gets the 24px Orbit gutter from `.orbit-main > .stack-vertical > :not(.orbit-page-header)`; a page that needs full bleed (the editor) renders its own root instead of a `FluentStack`.
- E2E hooks on the shell: `data-testid="nav-<section>"` on nav links, `data-testid="theme-toggle"`, and the `a[href='/settings']` gear link.

## Fluent UI Blazor

- Use only components that exist in <https://www.fluentui-blazor.net/> (e.g. `FluentBodyText` does not exist — plain text goes in `<p>`/`<span>`).
- `FluentButton` icons go via `IconStart`/`IconEnd`; `<FluentIcon>` as child content is wrong.
- `FluentButton` spinner states use the `Loading` parameter (it replaces the icon with a progress ring).

## Cache-bust local Razor host assets

Every `<script>`/`<link>` in `App.razor` (or any other Razor host file) that references an asset under `wwwroot/` MUST use `@Assets["..."]` so the URL carries a content hash. Externally-versioned CDN URLs (e.g. `https://unpkg.com/bpmn-js@17.11.1/...`) are exempt — the path already encodes the version — and so is the Google Fonts stylesheet for Space Grotesk / JetBrains Mono (not a local asset; offline, the font stacks fall back to system fonts).
