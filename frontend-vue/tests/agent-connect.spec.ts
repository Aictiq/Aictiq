import { mount } from '@vue/test-utils'
import { createMemoryHistory, createRouter } from 'vue-router'
import { describe, expect, it } from 'vitest'

import type { Agent } from '@/api/agents'
import AgentConnectSheet from '@/components/settings/AgentConnectSheet.vue'
import {
  claudeMdSnippet,
  cliLoginSnippet,
  httpMcpSnippet,
  mcpUrl,
  stdioMcpSnippet,
  tokenPlaceholder,
} from '@/lib/agentSnippets'

/**
 * The snippets the "Connect an agent" drawer hands out. Three rules are worth asserting:
 * the URL is this deployment's, a token appears only when one was just issued (Aictiq
 * cannot show a secret twice), and the stdio form carries no token at all — which is the
 * whole reason to prefer it for a file that gets committed.
 */
describe('agent connection snippets', () => {
  it('points at this deployment and tolerates a trailing slash on the origin', () => {
    expect(mcpUrl('https://aictiq.example.com')).toBe('https://aictiq.example.com/mcp')
    expect(mcpUrl('https://aictiq.example.com/')).toBe('https://aictiq.example.com/mcp')

    const snippet = JSON.parse(httpMcpSnippet({ origin: 'https://aictiq.example.com/' }))
    expect(snippet.mcpServers.aictiq.url).toBe('https://aictiq.example.com/mcp')
    expect(snippet.mcpServers.aictiq.type).toBe('http')
  })

  it('carries a freshly issued token, and a placeholder when there is none', () => {
    const withSecret = JSON.parse(
      httpMcpSnippet({ origin: 'https://x.test', secret: 'aiq_freshly_issued' }),
    )
    expect(withSecret.mcpServers.aictiq.headers.Authorization).toBe('Bearer aiq_freshly_issued')

    const without = JSON.parse(httpMcpSnippet({ origin: 'https://x.test', secret: null }))
    expect(without.mcpServers.aictiq.headers.Authorization).toBe(`Bearer ${tokenPlaceholder}`)
  })

  it('keeps the stdio form free of any credential', () => {
    const snippet = stdioMcpSnippet()

    expect(JSON.parse(snippet).mcpServers.aictiq).toEqual({ command: 'aictiq', args: ['mcp'] })
    expect(snippet).not.toContain('Authorization')
    expect(snippet).not.toContain('aiq_')
  })

  it('logs the CLI in against the same origin', () => {
    expect(cliLoginSnippet({ origin: 'https://aictiq.example.com/' })).toContain(
      'aictiq auth login --url https://aictiq.example.com',
    )
  })

  it('teaches the loop, in the project key the drawer was opened for', () => {
    const block = claudeMdSnippet({ origin: 'https://x.test', projectKey: 'ACME' })

    expect(block).toContain('list_ready_work(project: "ACME")')
    expect(block).toContain('claim_item(key, version)')
    expect(block).toContain('acme-123-short-slug')
    // The idempotent progress comment is the convention that keeps a retried loop from
    // turning the thread into a log.
    expect(block).toContain('<!-- aictiq:progress -->')
  })
})

describe('AgentConnectSheet', () => {
  it('offers Factory as a separate way to run the agent and links to its runners', async () => {
    const router = createRouter({
      history: createMemoryHistory(),
      routes: [{ path: '/o/:slug/factory/runners', component: { template: '<div />' } }],
    })
    await router.push('/o/acme/factory/runners')
    await router.isReady()

    const agent: Agent = {
      userId: 'agent-1',
      displayName: 'claude-dev',
      email: 'agent-1@agents.invalid',
      ownerUserId: 'user-1',
      ownerName: 'Alice',
      role: 'member',
      isActive: true,
      createdAt: '2026-09-19T00:00:00Z',
      lastActiveAt: null,
      tokenCount: 1,
    }
    const passthrough = { template: '<div><slot /></div>' }
    const wrapper = mount(AgentConnectSheet, {
      props: { agent, slug: 'acme', secret: null, open: true },
      global: {
        plugins: [router],
        stubs: {
          Sheet: passthrough,
          SheetContent: passthrough,
          SheetDescription: passthrough,
          SheetHeader: passthrough,
          SheetTitle: passthrough,
        },
      },
    })

    const tabs = wrapper.findAll('[role="tab"]')
    expect(tabs.map((entry) => entry.text())).toEqual(['Connect it yourself', 'Run it from Aictiq'])
    expect(tabs[0]!.attributes('aria-selected')).toBe('true')

    await tabs[1]!.trigger('click')

    expect(tabs[1]!.attributes('aria-selected')).toBe('true')
    expect(wrapper.text()).toContain('Give claude-dev a playbook and a runner')
    expect(wrapper.get('a[href="/o/acme/factory/runners"]').text()).toBe('Open Factory')
  })
})
