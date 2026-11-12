# Admin UI (`Fleans.Web`) conventions

Read before editing any `.razor` file or anything under `src/Fleans/Fleans.Web/`. Editor-specific invariants (multi-tab, plugin-switch dialog, properties panel) live in [`bpmn-editor.md`](bpmn-editor.md).

## Data access

The admin UI calls Orleans grains directly via the `WorkflowEngine` service, not through HTTP. Back new admin-UI features with grain calls; the REST API is for external callers only.

The one exception is **other environments** (#900, `Services/EnvironmentClient.cs`): another cluster is reachable only through its REST API, so the Admin UI `POST`s a version's BPMN to the target's `/Deployments/requests` (promotion, `Components/Shared/PromoteDialog.razor`) and reads its `/Definitions/latest` and pending `/Deployments/requests` (the environments board, `Pages/EnvironmentsBoard.razor`). It never calls its own cluster's API.

The second exception is the **editor's AI assistant backend** (#1015, `Services/HttpAssistantClient.cs`): a service outside the cluster, reachable only over HTTP + server-sent events (an AG-UI agent at `POST {Fleans:Assistant:Endpoint}/assistant/agent`, #1054, plus `/assistant/capabilities`, `/assistant/conversations` (with its `/events` feed) and `/assistant/jobs` for backends that store chats, #1017; contract in `Fleans.Assistant.Contracts`). Its named `HttpClient` drops the service-defaults resilience handler (a chat call is long and not idempotent — no retries, no 10 s attempt timeout; `Fleans:Assistant:Timeout` bounds the whole stream) and, with auth on, forwards the user's access token (`AssistantTokenHandler`; `SaveTokens` is on only while an endpoint is configured). Everything else the panel needs — plugin catalog, BPMN validation — still comes from grains and `IBpmnConverter`. Editor-side rules: [bpmn-editor.md](bpmn-editor.md#ai-assistant-panel-1015).

The optional **Knowledge tab** (#1025, `Pages/Knowledge*.razor`) reads the same backend's `api/ai/knowledge/*` through `HttpAssistantClient` and holds no data itself. `KnowledgeAccess` (scoped, so per user) decides whether it exists: endpoint configured **and** `knowledge` in the capabilities. Anything else means no nav entry and `/knowledge` renders `NotFound`. `NavMenu` is on every page, so it asks after the first render, never during prerender, and a failure only hides the tab. Repository content is untrusted: `(MarkupString)` is allowed only on `KnowledgeView.MarkdownToHtml`, which escapes raw HTML and keeps only `http(s)`/`mailto` links. The same goes for the assistant panel's approval details, which can quote repository content.

Reads that the read model already serves go through `IWorkflowQueryService` (the database), as `ProcessInstance.razor` does. The instance page's **debug mode** (`InstanceDebugger.razor`) reads the instance's journal through the host's `IEventStore` and replays it in the circuit (`InstanceJournal`, see [persistence.md](persistence.md#event-store-journal-snapshots-read-model)), so viewing old steps never activates the grain. The Web host gets the same persistence settings as the silos (`fleans.commonEnv` in the chart), including `ConnectionStrings:fleans-journal`.

**Acting user** (`Services/ActingUser.cs`, scoped per circuit): who requests or reviews a deployment and their groups — the signed-in user and its `Authentication:GroupsClaim` claims with auth on, a name and groups typed once per session with auth off. Reuse it for any other feature that records a user.

## Orbit look (`wwwroot/css/orbit.css`)

The shell (`MainLayout` side rail and top bar, `NavMenu` section links, `PageHeader` eyebrow + title), the editor chrome and the default diagram style all come from one token set:

- **Tokens are `--o-*` on `body`**: light values by default, dark under `body[data-theme$="dark"]` (Fluent writes `data-theme`). Use them for any new surface instead of hard-coded colours.
- **The accent is the Fluent accent** (`--o-accent: var(--accent-fill-rest)`, `--o-accentink: var(--accent-foreground-rest)`), so Settings → Accent Color recolours Orbit and Fluent alike. Soft/line/glow variants are `color-mix` of it.
- **Fluent neutral tokens are re-pointed at the Orbit palette on `.orbit-app`** (the layout wrapper), not on `body` — FAST may write its root tokens inline on the document/body, and for inherited custom properties the nearest ancestor wins. Components that set their own fill (e.g. `fluent-card`) override locally; see the "Fluent surfaces" rules.
- **UI themes** (Settings → UI Theme): `orbit` (default) and `bcchub` (`wwwroot/css/bcchub.css`, from the BCC HUB brand guidelines). `js/orbit.js` (`window.fleansUiTheme`, `localStorage["fleans.uiTheme"]`) writes `<html data-ui-theme>` before first paint — on `<html>`, not `body`, because the font tokens live on `:root`. A UI theme only re-points `--o-*` tokens (light and `body[data-theme$="dark"]`) plus Fluent's `--accent-*` tokens on `.orbit-app`. Because `--o-accent` resolves on `body` from Fluent's accent, a brand theme must set `--o-accent` itself, so `bcchub` fixes the accent and Settings disables Accent Color. Brand marks: `MainLayout` renders both the Fleans mark (`.brand-orbit`) and `BccHubLogo` (`.brand-bcchub`, vector art from the guide); CSS shows one of them. `bcchub` also removes Orbit's uppercase captions (the guide forbids caps) and sets buttons in the mono font (`Formular Neutral Mono`, falling back to JetBrains Mono). A new uppercase caption class needs adding to that list.
- Page content inside the standard `FluentStack` gets the 24px Orbit gutter from `.orbit-main > .stack-vertical > :not(.orbit-page-header)`; a page that needs full bleed (the editor) renders its own root instead of a `FluentStack`.
- E2E hooks on the shell: `data-testid="nav-<section>"` on nav links, `data-testid="nav-help"`, `data-testid="theme-toggle"`, and the rail's `a[href='/settings']` link.
- **Side rail** (`.orbit-rail`, #1027): brand, sections and, pinned to the bottom, Settings and Help; wordmark, user and theme toggle stay in the top bar. Items stack a 20px icon over a 10px label. The rail is 88px rather than the design's 72px because the design's 60px item clips the Russian labels; with a wider fallback font a label breaks inside its item rather than spilling out (`NavigationLocalizationTests` checks this with enlarged labels). At ≤768px it becomes a sideways-scrolling strip above the top bar. Editor modes stay in the editor's own header, not in the rail.
- **Status bar** (`.editor-status`, the editor's and instance page's footer): 10px high including its border, with 8px text on one line. It doesn't wrap, so what doesn't fit a narrow window is clipped at the right edge.

## Localization (#971)

The Web host registers `IStringLocalizer` with `ResourcesPath = "Resources"` and
`RequestLocalization` for `en` / `ru`. English is the default; only the standard
`.AspNetCore.Culture` cookie selects another culture. Browser `Accept-Language` and query-string
providers are intentionally disabled. Do not change process-wide default cultures to select
a user's language: users have independent HTTP requests and Blazor circuits.

`Components/Shared/CultureSelector.razor` reads the supported UI cultures from the middleware's
options. It navigates through `/Culture/Set`, which validates the culture, sets a one-year
HttpOnly preference cookie, and redirects locally. The full navigation creates a circuit in
the selected culture; setting a cookie from a running server-side component would be too late
to write response headers. The HTML `lang` follows the request UI culture.

**Resources follow the component namespace.** For `Components/Pages/Settings.razor`, use
`Resources/Components/Pages/Settings.resx` (neutral English) and `Settings.ru.resx`, and inject
`IStringLocalizer<Settings>`. Keep semantic keys stable (`Page.Title`, `Field.ThemeMode`,
`Error.DiagramThemeStorage`); changing the English wording must not rename the key. Add new
strings to the neutral file first. `NeutralLanguage=en` and the standard resource fallback
let a missing Russian key resolve to English without a custom localizer. Brand names such as
Orbit / Archify / Excel intentionally use this fallback.

Settings and the shared shell (`MainLayout` / `NavMenu`) are localized. The shell's EN/RU
resources live under `Resources/Components/Layout/`: translate link text, accessible names,
tooltips and formatted signed-in-user messages, while keeping routes and test ids unchanged.
`Custom Tasks` is `Обработчики задач` in Russian, avoiding confusion with human User Tasks.
`NavigationLocalizationTests` covers both languages and 1280 / 768 / 390 px layouts, and the
auth leg checks the translated user name, avatar title and sign-out button after switching to Russian.

Settings, Workflows, ProcessInstances, and the shared promotion dialog and PageHeader have
English/Russian resources.
Remaining pages and bpmn-js translation are follow-ups. Workflows uses localized pagination
controls because Fluent UI 4.14's paginator has hardcoded English navigation labels. Its
server-side sort uses column identity rather than a translated column title.
The table fills its available width. Version, date, and actions columns size to their content;
the process key uses the remaining space. Actions stay on one line, and narrow viewports scroll
the table horizontally.
Process keys, business IDs, environment names, status values, and persisted promotion audit
notes remain unchanged. Deployment timestamps remain UTC, with culture-specific display and
an invariant HTML `datetime` value. ProcessInstances follows the same sorting, pagination,
and timestamp conventions. Its table fills the available width, sizes date/status/action
columns to content, and scrolls horizontally on narrow screens. Instance GUIDs are unchanged.
Persisted values (`Dark`, `Excel`, `classic`, etc.) are identifiers, never translated;
translate only the option text. Format human-facing dates/numbers using the current request
culture, but keep wire formats and storage identifiers invariant. Regression coverage lives in
`LocalizationTests` (cookie persistence/isolation, English fallback, endpoint validation, theme
preservation and localized storage errors), `SettingsPageTests`, `NavigationLocalizationTests`,
and `WorkflowsLocalizationTests`
(actions verified against the API, dates, identifiers, pagination, empty/search states, and
non-overlapping action buttons at desktop/tablet widths in both languages).
`ProcessInstancesLocalizationTests` covers EN/RU status labels, dates, identifiers, version
routes, sorting, pagination, empty state, and layout at 768/1280/1920 px.

The Custom Tasks catalog, typed parameter-editor hints, and the properties panel's plugin
selector/replacement dialog also use EN/RU resources. Other properties-panel sections remain
separate localization work. Plugin-provided display names, descriptions, defaults, task types,
silo names, parameter targets, and BPMN expressions are not translated. The catalog's resize
controls use `ColumnResizeLabels`; headers open those controls without the grid's hardcoded
English options-button label. `UseMenuService=false` keeps the grid's menus inline without
requiring a layout-wide `FluentMenuProvider`. `CustomTaskCatalogLocalizationTests` waits for
and checks the shared fixture's gRPC worker catalog and unregistered-type warning;
`CustomTaskPluginLocalizationTests` covers the registered `e2e-probe` catalog, resize labels,
and parameterless editor on the split-role topology. The fixture starts a gRPC worker in every
topology, so these tests must not assume an empty catalog. Empty catalog and disabled-selector
localization are covered by Scenario 8 in `tests/manual/38-custom-task-editor/test-plan.md`,
against a cluster without workers.

## Fluent UI Blazor

- Use only components that exist in <https://www.fluentui-blazor.net/> (e.g. `FluentBodyText` does not exist — plain text goes in `<p>`/`<span>`).
- `FluentButton` icons go via `IconStart`/`IconEnd`; `<FluentIcon>` as child content is wrong.
- `FluentButton` spinner states use the `Loading` parameter (it replaces the icon with a progress ring).
- `FluentTabs` is a trap here: its overflow menu needs a `<FluentMenuProvider />` the layout doesn't have, and the first overflow kills the circuit. `Knowledge.razor` uses a plain `role="tablist"` bar instead.

## JS-initialised elements (bpmn-js canvases)

Initialise a JS widget from `OnAfterRenderAsync` only once the element it targets is in the rendered markup — gate on the same state the markup branches on (e.g. `!isLoading`), not just on the data being loaded. An after-render of the "Loading…" pass can run while `OnInitializedAsync` is still awaiting, with the data already assigned but the container not rendered yet; `bpmnViewer.init` then finds no container, and a "done once" flag blocks every retry (`ProcessInstance.razor` showed a blank diagram after in-app navigation this way).

## Cache-bust local Razor host assets

Every `<script>`/`<link>` in `App.razor` (or any other Razor host file) that references an asset under `wwwroot/` MUST use `@Assets["..."]` so the URL carries a content hash. Externally-versioned CDN URLs (e.g. `https://unpkg.com/bpmn-js@17.11.1/...`) are exempt — the path already encodes the version — and so is the Google Fonts stylesheet for Inter / JetBrains Mono (not a local asset; offline, the font stacks fall back to system fonts).
