# Documentation conventions

## Two doc surfaces, two audiences

The two surfaces have non-overlapping audiences and MUST stay separated:

- **`README.md` + `CLAUDE.md` + `docs/` (this repo)** — for contributors working with the Fleans **source tree**. Covers cloning, building, running tests, internal architecture, design docs (`docs/plans/`), source-level conventions (`docs/conventions/`), maintainer runbooks (`docs/runbooks/`), manual test plans. Anything that assumes the reader has `git clone`d this repo belongs here.
- **`website/src/content/docs/` (the public docs site)** — for users consuming a **released** Fleans artifact: pulling container images, running the Helm chart or compose bundle, calling the REST API, and extending the engine via published NuGet plugin packages. Anything that should still make sense to a reader who has never seen the source tree belongs here. "Writing custom-task plugins" lives here because plugin authors consume `Fleans.Worker` from nuget.org, not from source.

When deciding where a new doc belongs, ask: *does it require working in the source checkout, or does it only require a released artifact?* Source-checkout docs → repo. Released-artifact docs → website. Quick Start on the website MUST use a released artifact (container pull, Helm install) — not `git clone && dotnet run`, which is a source-tree workflow.

The CI guard at `.github/workflows/website-pin-guard.yml` enforces that website docs don't introduce source-code line pins or `src/Fleans/` paths.

## Documentation website mechanics

The public docs site lives in `website/` — an Astro + Starlight project deployed to GitHub Pages via `.github/workflows/deploy-website.yml` (Node 22, deploy job gated to `main`).

- Content: `website/src/content/docs/` (`guides/`, `concepts/`, `reference/`, `index.mdx`)
- Theme: `website/src/styles/custom.css` — palettes scoped to `:root[data-theme='light']` and `:root[data-theme='dark']`
- Local dev: `cd website && npm install && npm run dev`
- Build check: `npm run build` (must pass before merging)

For website-build infrastructure (Hero BPMN diagram regeneration, 3D landing background, load-test publishing rule), see [`website/README.md`](../../website/README.md).

## Tabs for interchangeable approaches

When a page documents two or more **interchangeable** tools or commands that produce the same outcome (e.g. `gh release download` vs `curl -LO`, `docker compose up` vs `helm install`, `apt` vs `brew`), use Starlight's [`<Tabs>`](https://starlight.astro.build/components/tabs/) component instead of stacking *"Or, without X:"* sections — keeps the page compact and lets the reader focus on the path they actually use.

Conventions:

- Pages that import components must use the `.mdx` extension. Add `import { Tabs, TabItem } from '@astrojs/starlight/components';` after the frontmatter.
- Set a `syncKey` so the tool choice persists across pages (e.g. `<Tabs syncKey="release-download-tool">` on every gh-vs-curl block, `syncKey="package-manager"` on every npm-vs-yarn block).
- **Don't use tabs for non-interchangeable content** — dev vs prod configs, SQLite vs PostgreSQL, compose vs Helm are *different workflows* the reader needs to see both of, not a one-of-two choice.
