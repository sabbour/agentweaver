/** Navigation, source exclusions, and local-search settings for the v1 documentation site. */
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
      { text: 'Deep dives', link: '/deep-dive/README' },
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
            { text: 'Gateway/BFF', link: '/guide/gateway' },
          ],
        },
        {
          text: 'Use',
          items: [
            { text: 'Gateway/BFF', link: '/guide/gateway' },
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
            { text: 'AgentHost', link: '/architecture/agenthost' },
            { text: 'PostgreSQL and Blob', link: '/architecture/persistence-objects' },
            { text: 'Telemetry', link: '/architecture/telemetry' },
            { text: 'Dedicated Azure environment', link: '/architecture/azure' },
          ],
        },
      ],
      '/deep-dive/': [
        {
          text: 'Deep dives',
          items: [
            { text: 'Overview', link: '/deep-dive/README' },
            { text: 'System overview', link: '/deep-dive/00-system-overview' },
            { text: 'Gateway', link: '/deep-dive/gateway' },
            { text: 'Orchestration', link: '/deep-dive/orchestration' },
            { text: 'Events and sessions', link: '/deep-dive/events-sessions' },
            { text: 'Agent runtime', link: '/deep-dive/agent-runtime' },
            { text: 'MCP server', link: '/deep-dive/mcp-server' },
          ],
        },
        {
          text: 'Platform',
          items: [
            { text: 'Identity and secrets', link: '/deep-dive/identity-secrets' },
            { text: 'Projects and configuration', link: '/deep-dive/projects-config' },
            { text: 'Environments and sandboxes', link: '/deep-dive/environment-sandbox' },
            { text: 'Knowledge and memory', link: '/deep-dive/knowledge-memory' },
            { text: 'Source control', link: '/deep-dive/source-control' },
            { text: 'Providers and models', link: '/deep-dive/providers-models' },
            { text: 'Persistence', link: '/deep-dive/persistence' },
            { text: 'Telemetry', link: '/deep-dive/telemetry' },
            { text: 'Infrastructure and deployment', link: '/deep-dive/infra-deployment' },
            { text: 'Testing strategy', link: '/deep-dive/testing-strategy' },
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
