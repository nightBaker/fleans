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
      social: [
        { icon: 'github', label: 'GitHub', href: 'https://github.com/nightBaker/fleans' },
      ],
      sidebar: [
        {
          label: 'Getting Started',
          items: [
            { label: 'Introduction', slug: 'guides/introduction' },
            { label: 'Quick Start', slug: 'guides/quick-start' },
            { label: 'Service Tasks', slug: 'guides/service-tasks' },
            { label: 'BPMN Editor', slug: 'guides/editor' },
            { label: 'Add to Existing Project', slug: 'guides/add-to-existing-project' },
          ],
        },
        {
          label: 'Concepts',
          items: [
            { label: 'Architecture', slug: 'concepts/architecture' },
            { label: 'What is BPMN?', slug: 'concepts/bpmn-overview' },
            { label: 'BPMN Support', slug: 'concepts/bpmn-support' },
            { label: 'Custom Tasks', slug: 'concepts/custom-tasks' },
          ],
        },
        {
          label: 'Reference',
          autogenerate: { directory: 'reference' },
        },
      ],
      components: {
        Footer: './src/components/Footer.astro',
      },
      customCss: ['./src/styles/custom.css'],
    }),
  ],
});
