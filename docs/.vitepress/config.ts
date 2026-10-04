import { defineConfig } from 'vitepress'

export default defineConfig({
  title: 'Agentweaver 1.x',
  description: 'Implementation documentation for Agentweaver 1.x.',
  base: '/agentweaver/v1/',
  srcExclude: [
    'architecture/decisions/**',
    'architecture/design/**',
    'specs/**',
  ],
  ignoreDeadLinks: false,
  markdown: {
    attrs: { disable: true },
  },
  head: [
    ['link', { rel: 'icon', type: 'image/png', href: '/agentweaver/v1/agentweaver.png' }],
  ],
  themeConfig: {
    logo: '/agentweaver.png',
    aside: false,
    outline: false,
    nav: [
      { text: 'Start', link: '/guide/build-and-use' },
      { text: 'Use', link: '/guide/azure-acceptance' },
      { text: 'Architecture', link: '/architecture/overview' },
      { text: 'Reference', link: '/reference/contracts' },
      {
        text: '0.x documentation',
        link: 'https://sabbour.github.io/agentweaver/',
      },
    ],
    sidebar: {
      '/guide/': [
        {
          text: 'Start',
          items: [
            { text: 'Build and use', link: '/guide/build-and-use' },
            { text: 'Testing', link: '/guide/testing' },
          ],
        },
        {
          text: 'Use',
          items: [
            { text: 'Azure acceptance', link: '/guide/azure-acceptance' },
            { text: 'Diagram authoring', link: '/diagrams/README' },
          ],
        },
      ],
      '/architecture/': [
        {
          text: 'Architecture',
          items: [
            { text: 'Foundation overview', link: '/architecture/overview' },
            { text: 'Providers and models', link: '/architecture/providers-models' },
            { text: 'Identity and secrets', link: '/architecture/identity-secrets' },
            { text: 'PostgreSQL and Blob', link: '/architecture/persistence-objects' },
            { text: 'Telemetry', link: '/architecture/telemetry' },
            { text: 'Dedicated Azure environment', link: '/architecture/azure' },
          ],
        },
      ],
      '/reference/': [
        {
          text: 'Reference',
          items: [
            { text: 'Contracts and endpoints', link: '/reference/contracts' },
            { text: 'Component releases', link: '/reference/releases' },
          ],
        },
      ],
      '/diagrams/': [
        {
          text: 'Diagrams',
          items: [
            { text: 'Authoring', link: '/diagrams/README' },
          ],
        },
      ],
    },
    search: {
      provider: 'local',
    },
    socialLinks: [
      { icon: 'github', link: 'https://github.com/sabbour/agentweaver/tree/v1' },
    ],
  },
})
