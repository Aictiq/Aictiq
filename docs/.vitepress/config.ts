import { defineConfig } from 'vitepress'

// DOCS_BASE lets the GitHub Pages workflow build the site under /<repo>/ while
// local builds and the eventual custom domain stay at the root.
export default defineConfig({
  base: process.env.DOCS_BASE ?? '/',
  lang: 'en-US',
  title: 'Aictiq',
  description:
    'Project management for software teams and their AI agents: organizations, ' +
    'projects, sprints, wiki and analytics — with agents that discover ready work, ' +
    'claim it atomically and see it through over MCP or the CLI.',
  // The source pages double as repository documentation and keep their in-repo
  // relative links, a few of which intentionally point outside this site
  // (deploy/, SECURITY.md). Ignore those instead of deadening every link check.
  ignoreDeadLinks: true,
  themeConfig: {
    outline: { level: [2, 3] },
    search: { provider: 'local' },
    nav: [
      { text: 'Home', link: '/' },
      { text: 'Guide', link: '/getting-started', activeMatch: '/guide/' },
      { text: 'Agents', link: '/agents' },
      { text: 'CLI', link: '/cli' },
      { text: 'API', link: '/api' }
    ],
    sidebar: [
      {
        text: 'Getting started',
        items: [
          { text: 'Install with compose', link: '/getting-started' },
          { text: 'Self-hosting', link: '/self-host' },
          { text: 'FAQ', link: '/guide/faq' }
        ]
      },
      {
        text: 'Agents & automation',
        items: [
          { text: 'Connecting an agent', link: '/agents' },
          { text: 'AI software factory', link: '/factory' },
          { text: 'The aictiq CLI', link: '/cli' },
          { text: 'REST API', link: '/api' },
          { text: 'Outgoing webhooks', link: '/webhooks' }
        ]
      },
      {
        text: 'Reference',
        items: [
          { text: 'Data model', link: '/data-model' },
          { text: 'Engineering invariants', link: '/invariants' },
          { text: 'Analytics definitions', link: '/analytics' },
          { text: 'Security model', link: '/security' },
          { text: 'Operations', link: '/operations' },
          { text: 'Billing (SaaS)', link: '/billing' },
          { text: 'Performance', link: '/performance' },
          { text: 'Usage telemetry', link: '/telemetry' }
        ]
      }
    ]
  }
})
