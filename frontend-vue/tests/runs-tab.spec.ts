import { flushPromises, mount } from '@vue/test-utils'
import { createPinia } from 'pinia'
import { VueQueryPlugin } from '@tanstack/vue-query'
import { createMemoryHistory, createRouter, type Router } from 'vue-router'
import { describe, expect, it, vi, afterEach } from 'vitest'
import { defineComponent, h, ref } from 'vue'

import RunsTab from '@/views/factory/RunsTab.vue'
import { orgScopeKey, type OrgScope } from '@/composables/useSettingsScope'
import type { Organization } from '@/api/organizations'
import { factoryDocsUrl } from '@/lib/factory'
import { localDay } from '@/lib/runStats'

// ECharts paints on a canvas jsdom does not have; the stand-in keeps the option and the click.
vi.mock('vue-echarts', () => ({
  default: defineComponent({
    name: 'VChart',
    props: { option: { type: Object, default: () => ({}) } },
    emits: ['click'],
    setup:
      (props, { attrs }) =>
      () =>
        h('div', { ...attrs, 'data-option': JSON.stringify(props.option) }),
  }),
}))

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

const today = localDay(new Date())

const stats = {
  total: 4,
  active: 1,
  finished: 3,
  succeeded: 2,
  totalCostUsd: 3.5,
  averageCostUsd: 1.1667,
  inputTokens: 1_250_000,
  outputTokens: 84_000,
  medianDurationSeconds: 420,
  p90DurationSeconds: 900,
  medianQueueWaitSeconds: 12,
  pullRequests: 2,
  days: [
    { day: today, status: 'succeeded', runs: 2, costUsd: 3 },
    { day: today, status: 'failed', runs: 1, costUsd: 0.5 },
    { day: today, status: 'running', runs: 1, costUsd: 0 },
  ],
  groupBy: 'agent',
  groups: [
    {
      key: 'a1',
      name: 'claude-dev',
      runs: 3,
      finished: 2,
      succeeded: 2,
      costUsd: 3,
      averageCostUsd: 1.5,
      medianDurationSeconds: 400,
    },
    {
      key: 'a2',
      name: 'codex-dev',
      runs: 1,
      finished: 1,
      succeeded: 0,
      costUsd: 0.5,
      averageCostUsd: 0.5,
      medianDurationSeconds: 900,
    },
  ],
  failureReasons: [{ reason: 'tests-failed', runs: 1 }],
  playbooks: [{ id: 'p1', name: 'Fix the bug' }],
  runners: [{ id: 'rn1', name: 'box-1' }],
}

const emptyStats = {
  ...stats,
  total: 0,
  active: 0,
  finished: 0,
  succeeded: 0,
  totalCostUsd: 0,
  averageCostUsd: null,
  inputTokens: 0,
  outputTokens: 0,
  medianDurationSeconds: null,
  p90DurationSeconds: null,
  medianQueueWaitSeconds: null,
  pullRequests: 0,
  days: [],
  groups: [],
  failureReasons: [],
}

function stubFetch() {
  const fetch = vi.fn(async (input: unknown) => {
    const url = String(input)
    const body = url.includes('/runs/stats')
      ? stats
      : url.includes('/runs')
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
  })
  vi.stubGlobal('fetch', fetch)
  return fetch
}

const requested = (fetch: ReturnType<typeof stubFetch>, path: string) =>
  fetch.mock.calls
    .map(([input]) => new URL(String(input), 'http://x'))
    .filter((url) => url.pathname.endsWith(path))

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
        async (input: unknown) =>
          new Response(
            JSON.stringify(
              String(input).includes('/runs/stats')
                ? emptyStats
                : { items: [], page: 1, pageSize: 25, totalCount: 0 },
            ),
            { status: 200, headers: { 'content-type': 'application/json' } },
          ),
      ),
    )
    const { wrapper } = await mountTab()

    expect(wrapper.text()).toContain('No runs yet')
    expect(wrapper.text()).toContain('register a runner')
    expect(wrapper.text()).toContain('write a playbook')
    expect(wrapper.text()).toContain('hand any item to an agent')
    expect(wrapper.get(`a[href="${factoryDocsUrl}"]`).text()).toContain('Factory guide')
  })

  it('shows the KPI row for the default 30 days and asks the stats and the list for the same runs', async () => {
    const fetch = stubFetch()
    const { wrapper } = await mountTab('?status=failed&kind=refine')

    const kpis = wrapper.get('[data-testid="run-stats-kpis"]')
    expect(kpis.get('[data-testid="kpi-runs"]').text()).toContain('4')
    expect(kpis.get('[data-testid="kpi-runs"]').text()).toContain('1 active')
    expect(kpis.get('[data-testid="kpi-success"]').text()).toContain('67 %')
    expect(kpis.get('[data-testid="kpi-cost"]').text()).toContain('$3.50')
    expect(kpis.get('[data-testid="kpi-tokens"]').text()).toContain('1.3M in')
    expect(kpis.get('[data-testid="kpi-duration"]').text()).toContain('7 min')
    expect(kpis.get('[data-testid="kpi-duration"]').text()).toContain('p90 15 min')
    expect(kpis.get('[data-testid="kpi-prs"]').text()).toContain('2')

    const statsUrl = requested(fetch, '/runs/stats').at(-1)!
    const listUrl = requested(fetch, '/runs').at(-1)!
    for (const key of ['status', 'kind', 'from']) {
      expect(statsUrl.searchParams.get(key)).toBe(listUrl.searchParams.get(key))
    }
    expect(statsUrl.searchParams.get('status')).toBe('failed')
    const from = new Date(statsUrl.searchParams.get('from')!)
    expect(Math.round((Date.now() - from.getTime()) / 86_400_000)).toBe(30)
    expect(statsUrl.searchParams.get('tz')).toBeTruthy()
  })

  it('keeps every new filter in the URL and reads it back', async () => {
    const fetch = stubFetch()
    const { wrapper, router } = await mountTab(
      '?kind=implement&playbook=p1&runner=rn1&range=7d&failure=tests-failed',
    )

    expect(
      (wrapper.get('select[aria-label="Filter by kind"]').element as HTMLSelectElement).value,
    ).toBe('implement')
    expect(
      (wrapper.get('select[aria-label="Filter by playbook"]').element as HTMLSelectElement).value,
    ).toBe('p1')
    expect(
      (wrapper.get('select[aria-label="Filter by runner"]').element as HTMLSelectElement).value,
    ).toBe('rn1')
    expect(
      (wrapper.get('select[aria-label="Filter by date range"]').element as HTMLSelectElement).value,
    ).toBe('7d')
    expect(wrapper.get('[data-testid="runs-filter-failure"]').text()).toContain('tests-failed')
    const listUrl = requested(fetch, '/runs').at(-1)!
    expect(listUrl.searchParams.get('playbook')).toBe('p1')
    expect(listUrl.searchParams.get('runner')).toBe('rn1')
    expect(listUrl.searchParams.get('failure')).toBe('tests-failed')

    const range = wrapper.get('select[aria-label="Filter by date range"]')
    ;(range.element as HTMLSelectElement).value = 'all'
    await range.trigger('change')
    await flushPromises()
    expect(router.currentRoute.value.query.range).toBe('all')
    expect(requested(fetch, '/runs').at(-1)!.searchParams.has('from')).toBe(false)
  })

  it('narrows the list from a breakdown row, a failure reason and a chart segment', async () => {
    stubFetch()
    const { wrapper, router } = await mountTab('?page=2')

    await wrapper.findAll('[data-testid="breakdown-row"]')[1]!.trigger('click')
    await flushPromises()
    expect(router.currentRoute.value.query).toEqual({ agent: 'a2' })

    await wrapper.get('[data-testid="breakdown-by-runner"]').trigger('click')
    await flushPromises()
    await wrapper.get('[data-testid="failure-reasons"] button').trigger('click')
    await flushPromises()
    expect(router.currentRoute.value.query.failure).toBe('tests-failed')

    const chart = wrapper.getComponent({ name: 'VChart' })
    const option = JSON.parse(chart.attributes('data-option')!) as { series: { id: string }[] }
    expect(option.series.map((series) => series.id)).toEqual(['running', 'succeeded', 'failed'])
    chart.vm.$emit('click', { seriesId: 'failed', dataIndex: 30 })
    await flushPromises()
    expect(router.currentRoute.value.query).toMatchObject({
      status: 'failed',
      from: today,
      to: today,
    })
    expect(
      (wrapper.get('select[aria-label="Filter by date range"]').element as HTMLSelectElement).value,
    ).toBe('custom')
  })

  it('remembers folding the charts away', async () => {
    stubFetch()
    // happy-dom does not provide `localStorage`.
    const stored = new Map<string, string>()
    vi.stubGlobal('localStorage', {
      getItem: (key: string) => stored.get(key) ?? null,
      setItem: (key: string, value: string) => stored.set(key, value),
    })
    const first = await mountTab()
    expect(first.wrapper.find('[data-testid="run-stats-charts"]').exists()).toBe(true)
    await first.wrapper.get('[data-testid="run-stats-toggle"]').trigger('click')
    expect(first.wrapper.find('[data-testid="run-stats-charts"]').exists()).toBe(false)
    first.wrapper.unmount()

    const second = await mountTab()
    expect(second.wrapper.find('[data-testid="run-stats-charts"]').exists()).toBe(false)
    expect(second.wrapper.find('[data-testid="kpi-runs"]').exists()).toBe(true)
  })

  it('shows dashes rather than errors when nothing matches, above the empty state', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(
        async (input: unknown) =>
          new Response(
            JSON.stringify(
              String(input).includes('/runs/stats')
                ? emptyStats
                : { items: [], page: 1, pageSize: 25, totalCount: 0 },
            ),
            { status: 200, headers: { 'content-type': 'application/json' } },
          ),
      ),
    )
    const { wrapper } = await mountTab('?status=failed')

    expect(wrapper.get('[data-testid="kpi-success"]').text()).toContain('–')
    expect(wrapper.get('[data-testid="kpi-duration"]').text()).toContain('–')
    expect(wrapper.text()).toContain('No runs match these filters')
  })
})
