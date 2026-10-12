# Admin UI (`Fleans.Web`) conventions

Read before editing any `.razor` file or anything under `src/Fleans/Fleans.Web/`. Editor-specific invariants (multi-tab, plugin-switch dialog, properties panel) live in [`bpmn-editor.md`](bpmn-editor.md).

## Data access

The admin UI calls Orleans grains directly via the `WorkflowEngine` service, not through HTTP. Back new admin-UI features with grain calls; the REST API is for external callers only.

## Fluent UI Blazor

- Use only components that exist in <https://www.fluentui-blazor.net/> (e.g. `FluentBodyText` does not exist — plain text goes in `<p>`/`<span>`).
- `FluentButton` icons go via `IconStart`/`IconEnd`; `<FluentIcon>` as child content is wrong.
- `FluentButton` spinner states use the `Loading` parameter (it replaces the icon with a progress ring).

## Cache-bust local Razor host assets

Every `<script>`/`<link>` in `App.razor` (or any other Razor host file) that references an asset under `wwwroot/` MUST use `@Assets["..."]` so the URL carries a content hash. Externally-versioned CDN URLs (e.g. `https://unpkg.com/bpmn-js@17.11.1/...`) are exempt — the path already encodes the version.
