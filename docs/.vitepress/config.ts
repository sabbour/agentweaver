/** Navigation, source exclusions, and local-search settings for the documentation site. */
import { defineConfig } from 'vitepress'

export default defineConfig({
  title: 'Agentweaver',
  description: 'Documentation for Agentweaver.',
  base: '/agentweaver/v1/',
  srcExclude: [
    'architecture/decisions/**',
    'architecture/design/**',
    'specs/**',
    'authoring/**',
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
      { text: 'Docs home', link: '/' },
      { text: 'Build source', link: '/guide/quickstart' },
      { text: 'Browser tasks', link: '/experience/README' },
      { text: 'How-to guides', link: '/guide/' },
      { text: 'System overview', link: '/deep-dive/00-system-overview' },
      { text: 'Reference', link: '/reference/runtime-configuration' },
      { text: 'Operations', link: '/guide/deploy-to-azure' },
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
            { text: 'All guides', link: '/guide/' },
            { text: 'Build and check the source', link: '/guide/quickstart' },
            { text: 'Agentweaver CLI', link: '/guide/cli' },
            { text: 'Find CLI commands', link: '/guide/find-cli-commands' },
            { text: 'Build and use foundation libraries', link: '/guide/build-and-use' },
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
            { text: 'Configure Knowledge memory', link: '/guide/configure-knowledge' },
            { text: 'Configure an Azure OpenAI BYOK model', link: '/guide/configure-byok-azure-openai' },
            { text: 'Build and configure the web client', link: '/guide/web-client' },
            { text: 'Read and dismiss notifications', link: '/guide/notifications' },
            { text: 'Use the Gateway', link: '/guide/gateway' },
            { text: 'Chief of Staff', link: '/guide/chief-of-staff' },
            { text: 'Read platform component status', link: '/guide/platform-components' },
            { text: 'Run database maintenance', link: '/guide/database-maintenance' },
          ],
        },
        {
          text: 'Plan and troubleshoot',
          items: [
            { text: 'Testing', link: '/guide/testing' },
            { text: 'Read-only CLI API case', link: '/guide/api-acceptance-migration' },
            { text: 'Numeric runtime observation case', link: '/guide/numeric-runtime-observation-acceptance' },
            { text: 'Plan issues and releases', link: '/guide/release-planning' },
            { text: 'Environment Sandbox testing', link: '/guide/environment-sandbox-testing' },
            { text: 'Azure acceptance', link: '/guide/azure-acceptance' },
            { text: 'Troubleshooting', link: '/guide/troubleshooting' },
            { text: 'Diagram authoring', link: '/diagrams/README' },
          ],
        },
        {
          text: 'Find reference information',
          items: [
            { text: 'Provider choices', link: '/guide/configure-providers' },
            { text: 'Identity Broker permissions', link: '/guide/identity-scopes' },
          ],
        },
      ],
      '/experience/': [
        {
          text: 'Browser tasks',
          items: [
            { text: 'Choose a browser task', link: '/experience/README' },
            { text: 'What the web app can do', link: '/experience/00-overview' },
            { text: 'Sign in and access', link: '/experience/onboarding-auth' },
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
            { text: 'Retained MCP compatibility adapter', link: '/deep-dive/mcp-server' },
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
            { text: 'Web integration', link: '/reference/web' },
            { text: 'Web UI parity', link: '/reference/web-ui-parity' },
            { text: 'AgentSuite import and export', link: '/reference/agentsuite-import' },
            { text: 'Environment creation and readiness records', link: '/reference/p1-creation-evidence' },
            { text: 'MCP compatibility adapter reference', link: '/reference/mcp-tools' },
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
