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
      { text: 'Get started', link: '/guide/' },
      { text: 'Tutorials', link: '/guide/deploy-to-azure' },
      { text: 'Concepts', link: '/experience/00-overview' },
      { text: 'How-to', link: '/guide/configure-providers' },
      { text: 'Reference', link: '/reference/runtime-configuration' },
      { text: 'Troubleshooting', link: '/guide/troubleshooting' },
      {
        text: '0.x documentation',
        link: 'https://sabbour.github.io/agentweaver/',
      },
    ],
    sidebar: {
      '/guide/': [
        {
          text: 'Get started',
          items: [
            { text: 'Guides and tutorials', link: '/guide/' },
            { text: 'Quickstart: build the source', link: '/guide/quickstart' },
            { text: 'Build and use', link: '/guide/build-and-use' },
          ],
        },
        {
          text: 'Tutorial',
          items: [
            { text: 'Deploy Agentweaver to Azure', link: '/guide/deploy-to-azure' },
          ],
        },
        {
          text: 'Configure and deploy',
          items: [
            { text: 'Configure providers', link: '/guide/configure-providers' },
            { text: 'Configure Knowledge memory', link: '/guide/configure-knowledge' },
            { text: 'Web client', link: '/guide/web-client' },
            { text: 'Notifications', link: '/guide/notifications' },
            { text: 'Gateway/BFF', link: '/guide/gateway' },
            { text: 'Chief of Staff', link: '/guide/chief-of-staff' },
            { text: 'Platform components', link: '/guide/platform-components' },
          ],
        },
        {
          text: 'Test and troubleshoot',
          items: [
            { text: 'Testing', link: '/guide/testing' },
            { text: 'Environment Sandbox testing', link: '/guide/environment-sandbox-testing' },
            { text: 'Azure acceptance', link: '/guide/azure-acceptance' },
            { text: 'Troubleshooting', link: '/guide/troubleshooting' },
            { text: 'Diagram authoring', link: '/diagrams/README' },
          ],
        },
      ],
      '/experience/': [
        {
          text: 'Experience',
          items: [
            { text: 'Overview', link: '/experience/README' },
            { text: 'What you can do', link: '/experience/00-overview' },
            { text: 'Sign-in and consent', link: '/experience/onboarding-auth' },
            { text: 'Projects', link: '/experience/projects' },
            { text: 'Configuration', link: '/experience/configuration' },
            { text: 'Skills', link: '/experience/skills' },
            { text: 'Connections', link: '/experience/connections' },
            { text: 'Runs and sessions', link: '/experience/runs-sessions' },
            { text: 'Knowledge', link: '/experience/knowledge' },
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
            { text: 'Runtime configuration', link: '/reference/runtime-configuration' },
            { text: 'HTTP API', link: '/reference/api' },
            { text: 'Events', link: '/reference/events' },
            { text: 'Web client', link: '/reference/web' },
            { text: 'MCP tools', link: '/reference/mcp-tools' },
            { text: 'AgentSuite import', link: '/reference/agentsuite-import' },
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
