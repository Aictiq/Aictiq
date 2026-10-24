import { flushPromises, mount } from '@vue/test-utils'
import { createPinia } from 'pinia'
import { VueQueryPlugin } from '@tanstack/vue-query'
import { createMemoryHistory, createRouter } from 'vue-router'
import { afterEach, describe, expect, it, vi } from 'vitest'

import type { Run } from '@/api/runs'
import { formatScheduledTime, toDateTimeLocalValue } from '@/lib/runs'
import { useSessionStore } from '@/stores/session'
import RunDetailView from '@/views/factory/RunDetailView.vue'

// The page owns a SignalR connection; nothing here is about realtime.
vi.mock('@/composables/useRunRealtime', () => ({ useRunRealtime: () => {} }))
const { infoToast } = vi.hoisted(() => ({ infoToast: vi.fn() }))
vi.mock('@/composables/useToast', () => ({
  useToast: () => ({ info: infoToast, error: vi.fn() }),
}))

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

interface RunTestOptions {
  role?: string
  userId?: string
  rescheduleStatus?: number
}

function stubFetch(run: Run, options: RunTestOptions) {
  const fetchMock = vi.fn(async (input: unknown, init?: RequestInit) => {
    const url = String(input)
    if (url.endsWith('/reschedule') && init?.method === 'POST') {
      const status = options.rescheduleStatus ?? 204
      if (status === 204) {
        run.scheduledFor = JSON.parse(String(init.body)).scheduledFor
        return new Response(null, { status })
      }
      if (status === 409) run.status = 'running'
      return new Response(
        JSON.stringify({
          title: 'Cannot reschedule run',
          status,
          ...(status === 422
            ? { errors: { scheduledFor: ['Choose a start time in the future.'] } }
            : {}),
        }),
        { status, headers: { 'content-type': 'application/problem+json' } },
      )
    }
    const body =
      init?.method === 'POST'
        ? { ...run, id: 'r-3', status: 'queued' }
        : url.includes('/log')
          ? { items: [], truncated: false }
          : url.includes('/runs/')
            ? run
            : url.includes('/projects/')
              ? { id: 'pr1', key: 'PROJ', role: options.role ?? 'admin', isArchived: false }
              : {}
    return new Response(JSON.stringify(body), {
      status: init?.method === 'POST' ? 201 : 200,
      headers: { 'content-type': 'application/json' },
    })
  })
  vi.stubGlobal('fetch', fetchMock)
  return fetchMock
}

async function mountRun(run: Run, options: RunTestOptions = {}) {
  const fetchMock = stubFetch(run, options)
  const pinia = createPinia()
  useSessionStore(pinia).set({
    id: options.userId ?? 'u1',
    email: 'fixture@example.invalid',
    firstName: 'Fixture',
    lastName: 'Reviewer',
    fullName: 'Fixture Reviewer',
    roles: [],
    isAgent: false,
    avatarKey: null,
    timeZone: 'UTC',
  })
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
    global: {
      plugins: [pinia, router, VueQueryPlugin],
      stubs: {
        Dialog: { props: ['open'], template: '<div v-if="open"><slot /></div>' },
        DialogContent: { template: '<div><slot /></div>' },
        DialogHeader: { template: '<div><slot /></div>' },
        DialogTitle: { template: '<div><slot /></div>' },
        DialogDescription: { template: '<div><slot /></div>' },
        DialogFooter: { template: '<div><slot /></div>' },
      },
    },
  })
  await flushPromises()
  return { wrapper, router, fetchMock }
}

afterEach(() => {
  vi.unstubAllGlobals()
  vi.clearAllMocks()
})

const scheduled = (): Run => ({
  ...failed,
  status: 'queued',
  scheduledFor: new Date(Date.now() + 6 * 3_600_000).toISOString(),
  continuable: false,
  chain: null,
})

describe('RunDetailView rescheduling', () => {
  it.each([
    ['queued', new Date(Date.now() + 3_600_000).toISOString(), 'member', 'u1', true],
    ['queued', new Date(Date.now() + 3_600_000).toISOString(), 'admin', 'u2', true],
    ['queued', new Date(Date.now() + 3_600_000).toISOString(), 'member', 'u2', false],
    ['queued', null, 'admin', 'u1', false],
    ['queued', '2020-01-01T00:00:00Z', 'admin', 'u1', false],
    ['running', new Date(Date.now() + 3_600_000).toISOString(), 'admin', 'u1', false],
    ['cancelled', new Date(Date.now() + 3_600_000).toISOString(), 'admin', 'u1', false],
    ['succeeded', new Date(Date.now() + 3_600_000).toISOString(), 'admin', 'u1', false],
  ] as const)(
    'shows Edit time for %s at %s with role %s and user %s: %s',
    async (status, scheduledFor, role, userId, visible) => {
      const { wrapper } = await mountRun({ ...scheduled(), status, scheduledFor }, { role, userId })
      expect(wrapper.find('[data-testid="run-edit-time"]').exists()).toBe(visible)
      wrapper.unmount()
    },
  )

  it('prefills the existing time and saves a future time to the same run', async () => {
    const current = scheduled()
    const { wrapper, fetchMock } = await mountRun(current)
    await wrapper.get('[data-testid="run-edit-time"]').trigger('click')
    const input = wrapper.get<HTMLInputElement>('[data-testid="run-start-at"]')
    expect(input.element.value).toBe(toDateTimeLocalValue(new Date(current.scheduledFor!)))
    const at = new Date(Date.now() + 12 * 3_600_000)
    at.setSeconds(0, 0)
    await input.setValue(toDateTimeLocalValue(at))
    await wrapper.get('#reschedule-run').trigger('submit')
    await flushPromises()
    const posts = fetchMock.mock.calls.filter(([, init]) => init?.method === 'POST')
    expect(posts).toHaveLength(1)
    expect(String(posts[0]![0])).toBe('/api/v1/orgs/acme/runs/r-2/reschedule')
    expect(JSON.parse(String(posts[0]![1]!.body))).toEqual({ scheduledFor: at.toISOString() })
    expect(wrapper.find('#reschedule-run').exists()).toBe(false)
    expect(wrapper.get('[data-testid="run-scheduled"]').text()).toBe(
      `Scheduled for ${formatScheduledTime(at.toISOString())}`,
    )
    wrapper.unmount()
  })

  it.each(['', '2020-01-01T12:00', toDateTimeLocalValue(new Date())])(
    'rejects an empty, past or current time: %s',
    async (value) => {
      const { wrapper, fetchMock } = await mountRun(scheduled())
      await wrapper.get('[data-testid="run-edit-time"]').trigger('click')
      await wrapper.get('[data-testid="run-start-at"]').setValue(value)
      await wrapper.get('#reschedule-run').trigger('submit')
      expect(wrapper.get('[role="alert"]').text()).toBe(
        value ? 'Choose a start time in the future.' : 'Choose a date and time.',
      )
      expect(fetchMock.mock.calls.some(([, init]) => init?.method === 'POST')).toBe(false)
      wrapper.unmount()
    },
  )

  it('shows backend field validation on the date/time input', async () => {
    const { wrapper } = await mountRun(scheduled(), { rescheduleStatus: 422 })
    await wrapper.get('[data-testid="run-edit-time"]').trigger('click')
    await wrapper.get('#reschedule-run').trigger('submit')
    await flushPromises()
    expect(wrapper.get('[role="alert"]').text()).toBe('Choose a start time in the future.')
    expect(wrapper.get('[data-testid="run-start-at"]').attributes('aria-invalid')).toBe('true')
    wrapper.unmount()
  })

  it('explains a 409 and refreshes the page when the run has started', async () => {
    const { wrapper, fetchMock } = await mountRun(scheduled(), { rescheduleStatus: 409 })
    await wrapper.get('[data-testid="run-edit-time"]').trigger('click')
    const readsBeforeSave = fetchMock.mock.calls.filter(
      ([, init]) => !init?.method || init.method === 'GET',
    ).length
    await wrapper.get('#reschedule-run').trigger('submit')
    await flushPromises()
    expect(infoToast).toHaveBeenCalledWith('This run is no longer waiting for its start time.')
    expect(wrapper.find('#reschedule-run').exists()).toBe(false)
    expect(wrapper.find('[data-testid="run-edit-time"]').exists()).toBe(false)
    expect(
      fetchMock.mock.calls.filter(([, init]) => !init?.method || init.method === 'GET').length,
    ).toBeGreaterThan(readsBeforeSave)
    wrapper.unmount()
  })
})

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

  it('links a follow-up to the earlier run and the comment that asked for it', async () => {
    const { wrapper } = await mountRun({
      ...failed,
      continuesRunId: null,
      chain: null,
      triggerCommentId: 'c-9',
      followsUpRunId: 'r-0',
      branchName: 'proj-1-fix-2',
    })

    const trigger = wrapper.find('[data-testid="run-trigger-comment"]')
    expect(trigger.text()).toContain('Follows up')
    const links = trigger.findAll('a').map((link) => link.attributes('href'))
    expect(links).toEqual(['/o/acme/factory/runs/r-0', '/o/acme/p/PROJ/items/PROJ-1#comment-c-9'])
    expect(wrapper.find('[data-testid="run-branch"]').text()).toContain('proj-1-fix-2')
  })

  it('heads a stage run with its stage and verdict, and offers no Retry', async () => {
    const { wrapper } = await mountRun({
      ...failed,
      kind: 'stage',
      playbookName: 'Code review',
      status: 'succeeded',
      stageVerdict: 'pass',
      continuable: false,
      continuesRunId: null,
      chain: null,
    })

    expect(wrapper.get('[data-testid="run-stage-badge"]').text()).toBe('Stage: Code review')
    expect(wrapper.get('[data-testid="run-stage-verdict"]').text()).toBe('passed')
    expect(wrapper.get('[data-testid="run-kind"]').text()).toContain('stage')
    expect(wrapper.find('[data-testid="run-retry"]').exists()).toBe(false)
  })

  it('links a rework run to the stage whose findings it addresses', async () => {
    const { wrapper } = await mountRun({
      ...failed,
      reworkOfRunId: 'r-stage',
      continuesRunId: null,
      chain: null,
    })

    expect(wrapper.get('[data-testid="run-rework-badge"]').text()).toBe('Rework')
    expect(wrapper.get('[data-testid="run-rework-of"] a').attributes('href')).toBe(
      '/o/acme/factory/runs/r-stage',
    )
  })

  it('renders a project chat run without an item, and offers no Retry', async () => {
    const { wrapper } = await mountRun({
      ...failed,
      itemId: null,
      itemKey: null,
      kind: 'projectChat',
      playbookId: '00000000-0000-0000-0000-000000000000',
      playbookName: 'Project chat',
      continuable: false,
      continuesRunId: null,
      chain: null,
    })

    expect(wrapper.get('[data-testid="run-chat-label"]').text()).toBe('Chat')
    expect(wrapper.find('a[href*="/items/"]').exists()).toBe(false)
    expect(wrapper.get('[data-testid="run-kind"]').text()).toContain('project chat')
    expect(wrapper.find('[data-testid="run-retry"]').exists()).toBe(false)
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
