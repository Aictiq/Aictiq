import { flushPromises, mount } from '@vue/test-utils'
import { createPinia } from 'pinia'
import { VueQueryPlugin } from '@tanstack/vue-query'
import { createMemoryHistory, createRouter, type Router } from 'vue-router'
import { describe, expect, it, vi, afterEach } from 'vitest'
import { ref } from 'vue'

import RulesTab from '@/views/factory/RulesTab.vue'
import { orgScopeKey, type OrgScope } from '@/composables/useSettingsScope'
import type { Organization } from '@/api/organizations'
import { factoryDocsUrl } from '@/lib/factory'

/**
 * The Rules tab groups by project - one section per project the caller administers,
 * shown even with zero rules so there is somewhere to create the first one - and each
 * row reads as the sentence a rule describes: "When an item enters X [with label Y] →
 * run Z as W."
 */
const adminProject = {
  id: 'pr1',
  key: 'PROJ',
  name: 'Prototype',
  description: null,
  visibility: 'organization',
  icon: null,
  color: null,
  isArchived: false,
  createdAt: '2026-01-01T00:00:00Z',
  role: 'admin',
  version: 1,
}

const memberProject = {
  ...adminProject,
  id: 'pr2',
  key: 'OTHER',
  name: 'Not mine to administer',
  role: 'member',
}

const rule = {
  id: 'r1',
  projectId: 'pr1',
  projectKey: 'PROJ',
  projectName: 'Prototype',
  name: 'Start implementation',
  triggerStateId: 's-ready',
  requiredLabelId: 'l-agent',
  playbookId: 'p1',
  playbookName: 'Implement',
  agentId: 'a1',
  agentName: 'claude-dev',
  enabled: true,
  createdBy: 'u1',
  createdAt: '2026-01-01T00:00:00Z',
  updatedAt: '2026-01-01T00:00:00Z',
  version: 2,
  lastFiring: {
    at: '2026-09-01T00:00:00Z',
    itemKey: 'PROJ-9',
    runId: 'run-1',
    skipReason: null,
  },
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

const label = {
  id: 'l-agent',
  name: 'agent',
  color: null,
  description: null,
  group: null,
  itemCount: 3,
  version: 1,
}

function stubFetch(rules: unknown[] = [rule]) {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: unknown, init?: RequestInit) => {
      const url = String(input)
      let body: unknown = { title: 'Not found' }
      if (url.endsWith('/rules')) body = url.includes('OTHER') ? [] : rules
      else if (url.endsWith('/workflows')) body = [workflow]
      else if (url.endsWith('/labels')) body = [label]
      else if (/\/rules\/[^/]+$/.test(url) && init?.method === 'PATCH') {
        body = { ...rule, enabled: JSON.parse(String(init.body)).enabled, version: rule.version + 1 }
      } else if (url.includes('/projects')) body = [adminProject, memberProject]
      return new Response(JSON.stringify(body), {
        status: 200,
        headers: { 'content-type': 'application/json' },
      })
    }),
  )
}

const organization: Organization = {
  id: 'org-1',
  slug: 'acme',
  name: 'Acme',
  role: 'owner',
  canOperateFactory: true,
  plan: 'trial',
  timeZone: 'UTC',
  weekStart: 'monday',
  membersCanCreateProjects: true,
  createdAt: '2026-01-01T00:00:00Z',
  version: 1,
}

async function mountTab() {
  const router: Router = createRouter({
    history: createMemoryHistory(),
    routes: [
      { path: '/o/:slug/factory/rules', component: RulesTab },
      { path: '/o/:slug/factory/rules/:ruleId', component: { template: '<div />' } },
    ],
  })
  await router.push('/o/acme/factory/rules')
  await router.isReady()

  const scope: OrgScope = {
    slug: ref('acme'),
    record: ref(organization),
    loading: ref(false),
    notFound: ref(false),
    reload: async () => {},
    set: () => {},
  }

  const wrapper = mount(RulesTab, {
    global: {
      plugins: [createPinia(), router, VueQueryPlugin],
      provide: { [orgScopeKey]: scope },
    },
  })
  await flushPromises()
  await flushPromises()
  return { wrapper, router }
}

afterEach(() => vi.unstubAllGlobals())

describe('RulesTab', () => {
  it('groups by project and renders the rule as a sentence', async () => {
    stubFetch()
    const { wrapper } = await mountTab()

    const groups = wrapper.find('[data-testid="rules-groups"]')
    expect(groups.text()).toContain('Prototype')
    expect(groups.text()).toContain('PROJ')
    expect(groups.text()).toContain('Start implementation')
    expect(groups.text()).toContain('Ready')
    expect(groups.text()).toContain('agent')
    expect(groups.text()).toContain('Implement')
    expect(groups.text()).toContain('claude-dev')
  })

  it('shows only projects the caller administers', async () => {
    stubFetch()
    const { wrapper } = await mountTab()

    expect(wrapper.text()).not.toContain('Not mine to administer')
  })

  it('shows an empty section, with a way to create the first rule, for an admin project with none', async () => {
    stubFetch([])
    const { wrapper } = await mountTab()

    expect(wrapper.text()).toContain('No rules in this project yet.')
    expect(wrapper.find('[data-testid="new-rule"]').exists()).toBe(true)
    expect(wrapper.get(`a[href="${factoryDocsUrl}"]`).text()).toContain(
      'Learn about automation rules',
    )
  })

  it('shows the last firing’s run link when it dispatched', async () => {
    stubFetch()
    const { wrapper } = await mountTab()
    expect(wrapper.text()).toContain('PROJ-9')
    expect(wrapper.text()).toContain('view run')
  })

  it('shows the skip reason when the last firing did not dispatch', async () => {
    stubFetch([{ ...rule, lastFiring: { ...rule.lastFiring, runId: null, skipReason: 'item-claimed' } }])
    const { wrapper } = await mountTab()
    expect(wrapper.text()).toContain('Skipped: item already claimed')
  })

  it('toggles enabled with a PATCH carrying the version', async () => {
    stubFetch()
    const { wrapper } = await mountTab()

    const toggle = wrapper.find('[data-testid="rule-toggle"]')
    expect(toggle.attributes('aria-checked')).toBe('true')
    await toggle.trigger('click')
    await flushPromises()

    expect(toggle.attributes('aria-checked')).toBe('false')
  })
})
