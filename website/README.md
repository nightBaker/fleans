# Fleans Website

Astro + Starlight site for Fleans. Landing page + documentation.

## Local development

Requires Node.js 20+:

```bash
brew install node   # macOS, if not installed
cd website
npm install
npm run dev
```

Open the URL printed by Astro (usually `http://localhost:4321/fleans/`).

## Build

```bash
npm run build
npm run preview
```

## Deployment

Pushes to `main` that touch `website/**` trigger `.github/workflows/deploy-website.yml`,
which builds the site and deploys it to GitHub Pages.

Before the first deploy:

1. In GitHub repo Settings → Pages, set **Source** to **GitHub Actions**.
2. Update `astro.config.mjs`:
   - Set `site` to your actual `https://<github-user>.github.io`
   - Set `base` to `/<repo-name>` (likely `/fleans`)
   - Update the GitHub social link
3. Update `<github-user>` placeholders in `src/content/docs/` markdown files.

## Structure

```
website/
├── astro.config.mjs          # Starlight config (sidebar, branding, deploy base)
├── src/
│   ├── assets/hero.svg       # Landing page hero illustration
│   ├── content/docs/         # All docs pages
│   │   ├── index.mdx         # Landing page (splash template)
│   │   ├── guides/           # Getting Started section
│   │   ├── concepts/         # Architecture, BPMN support
│   │   └── reference/        # API reference
│   ├── content.config.ts     # Starlight content collection
│   └── styles/custom.css     # Brand color overrides
└── public/favicon.svg
```
