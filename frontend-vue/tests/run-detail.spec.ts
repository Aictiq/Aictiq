import { flushPromises, mount } from '@vue/test-utils'
import { createPinia } from 'pinia'
import { VueQueryPlugin } from '@tanstack/vue-query'
import { createMemoryHistory, createRouter } from 'vue-router'
import { afterEach, describe, expect, it, vi } from 'vitest'

import type { Run } from '@/api/runs'
import RunDetailView from '@/views/factory/RunDetailView.vue'

// The page owns a SignalR connection; nothing here is about realtime.
vi.mock('@/composables/useRunRealtime', () => ({ useRunRealtime: () => {} }))

/**
 * A failed run's way forward: Continue resumes the agent's session where it stopped, Retry
 * starts over. Which of the two the page offers is the server's word (`continuable`,
 * `superseded`) plus the run's kind, and the chain shows what the whole attempt cost.
 */
const failed: Run = {
  id: 'r-2',
  projectId: 'pr1',
  itemId: 'i1',
  itemKey: 'PROJ-1',
  playbookId: 'p1',
  playbookName: 'Implement',
  agentId: 'a1',
  agentName: 'claude-dev',
  requestedBy: 'u1',
  ruleId: null,
  ruleName: null,
  runnerId: 'rn1',
  runnerName: 'box-1',
  requestedRunnerId: 'rn1',
  requestedRunnerName: 'box-1',
  status: 'failed',
  harness: 'claude',
  playbookRevisionId: null,
  maxMinutes: 60,
  queuedAt: '2026-10-01T10:05:00Z',
  assignedAt: '2026-10-01T10:05:01Z',
  startedAt: '2026-10-01T10:05:02Z',
  finishedAt: '2026-10-01T10:20:00Z',
  lastHeartbeatAt: null,
  cancelRequested: false,
  outcomeSummary: null,
  pullRequestUrl: null,
  exitCode: 1,
  costUsd: 0.1,
  inputTokens: 200,
  outputTokens: 20,
  failureReason: 'harness-rate-limited',
  promptSnapshot: 'Implement PROJ-1',
  version: 4,
  kind: 'implement',
  sessionId: 'sess-1',
  continuesRunId: 'r-1',
  continuedByRunId: null,
  autoContinued: true,
  continuable: true,
  superseded: false,
  chain: [
    {
      id: 'r-1',
      status: 'failed',
      autoContinued: false,
      queuedAt: '2026-10-01T10:00:00Z',
      finishedAt: '2026-10-01T10:04:00Z',
      costUsd: 0.4,
      inputTokens: 1000,
      outputTokens: 100,
    },
    {
      id: 'r-2',
      status: 'failed',
      autoContinued: true,
      queuedAt: '2026-10-01T10:05:00Z',
      finishedAt: '2026-10-01T10:20:00Z',
      costUsd: 0.1,
      inputTokens: 200,
      outputTokens: 20,
    },
  ],
}

function stubFetch(run: Run) {
  const fetchMock = vi.fn(async (input: unknown, init?: RequestInit) => {
    const url = String(input)
    const body =
      init?.method === 'POST'
        ? { ...run, id: 'r-3', status: 'queued' }
        : url.includes('/log')
          ? { items: [], truncated: false }
          : url.includes('/runs/')
            ? run
            : url.includes('/projects/')
              ? { id: 'pr1', key: 'PROJ', role: 'admin', isArchived: false }
              : {}
    return new Response(JSON.stringify(body), {
      status: init?.method === 'POST' ? 201 : 200,
      headers: { 'content-type': 'application/json' },
    })
  })
  vi.stubGlobal('fetch', fetchMock)
  return fetchMock
}

async function mountRun(run: Run) {
  const fetchMock = stubFetch(run)
  const router = createRouter({
    history: createMemoryHistory(),
    routes: [
      { path: '/o/:slug/factory/runs/:runId', component: RunDetailView },
      { path: '/:rest(.*)*', component: { template: '<div />' } },
    ],
  })
  await router.push(`/o/acme/factory/runs/${run.id}`)
  await router.isReady()
  const wrapper = mount(RunDetailView, {
    global: { plugins: [createPinia(), router, VueQueryPlugin] },
  })
  await flushPromises()
  return { wrapper, router, fetchMock }
}

afterEach(() => vi.unstubAllGlobals())

describe('RunDetailView continue and retry', () => {
  it('offers Continue and Retry on a continuable run and shows the chain with its total', async () => {
    const { wrapper } = await mountRun(failed)

    expect(wrapper.find('[data-testid="run-continue"]').exists()).toBe(true)
    expect(wrapper.find('[data-testid="run-retry"]').exists()).toBe(true)
    expect(wrapper.find('[data-testid="run-session"]').text()).toContain('sess-1')
    expect(wrapper.find('[data-testid="run-continues"]').text()).toContain('Auto-continues')

    const chain = wrapper.find('[data-testid="run-chain"]')
    expect(chain.findAll('li')).toHaveLength(2)
    expect(chain.text()).toContain('automatic')
    expect(wrapper.find('[data-testid="run-chain-total"]').text()).toBe(
      'Total $0.50 · 1.2k in · 120 out',
    )
  })

  it('continues the run and opens the new one', async () => {
    const { wrapper, router, fetchMock } = await mountRun(failed)

    await wrapper.find('[data-testid="run-continue"]').trigger('click')
    await flushPromises()

    const post = fetchMock.mock.calls.find(([, init]) => init?.method === 'POST')!
    expect(String(post[0])).toBe('/api/v1/orgs/acme/runs/r-2/continue')
    expect(router.currentRoute.value.fullPath).toBe('/o/acme/factory/runs/r-3')
  })

  it('retries with the same playbook and agent', async () => {
    const { wrapper, fetchMock } = await mountRun(failed)

    await wrapper.find('[data-testid="run-retry"]').trigger('click')
    await flushPromises()

    const post = fetchMock.mock.calls.find(([, init]) => init?.method === 'POST')!
    expect(String(post[0])).toBe('/api/v1/orgs/acme/items/PROJ-1/runs')
    expect(JSON.parse(String(post[1]!.body))).toEqual({ playbookId: 'p1', agentId: 'a1' })
  })

  it('offers only Retry when there is no session to continue', async () => {
    const { wrapper } = await mountRun({
      ...failed,
      failureReason: 'workspace-failed',
      sessionId: null,
      continuable: false,
      continuesRunId: null,
      chain: null,
    })

    expect(wrapper.find('[data-testid="run-continue"]').exists()).toBe(false)
    expect(wrapper.find('[data-testid="run-retry"]').exists()).toBe(true)
    expect(wrapper.find('[data-testid="run-chain"]').exists()).toBe(false)
  })

  it('offers neither once the run was continued, and links the next run', async () => {
    const { wrapper } = await mountRun({ ...failed, continuable: false, continuedByRunId: 'r-3' })

    expect(wrapper.find('[data-testid="run-continue"]').exists()).toBe(false)
    expect(wrapper.find('[data-testid="run-retry"]').exists()).toBe(false)
    expect(wrapper.find('[data-testid="run-continued-by"] a').attributes('href')).toBe(
      '/o/acme/factory/runs/r-3',
    )
  })
})
