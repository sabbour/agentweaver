import { defineConfig } from 'vitepress'

export default defineConfig({
  title: 'Scaffolders',
  description: 'AI agent runs inside a sandboxed git worktree. You review before anything merges.',
  base: '/',
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
            { text: 'CLI', link: '/reference/cli' },
            { text: 'Web UI', link: '/reference/web' },
            { text: 'Events', link: '/reference/events' },
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
