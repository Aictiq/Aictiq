import { flushPromises, mount } from '@vue/test-utils'
import { createPinia } from 'pinia'
import { VueQueryPlugin } from '@tanstack/vue-query'
import { createMemoryHistory, createRouter, type Router } from 'vue-router'
import { describe, expect, it, vi, afterEach } from 'vitest'
import { ref } from 'vue'

import RunsTab from '@/views/factory/RunsTab.vue'
import { orgScopeKey, type OrgScope } from '@/composables/useSettingsScope'
import type { Organization } from '@/api/organizations'
import { factoryDocsUrl } from '@/lib/factory'

/**
 * The Runs tab's filters live in the URL, because "the failed runs on PROJ" is a link
 * someone pastes, not a state to recreate by hand. That is worth asserting from both
 * directions: a control moves the route, and a pasted route moves the controls.
 */
const runs = {
  items: [
    {
      id: 'r-1',
      projectId: 'pr1',
      itemId: 'i1',
      itemKey: 'PROJ-1',
      playbookId: 'p1',
      playbookName: 'Fix the bug',
      agentId: 'a1',
      agentName: 'claude-dev',
      requestedBy: 'u1',
      ruleId: null as string | null,
      ruleName: null as string | null,
      runnerId: null,
      runnerName: null,
      status: 'running',
      harness: 'claude',
      playbookRevisionId: null,
      maxMinutes: 60,
      queuedAt: '2026-09-01T00:00:00Z',
      assignedAt: null,
      startedAt: '2026-09-01T00:01:00Z',
      finishedAt: null,
      lastHeartbeatAt: null,
      cancelRequested: false,
      outcomeSummary: null,
      pullRequestUrl: 'https://github.com/acme/repo/pull/9',
      exitCode: null,
      costUsd: null,
      inputTokens: null,
      outputTokens: null,
      failureReason: null,
      promptSnapshot: null,
      version: 3,
    },
  ],
  page: 1,
  pageSize: 25,
  totalCount: 1,
}

const projects = [
  {
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
  },
]

const agents = [
  {
    userId: 'a1',
    displayName: 'claude-dev',
    email: null,
    ownerUserId: 'u9',
    ownerName: 'Rona',
    role: 'member',
    isActive: true,
    createdAt: '2026-01-01T00:00:00Z',
    lastActiveAt: null,
    tokenCount: 1,
  },
]

function stubFetch() {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: unknown) => {
      const url = String(input)
      const body = url.includes('/runs')
        ? runs
        : url.includes('/projects')
          ? projects
          : url.includes('/agents')
            ? agents
            : { title: 'Not found' }
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

async function mountTab(query = '') {
  const router: Router = createRouter({
    history: createMemoryHistory(),
    routes: [
      { path: '/o/:slug/factory/runs', component: RunsTab },
      { path: '/', component: { template: '<div />' } },
    ],
  })
  await router.push(`/o/acme/factory/runs${query}`)
  await router.isReady()

  const scope: OrgScope = {
    slug: ref('acme'),
    record: ref(organization),
    loading: ref(false),
    notFound: ref(false),
    reload: async () => {},
    set: () => {},
  }

  const wrapper = mount(RunsTab, {
    global: {
      plugins: [createPinia(), router, VueQueryPlugin],
      provide: { [orgScopeKey]: scope },
    },
  })
  await flushPromises()
  return { wrapper, router }
}

afterEach(() => vi.unstubAllGlobals())

describe('RunsTab', () => {
  it('lists runs with their status, item and pull request', async () => {
    stubFetch()
    const { wrapper } = await mountTab()

    const list = wrapper.find('[data-testid="runs-list"]')
    expect(list.text()).toContain('Running')
    expect(list.text()).toContain('PROJ-1')
    expect(list.text()).toContain('claude-dev')
    expect(list.find('a[href="https://github.com/acme/repo/pull/9"]').exists()).toBe(true)
  })

  it('names the rule that dispatched a run with no requester', async () => {
    const original = { ...runs.items[0]! }
    runs.items[0]!.requestedBy = null as unknown as string
    runs.items[0]!.ruleId = 'rule-1'
    runs.items[0]!.ruleName = 'Start implementation'
    stubFetch()

    const { wrapper } = await mountTab()

    expect(wrapper.find('[data-testid="runs-list"]').text()).toContain('Rule: Start implementation')

    Object.assign(runs.items[0]!, original)
  })

  it('rounds a filter change out to the URL, and resets the page with it', async () => {
    stubFetch()
    const { wrapper, router } = await mountTab('?page=3')

    const projectSelect = wrapper.find('select[aria-label="Filter by project"]')
    ;(projectSelect.element as HTMLSelectElement).value = 'PROJ'
    await projectSelect.trigger('change')
    await flushPromises()

    expect(router.currentRoute.value.query.project).toBe('PROJ')
    expect(router.currentRoute.value.query.page).toBeUndefined()
    expect(wrapper.find('[data-testid="runs-filters-clear"]').exists()).toBe(true)
  })

  it('reads a pasted URL back into its controls', async () => {
    stubFetch()
    const { wrapper, router } = await mountTab(
      '?project=PROJ&agent=a1&status=failed&itemKey=PROJ-9&page=2',
    )
    await flushPromises()

    expect(
      (wrapper.find('select[aria-label="Filter by project"]').element as HTMLSelectElement).value,
    ).toBe('PROJ')
    expect(
      (wrapper.find('select[aria-label="Filter by agent"]').element as HTMLSelectElement).value,
    ).toBe('a1')
    expect(
      (wrapper.find('select[aria-label="Filter by status"]').element as HTMLSelectElement).value,
    ).toBe('failed')
    expect(
      (wrapper.find('input[aria-label="Filter by item key"]').element as HTMLInputElement).value,
    ).toBe('PROJ-9')
    expect(wrapper.text()).toContain('page 2 of')
    void router
  })

  it("files the item filter under itemKey, never the item peek's ?item=", async () => {
    stubFetch()
    const { wrapper, router } = await mountTab()

    const input = wrapper.find('input[aria-label="Filter by item key"]')
    await input.setValue('PROJ-9')
    await input.trigger('change')
    await flushPromises()

    expect(router.currentRoute.value.query.itemKey).toBe('PROJ-9')
    expect(router.currentRoute.value.query.item).toBeUndefined()
  })

  it('clears every filter from the URL at once', async () => {
    stubFetch()
    const { wrapper, router } = await mountTab('?project=PROJ&status=failed')

    await wrapper.find('[data-testid="runs-filters-clear"]').trigger('click')
    await flushPromises()

    expect(router.currentRoute.value.query).toEqual({})
  })

  it('offers the three steps when the factory has never run', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(
        async () =>
          new Response(JSON.stringify({ items: [], page: 1, pageSize: 25, totalCount: 0 }), {
            status: 200,
            headers: { 'content-type': 'application/json' },
          }),
      ),
    )
    const { wrapper } = await mountTab()

    expect(wrapper.text()).toContain('No runs yet')
    expect(wrapper.text()).toContain('register a runner')
    expect(wrapper.text()).toContain('write a playbook')
    expect(wrapper.text()).toContain('hand any item to an agent')
    expect(wrapper.get(`a[href="${factoryDocsUrl}"]`).text()).toContain('Factory guide')
  })
})
