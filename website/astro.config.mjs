import { defineConfig } from 'astro/config';
import starlight from '@astrojs/starlight';

// https://astro.build/config
export default defineConfig({
  site: 'https://nightBaker.github.io',
  base: '/fleans',
  integrations: [
    starlight({
      title: 'Fleans',
      description: 'BPMN Workflow Engine on Orleans — Camunda on .NET Orleans',
      logo: {
        src: './src/assets/logo.svg',
        replacesTitle: false,
      },
      favicon: '/favicon.svg',
      head: [
        // Archify diagrams (public/diagrams/*.html) are embedded as iframes with
        // `data-arch-src`; keep their `?theme=` in sync with Starlight's theme toggle.
        {
          tag: 'script',
          content: `(() => {
  const sync = () => {
    const theme = document.documentElement.dataset.theme === 'light' ? 'light' : 'dark';
    document.querySelectorAll('iframe[data-arch-src]').forEach((f) => {
      const src = f.dataset.archSrc + '?embed=1&theme=' + theme;
      if (f.getAttribute('src') !== src) f.setAttribute('src', src);
    });
  };
  // Re-skin the diagram canvas with the site's neutrals (same-origin iframe), so
  // regenerated Archify files need no hand edits. Node colors stay Archify's.
  const SKIN = 'html[data-embed="true"][data-theme="dark"]{--bg:#1b1818;--grid:#272323;--mask:#221f1f;--panel:rgba(34,31,31,.96)}'
    + 'html[data-embed="true"][data-theme="light"]{--bg:#fff;--grid:#f1efef;--mask:#f7f6f6;--panel:rgba(247,246,246,.97)}';
  const skin = (f) => {
    try {
      const d = f.contentDocument;
      if (d && d.head && !d.getElementById('fl-skin')) {
        const s = d.createElement('style');
        s.id = 'fl-skin';
        s.textContent = SKIN;
        d.head.appendChild(s);
      }
    } catch (_) { /* cross-origin — leave as is */ }
  };
  document.addEventListener('DOMContentLoaded', () => {
    document.querySelectorAll('iframe[data-arch-src]').forEach((f) => {
      f.addEventListener('load', () => skin(f));
      skin(f);
    });
    sync();
    new MutationObserver(sync).observe(document.documentElement, { attributes: true, attributeFilter: ['data-theme'] });
  });
})();`,
        },
      ],
      social: [
        { icon: 'github', label: 'GitHub', href: 'https://github.com/nightBaker/fleans' },
      ],
      sidebar: [
        {
          label: 'Getting Started',
          items: [
            { label: 'Introduction', slug: 'guides/introduction' },
            { label: 'Quick Start', slug: 'guides/quick-start' },
          ],
        },
        {
          label: 'Concepts',
          items: [
            { label: 'Architecture', slug: 'concepts/architecture' },
            { label: 'What is BPMN?', slug: 'concepts/bpmn-overview' },
            { label: 'BPMN Support', slug: 'concepts/bpmn-support' },
            { label: 'Custom Tasks', slug: 'concepts/custom-tasks' },
            { label: 'Hosting plugins externally', slug: 'concepts/plugin-hosting' },
          ],
        },
        {
          label: 'BPMN Elements',
          autogenerate: { directory: 'concepts/activities' },
        },
        {
          label: 'Building Workflows',
          items: [
            { label: 'Service Tasks', slug: 'guides/service-tasks' },
            { label: 'User Tasks', slug: 'guides/user-tasks' },
            { label: 'Call Activities and Sub-Processes', slug: 'guides/call-activities-and-subprocesses' },
            { label: 'Variables and Scope', slug: 'guides/variables-and-scope' },
            { label: 'Error Handling', slug: 'guides/error-handling' },
            { label: 'Multi-Instance Activities', slug: 'guides/multi-instance-activities' },
            { label: 'Message Correlation', slug: 'guides/message-correlation' },
          ],
        },
        {
          label: 'Admin UI',
          items: [
            { label: 'BPMN Editor', slug: 'guides/editor' },
            { label: 'Events Page', slug: 'guides/events-page' },
          ],
        },
        {
          label: 'Extending Fleans',
          items: [
            { label: 'Writing Custom-Task Plugins', slug: 'guides/writing-custom-tasks' },
            { label: 'Hosting Plugins (Custom Worker Host)', slug: 'guides/custom-worker-host' },
          ],
        },
        {
          label: 'Self-host',
          items: [
            { label: 'Docker Compose', slug: 'guides/self-host-docker-compose' },
            { label: 'Helm Chart', slug: 'guides/self-host-helm' },
            { label: 'Configuring observability', slug: 'guides/configuring-observability' },
          ],
        },
        {
          label: 'Reference',
          autogenerate: { directory: 'reference' },
        },
      ],
      components: {
        Footer: './src/components/Footer.astro',
        Header: './src/components/Header.astro',
        Hero: './src/components/Hero.astro',
        ThemeSelect: './src/components/ThemeSelect.astro',
      },
      // Code frames follow the site tokens (custom.css) so they flip with the theme.
      expressiveCode: {
        styleOverrides: {
          borderRadius: '0.625rem',
          borderColor: 'var(--fl-stroke)',
          codeFontFamily: "'JetBrains Mono', ui-monospace, monospace",
          codeFontSize: '0.875rem',
          codeBackground: 'var(--fl-surface-2)',
          uiFontFamily: "'Geist Sans', ui-sans-serif, system-ui, sans-serif",
          frames: {
            shadowColor: 'transparent',
            editorTabBarBackground: 'var(--fl-surface-3)',
            editorTabBarBorderBottomColor: 'var(--fl-stroke)',
            editorActiveTabBackground: 'var(--fl-surface-2)',
            editorActiveTabIndicatorTopColor: 'var(--fl-blue)',
            editorActiveTabIndicatorBottomColor: 'transparent',
            terminalTitlebarBackground: 'var(--fl-surface-3)',
            terminalTitlebarBorderBottomColor: 'var(--fl-stroke)',
            terminalBackground: 'var(--fl-surface-2)',
          },
        },
      },
      customCss: ['./src/styles/custom.css'],
    }),
  ],
});
