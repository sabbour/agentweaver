import { defineConfig } from 'vitepress'

export default defineConfig({
  title: 'Agentweaver',
  description: 'AI agent runs inside a sandboxed git worktree. You review before anything merges.',
  base: '/',
  markdown: {
    // Endpoint headings end in route params like `{id}`/`{path}`, which
    // markdown-it-attrs otherwise parses as an (empty) attribute block and
    // collides on a duplicate empty id. Disable attribute parsing — no doc
    // relies on `{.class}`/`{#anchor}` syntax.
    attrs: { disable: true },
  },
  themeConfig: {
    nav: [
      { text: 'Guide', link: '/guide/getting-started' },
      { text: 'Reference', link: '/reference/api' },
      { text: 'Architecture', link: '/architecture/overview' },
    ],
    sidebar: {
      '/guide/': [
        {
          text: 'Guide',
          items: [
            { text: 'Getting started', link: '/guide/getting-started' },
            { text: 'Configuration', link: '/guide/configuration' },
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
          ],
        },
      ],
    },
    search: {
      provider: 'local',
    },
  },
})
