import { flushPromises, mount } from '@vue/test-utils'
import { createPinia } from 'pinia'
import { VueQueryPlugin } from '@tanstack/vue-query'
import { createMemoryHistory, createRouter, type Router } from 'vue-router'
import { describe, expect, it, vi, afterEach } from 'vitest'

import RuleDetailView from '@/views/factory/RuleDetailView.vue'

/**
 * A rule's own page resolves itself out of the organization-wide rules listing (there is
 * no single-rule-by-id endpoint without a project key), then lists its last 50 firings —
 * each one either a run to open or the reason it was skipped.
 */
const rule = {
  id: 'r1',
  projectId: 'pr1',
  projectKey: 'PROJ',
  projectName: 'Prototype',
  name: 'Start implementation',
  triggerStateId: 's-ready',
  requiredLabelId: null,
  playbookId: 'p1',
  playbookName: 'Implement',
  agentId: 'a1',
  agentName: 'claude-dev',
  enabled: true,
  createdBy: 'u1',
  createdAt: '2026-01-01T00:00:00Z',
  updatedAt: '2026-01-01T00:00:00Z',
  version: 5,
  lastFiring: null,
}

const workflow = {
  id: 'w1',
  name: 'Default',
  isDefault: true,
  version: 1,
  states: [
    { id: 's-ready', name: 'Ready', category: 'active', position: 1, color: null, isInitial: false },
  ],
  transitions: [],
}

const firings = [
  {
    itemId: 'i1',
    itemKey: 'PROJ-1',
    eventId: 'e1',
    at: '2026-09-01T00:00:00Z',
    runId: 'run-1',
    runStatus: 'succeeded',
    skipReason: null,
  },
  {
    itemId: 'i2',
    itemKey: 'PROJ-2',
    eventId: 'e2',
    at: '2026-09-02T00:00:00Z',
    runId: null,
    runStatus: null,
    skipReason: 'item-claimed',
  },
]

function stubFetch() {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: unknown) => {
      const url = String(input)
      let body: unknown = { title: 'Not found' }
      if (url.endsWith('/orgs/acme/rules')) body = [rule]
      else if (url.endsWith('/firings')) body = firings
      else if (url.endsWith('/workflows')) body = [workflow]
      else if (url.endsWith('/labels')) body = []
      return new Response(JSON.stringify(body), {
        status: 200,
        headers: { 'content-type': 'application/json' },
      })
    }),
  )
}

async function mountDetail(ruleId = 'r1') {
  const router: Router = createRouter({
    history: createMemoryHistory(),
    routes: [{ path: '/o/:slug/factory/rules/:ruleId', component: RuleDetailView }],
  })
  await router.push(`/o/acme/factory/rules/${ruleId}`)
  await router.isReady()

  const wrapper = mount(RuleDetailView, {
    global: { plugins: [createPinia(), router, VueQueryPlugin] },
  })
  await flushPromises()
  await flushPromises()
  return wrapper
}

afterEach(() => vi.unstubAllGlobals())

describe('RuleDetailView', () => {
  it('shows the rule and its sentence', async () => {
    stubFetch()
    const wrapper = await mountDetail()

    expect(wrapper.text()).toContain('Start implementation')
    expect(wrapper.text()).toContain('Ready')
    expect(wrapper.text()).toContain('Implement')
    expect(wrapper.text()).toContain('claude-dev')
  })

  it('lists firings: a run link for a dispatch, the skip reason otherwise', async () => {
    stubFetch()
    const wrapper = await mountDetail()

    expect(wrapper.text()).toContain('PROJ-1')
    expect(wrapper.text()).toContain('Open run')
    expect(wrapper.text()).toContain('PROJ-2')
    expect(wrapper.text()).toContain('Skipped: item already claimed')
  })

  it('says not found for a rule id outside the caller’s admin projects', async () => {
    stubFetch()
    const wrapper = await mountDetail('missing')

    expect(wrapper.text()).toContain('Rule not found')
  })
})
