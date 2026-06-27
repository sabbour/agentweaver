import { withMermaid } from 'vitepress-plugin-mermaid'

export default withMermaid({
  title: 'Agentweaver',
  description: 'Orchestrate AI agent teams that build and ship code — with full observability and human oversight at every step.',
  base: '/docs/',
  ignoreDeadLinks: true,
  markdown: {
    // Endpoint headings end in route params like `{id}`/`{path}`, which
    // markdown-it-attrs otherwise parses as an (empty) attribute block and
    // collides on a duplicate empty id. Disable attribute parsing — no doc
    // relies on `{.class}`/`{#anchor}` syntax.
    attrs: { disable: true },
  },
  mermaid: {},
  themeConfig: {
    logo: '/agentweaver.png',
    nav: [
      { text: 'Guide', link: '/guide/' },
      { text: 'Reference', link: '/reference/api' },
      { text: 'Architecture', link: '/architecture/overview' },
      { text: 'Deep Dive', link: '/deep-dive/00-system-overview' },
      { text: 'Experience', link: '/experience/' },
    ],
    sidebar: {
      '/guide/': [
        {
          text: 'Guide',
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
            { text: 'Getting started', link: '/guide/getting-started' },
            { text: 'Configuration', link: '/guide/configuration' },
            { text: 'Deploy to AKS', link: '/guide/deployment-aks' },
          ],
        },
      ],
      '/reference/': [
        {
          text: 'Reference',
          items: [
            { text: 'API', link: '/reference/api' },
            { text: 'MCP server', link: '/reference/mcp' },
            { text: 'Web UI', link: '/reference/web' },
            { text: 'Events', link: '/reference/events' },
            { text: 'Coordinator', link: '/reference/coordinator' },
            { text: 'Memory', link: '/reference/memory' },
          ],
        },
      ],
      '/architecture/': [
        {
          text: 'Architecture',
          items: [
            { text: 'Overview', link: '/architecture/overview' },
            { text: 'Sandbox', link: '/architecture/sandbox' },
            { text: 'Events', link: '/architecture/events' },
            { text: 'AKS Architecture', link: '/architecture-aks' },
          ],
        },
      ],
      '/experience/': [
        {
          text: 'Experience',
          items: [
            { text: 'Overview', link: '/experience/00-overview' },
            { text: 'Onboarding & auth', link: '/experience/onboarding-auth' },
            { text: 'Projects', link: '/experience/projects' },
            { text: 'Runs, board & watch', link: '/experience/runs-board-watch' },
            { text: 'Coordinator & orchestration', link: '/experience/coordinator-orchestration' },
            { text: 'Review, workspace & merge', link: '/experience/review-workspace-merge' },
            { text: 'Team, casting & memory', link: '/experience/team-casting-memory' },
            { text: 'Workflows & backlog', link: '/experience/workflows-backlog' },
            { text: 'Operations', link: '/experience/operations' },
            { text: 'MCP client', link: '/experience/mcp-client' },
          ],
        },
      ],
      '/deep-dive/': [
        {
          text: 'Foundations',
          items: [
            { text: 'System overview', link: '/deep-dive/00-system-overview' },
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
            { text: 'Team & casting', link: '/deep-dive/team-casting' },
            { text: 'Agent runtime', link: '/deep-dive/agent-runtime' },
            { text: 'Review & merge', link: '/deep-dive/review-merge' },
          ],
        },
        {
          text: 'Execution & integration',
          items: [
            { text: 'Sandbox', link: '/deep-dive/sandbox' },
            { text: 'Git integration', link: '/deep-dive/git-integration' },
            { text: 'MCP server', link: '/deep-dive/mcp-server' },
            { text: 'Projects & workspaces', link: '/deep-dive/projects' },
          ],
        },
        {
          text: 'Data & platform',
          items: [
            { text: 'Data & persistence', link: '/deep-dive/data-persistence' },
            { text: 'Memory & decisions', link: '/deep-dive/memory-decisions' },
            { text: 'Events & observability', link: '/deep-dive/events-observability' },
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
