import { withMermaid } from 'vitepress-plugin-mermaid'

export default withMermaid({
  title: 'Agentweaver',
  description: 'Orchestrate AI agent teams that build and ship code — with full observability and human oversight at every step.',
  base: '/agentweaver/',
  ignoreDeadLinks: true,
  markdown: {
    // Endpoint headings end in route params like `{id}`/`{path}`, which
    // markdown-it-attrs otherwise parses as an (empty) attribute block and
    // collides on a duplicate empty id. Disable attribute parsing — no doc
    // relies on `{.class}`/`{#anchor}` syntax.
    attrs: { disable: true },
  },
  mermaid: {
    flowchart: {
      useMaxWidth: true,
      htmlLabels: true,
      padding: 12,
    },
  },
  themeConfig: {
    logo: '/agentweaver.png',
    // Top navbar is the section switcher; the right-hand outline/aside is
    // disabled globally so content and diagrams use the full page width.
    aside: false,
    outline: false,
    nav: [
      { text: 'Getting Started', link: '/guide/' },
      { text: 'User Guide', link: '/experience/00-overview' },
      { text: 'Deep Dive', link: '/deep-dive/00-system-overview' },
      { text: 'Reference', link: '/reference/api' },
    ],
    socialLinks: [
      { icon: 'github', link: 'https://github.com/sabbour/agentweaver' },
    ],
    sidebar: {
      '/guide/': [
        {
          text: 'Concepts',
          items: [
            { text: 'What is Agentweaver?', link: '/guide/' },
            { text: 'Authentication', link: '/guide/authentication' },
            { text: 'Projects', link: '/guide/projects' },
            { text: 'Agent Teams & Blueprints', link: '/guide/teams' },
            { text: 'Workflows', link: '/guide/workflows' },
            { text: 'Board and Backlog', link: '/guide/board' },
            { text: 'Runs', link: '/guide/runs' },
            { text: 'Review & Merge', link: '/guide/review' },
          ],
        },
        {
          text: 'Setup',
          items: [
            { text: 'Configuration', link: '/guide/configuration' },
            { text: 'Deploy to AKS', link: '/guide/deployment-aks' },
            { text: 'AKS Architecture', link: '/architecture-aks' },
          ],
        },
        {
          text: 'Getting Started',
          items: [
            { text: 'Getting started', link: '/guide/getting-started' },
            { text: 'Example walkthroughs', link: '/guide/example-scenarios' },
          ],
        },
      ],
      '/reference/': [
        {
          text: 'Reference',
          items: [
            { text: 'API', link: '/reference/api' },
            { text: 'MCP server', link: '/reference/mcp' },
            { text: 'Agent definition', link: '/reference/agent-definition' },
            { text: 'Web UI', link: '/reference/web' },
            { text: 'Events', link: '/reference/events' },
            { text: 'Coordinator', link: '/reference/coordinator' },
            { text: 'Memory', link: '/reference/memory' },
            { text: 'Agent communication', link: '/reference/agent-communication' },
            { text: 'Sandbox pods', link: '/reference/sandbox-pods' },
            { text: 'Sandbox browser preview', link: '/reference/sandbox-browser-preview' },
            { text: 'Sandbox setup', link: '/reference/sandbox-setup' },
            { text: 'Token usage', link: '/reference/token-usage' },
            { text: 'A2A transport', link: '/reference/a2a' },
            { text: 'Cluster diagnostics', link: '/reference/cluster-diagnostics' },
            { text: 'Scaling & data layer', link: '/reference/scaling-data-layer' },
          ],
        },
      ],
      '/experience/': [
        {
          text: 'Web',
          items: [
            { text: 'Overview', link: '/experience/00-overview' },
            { text: 'Onboarding & auth', link: '/experience/onboarding-auth' },
            { text: 'Projects', link: '/experience/projects' },
            { text: 'Agent definition', link: '/experience/agent-definition' },
            { text: 'Runs board & watch', link: '/experience/runs-board-watch' },
            { text: 'Coordinator & orchestration', link: '/experience/coordinator-orchestration' },
            { text: 'Review workspace & merge', link: '/experience/review-workspace-merge' },
            { text: 'Team casting & memory', link: '/experience/team-casting-memory' },
            { text: 'Workflows & backlog', link: '/experience/workflows-backlog' },
            { text: 'Operations', link: '/experience/operations' },
            { text: 'Cluster', link: '/experience/cluster-page' },
            { text: 'Sandbox pod execution', link: '/experience/sandbox-pod-execution' },
            { text: 'Sandbox browser preview', link: '/experience/sandbox-browser-preview' },
            { text: 'Distributed agents (A2A)', link: '/experience/a2a-distributed-agents' },
            { text: 'Scaling & operations', link: '/experience/scaling-operations' },
            { text: 'Agent communication', link: '/experience/agent-communication' },
            { text: 'Token usage monitoring', link: '/experience/token-usage-monitoring' },
          ],
        },
        {
          text: 'MCP (Experimental)',
          items: [
            { text: 'MCP client', link: '/experience/mcp-client' },
          ],
        },
      ],
      '/deep-dive/': [
        {
          text: 'Foundations',
          items: [
            { text: 'System overview', link: '/deep-dive/00-system-overview' },
            { text: 'Agent Framework (MAF)', link: '/deep-dive/agent-framework' },
            { text: 'API core', link: '/deep-dive/api-core' },
            { text: 'Auth & security', link: '/deep-dive/auth-security' },
          ],
        },
        {
          text: 'Orchestration & agents',
          items: [
            { text: 'Orchestration', link: '/deep-dive/orchestration' },
            { text: 'Coordinator internals', link: '/deep-dive/coordinator-internals' },
            { text: 'Workflow engine', link: '/deep-dive/workflow-engine' },
            { text: 'Workflow selection', link: '/deep-dive/workflow-selection' },
            { text: 'Team & casting', link: '/deep-dive/team-casting' },
            { text: 'Agent runtime', link: '/deep-dive/agent-runtime' },
            { text: 'Agent communication', link: '/deep-dive/agent-communication' },
            { text: 'Review & merge', link: '/deep-dive/review-merge' },
          ],
        },
        {
          text: 'Execution & integration',
          items: [
            { text: 'Sandbox', link: '/deep-dive/sandbox' },
            { text: 'Sandbox pod execution', link: '/deep-dive/sandbox-pod-execution' },
            { text: 'Agent-host token delivery', link: '/deep-dive/agent-token-delivery' },
            { text: 'Sandbox browser preview', link: '/deep-dive/sandbox-browser-preview' },
            { text: 'Sandboxed execution', link: '/deep-dive/sandboxed-execution' },
            { text: 'A2A bridge', link: '/deep-dive/a2a-bridge' },
            { text: 'Git integration', link: '/deep-dive/git-integration' },
            { text: 'MCP server', link: '/deep-dive/mcp-server' },
            { text: 'Agent definition', link: '/deep-dive/agent-definition' },
            { text: 'Projects & workspaces', link: '/deep-dive/projects' },
          ],
        },
        {
          text: 'Data & platform',
          items: [
            { text: 'Data & persistence', link: '/deep-dive/data-persistence' },
            { text: 'Distributed execution & scaling', link: '/deep-dive/distributed-execution-scaling' },
            { text: 'Memory & decisions', link: '/deep-dive/memory-decisions' },
            { text: 'Events & observability', link: '/deep-dive/events-observability' },
            { text: 'Token usage monitoring', link: '/deep-dive/token-usage-monitoring' },
            { text: 'Frontend', link: '/deep-dive/frontend' },
            { text: 'Infrastructure', link: '/deep-dive/infra-deployment' },
            { text: 'Testing strategy', link: '/deep-dive/testing-strategy' },
          ],
        },
      ],
    },
    search: {
      provider: 'local',
    },
  },
})
