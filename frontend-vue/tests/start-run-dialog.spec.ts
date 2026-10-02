import { flushPromises, mount } from '@vue/test-utils'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import StartRunDialog from '@/components/factory/StartRunDialog.vue'

/**
 * Starting a run should be two clicks, so the dialog's defaults carry the weight: the
 * playbook and agent this project used last - or its defaults, when nothing is
 * remembered. What it refuses to guess, it says: no playbook, no agent, or a claim that
 * got there first is the server's word, shown as it arrived.
 */
const playbooks = [
  {
    id: 'p1',
    projectId: 'pr1',
    name: 'Fix the bug',
    wikiPageId: 'w1',
    harness: 'claude',
    onSuccessStateId: null,
    onFailureStateId: null,
    maxMinutes: 60,
    isDefault: true,
    createdBy: 'u1',
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
    version: 1,
  },
  { ...playbookLike('p2', 'Write the tests') },
]

function playbookLike(id: string, name: string) {
  return {
    id,
    projectId: 'pr1',
    name,
    wikiPageId: 'w1',
    harness: 'claude',
    onSuccessStateId: null,
    onFailureStateId: null,
    maxMinutes: 60,
    isDefault: false,
    createdBy: 'u1',
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
    version: 1,
  }
}

function agentLike(userId: string, displayName: string, isActive = true) {
  return {
    userId,
    displayName,
    email: null,
    ownerUserId: 'u9',
    ownerName: 'Rona',
    role: 'member',
    isActive,
    createdAt: '2026-01-01T00:00:00Z',
    lastActiveAt: null,
    tokenCount: 1,
  }
}

function runnerLike(id: string, name: string, isOnline = true, harnesses = ['claude', 'codex']) {
  return { id, name, harnesses, isOnline }
}

const agents = [
  agentLike('a1', 'claude-dev'),
  agentLike('a2', 'codex-dev'),
  agentLike('a3', 'retired-dev', false),
]

const members = [
  { userId: 'a1', displayName: 'claude-dev', email: null, avatarKey: null, isAgent: true, role: 'member', isImplicit: false, addedAt: null },
  { userId: 'a2', displayName: 'codex-dev', email: null, avatarKey: null, isAgent: true, role: 'member', isImplicit: false, addedAt: null },
]

function stubFetch(routes: Record<string, unknown>) {
  const fetchMock = vi.fn(async (input: unknown, _init?: RequestInit) => {
    const url = String(input)
    for (const [pattern, body] of Object.entries(routes)) {
      if (url.includes(pattern)) {
        return new Response(JSON.stringify(body), {
          status: 200,
          headers: { 'content-type': 'application/json' },
        })
      }
    }
    return new Response(JSON.stringify({ title: 'Not found' }), {
      status: 404,
      headers: { 'content-type': 'application/problem+json' },
    })
  })
  vi.stubGlobal('fetch', fetchMock)
  return fetchMock
}

const storage = new Map<string, string>()

beforeEach(() => {
  // happy-dom's localStorage is not usable here; every spec that needs it stubs its own.
  vi.stubGlobal('localStorage', {
    getItem: (key: string) => storage.get(key) ?? null,
    setItem: (key: string, value: string) => void storage.set(key, value),
    removeItem: (key: string) => void storage.delete(key),
  })
})

afterEach(() => {
  storage.clear()
  vi.unstubAllGlobals()
})

const dialogStubs = {
  Dialog: { template: '<div><slot /></div>' },
  DialogContent: { template: '<div><slot /></div>' },
  DialogHeader: { template: '<div><slot /></div>' },
  DialogTitle: { template: '<div><slot /></div>' },
  DialogDescription: { template: '<div><slot /></div>' },
  DialogFooter: { template: '<div><slot /></div>' },
}

const mountDialog = async (props: Record<string, unknown> = {}) => {
  const wrapper = mount(StartRunDialog, {
    props: { slug: 'acme', projectKey: 'PROJ', itemKey: 'PROJ-1', open: true, ...props },
    global: { stubs: dialogStubs },
  })
  await flushPromises()
  return wrapper
}

describe('StartRunDialog', () => {
  it('preselects the project default playbook and the settings default agent', async () => {
    stubFetch({
      '/playbooks': playbooks,
      '/agents': agents,
      '/factory-settings': { defaultAgentId: 'a2' },
      '/members': members,
    })

    const wrapper = await mountDialog()
    const selects = wrapper.findAll('select')

    expect((selects[0]!.element as HTMLSelectElement).value).toBe('p1')
    expect((selects[1]!.element as HTMLSelectElement).value).toBe('a2')
  })

  it('prefers what this project used last, when it still exists', async () => {
    stubFetch({
      '/playbooks': playbooks,
      '/agents': agents,
      '/factory-settings': { defaultAgentId: 'a2' },
      '/members': members,
    })
    storage.set('aictiq.run.PROJ', JSON.stringify({ playbookId: 'p2', agentId: 'a1' }))

    const wrapper = await mountDialog()
    const selects = wrapper.findAll('select')

    expect((selects[0]!.element as HTMLSelectElement).value).toBe('p2')
    expect((selects[1]!.element as HTMLSelectElement).value).toBe('a1')
  })

  it('forgets a remembered choice that names something gone', async () => {
    stubFetch({
      '/playbooks': playbooks,
      '/agents': agents,
      '/factory-settings': { defaultAgentId: null },
      '/members': members,
    })
    storage.set('aictiq.run.PROJ', JSON.stringify({ playbookId: 'gone', agentId: 'a3' }))

    const wrapper = await mountDialog()
    const selects = wrapper.findAll('select')

    expect((selects[0]!.element as HTMLSelectElement).value).toBe('p1')
    // An inactive agent is not assignable, whatever the dialog once sent here.
    expect((selects[1]!.element as HTMLSelectElement).value).toBe('a1')
  })

  it('sends the dispatch and remembers the choice that made it', async () => {
    const run = {
      id: 'r-1',
      projectId: 'pr1',
      itemId: 'i1',
      itemKey: 'PROJ-1',
      playbookId: 'p1',
      playbookName: 'Fix the bug',
      agentId: 'a2',
      agentName: 'codex-dev',
      requestedBy: 'u1',
      runnerId: null,
      runnerName: null,
      status: 'queued',
      harness: 'claude',
      playbookRevisionId: null,
      maxMinutes: 60,
      queuedAt: '2026-09-01T00:00:00Z',
      assignedAt: null,
      startedAt: null,
      finishedAt: null,
      lastHeartbeatAt: null,
      cancelRequested: false,
      outcomeSummary: null,
      pullRequestUrl: null,
      exitCode: null,
      costUsd: null,
      inputTokens: null,
      outputTokens: null,
      failureReason: null,
      promptSnapshot: null,
      version: 1,
    }
    const fetchMock = stubFetch({
      '/playbooks': playbooks,
      '/agents': agents,
      '/factory-settings': { defaultAgentId: 'a2' },
      '/members': members,
      '/items/PROJ-1/runs': run,
    })

    const wrapper = await mountDialog()
    await wrapper.find('form#start-run').trigger('submit.prevent')
    await flushPromises()

    const [url, init] = fetchMock.mock.calls.find(
      ([input]) => String(input).endsWith('/items/PROJ-1/runs'),
    )!
    expect(String(url)).toBe('/api/v1/orgs/acme/items/PROJ-1/runs')
    expect(init?.method).toBe('POST')
    // One runner or none is no choice, so the run goes to any free runner.
    expect(JSON.parse(String(init?.body))).toEqual({ playbookId: 'p1', agentId: 'a2', runnerId: null })

    expect(wrapper.emitted('dispatched')).toHaveLength(1)
    expect(wrapper.emitted('update:open')?.at(-1)).toEqual([false])
    expect(storage.get('aictiq.run.PROJ')).toBe(
      JSON.stringify({ playbookId: 'p1', agentId: 'a2', runnerId: null }),
    )
  })

  it('offers no runner choice with a single runner', async () => {
    stubFetch({
      '/playbooks': playbooks,
      '/agents': agents,
      '/factory-settings': { defaultAgentId: 'a2' },
      '/members': members,
      '/runners/choices': [runnerLike('rn1', 'laptop')],
    })

    const wrapper = await mountDialog()

    expect(wrapper.find('[data-testid="start-run-runner"]').exists()).toBe(false)
  })

  it('sends the run to the chosen runner, offering only runners that have the playbook harness', async () => {
    const fetchMock = stubFetch({
      '/playbooks': playbooks,
      '/agents': agents,
      '/factory-settings': { defaultAgentId: 'a2' },
      '/members': members,
      '/runners/choices': [
        runnerLike('rn1', 'hetzner-vm'),
        runnerLike('rn2', 'laptop', false),
        runnerLike('rn3', 'codex-box', true, ['codex']),
      ],
      '/items/PROJ-1/runs': { id: 'r-1' },
    })

    const wrapper = await mountDialog()
    const select = wrapper.find('[data-testid="start-run-runner"]')
    expect((select.element as HTMLSelectElement).selectedIndex).toBe(0)
    const labels = select.findAll('option').map((option) => option.text())
    expect(labels).toEqual(['Any free runner', 'hetzner-vm · online', 'laptop · offline'])

    await select.setValue('rn2')
    expect(wrapper.text()).toContain('laptop is offline. The run waits in the queue until it comes back.')

    await wrapper.find('form#start-run').trigger('submit.prevent')
    await flushPromises()

    const [, init] = fetchMock.mock.calls.find(([input]) => String(input).endsWith('/items/PROJ-1/runs'))!
    expect(JSON.parse(String(init?.body))).toEqual({ playbookId: 'p1', agentId: 'a2', runnerId: 'rn2' })
    expect(JSON.parse(storage.get('aictiq.run.PROJ')!)).toMatchObject({ runnerId: 'rn2' })
  })

  it('preselects the runner this project used last', async () => {
    stubFetch({
      '/playbooks': playbooks,
      '/agents': agents,
      '/factory-settings': { defaultAgentId: 'a2' },
      '/members': members,
      '/runners/choices': [runnerLike('rn1', 'hetzner-vm'), runnerLike('rn2', 'laptop')],
    })
    storage.set('aictiq.run.PROJ', JSON.stringify({ playbookId: 'p1', agentId: 'a1', runnerId: 'rn2' }))

    const wrapper = await mountDialog()

    expect((wrapper.find('[data-testid="start-run-runner"]').element as HTMLSelectElement).value).toBe('rn2')
  })

  it('sends no run when the project has no playbook, and says where to make one', async () => {
    const fetchMock = stubFetch({
      '/playbooks': [],
      '/agents': agents,
      '/factory-settings': { defaultAgentId: 'a2' },
      '/members': members,
    })

    const wrapper = await mountDialog()

    expect(wrapper.text()).toContain('No playbook yet')
    expect(wrapper.find('[data-testid="start-run-create-playbook"]').exists()).toBe(true)
    expect(wrapper.find('form#start-run').exists()).toBe(false)
    expect(fetchMock.mock.calls.every(([input]) => !String(input).includes('/items/PROJ-1/runs'))).toBe(true)
  })

  it('sends no run when no active agent can see the project', async () => {
    stubFetch({
      '/playbooks': playbooks,
      '/agents': agents,
      '/factory-settings': { defaultAgentId: null },
      '/members': [{ ...members[0]!, userId: 'u1', isAgent: false }],
    })

    const wrapper = await mountDialog()

    expect(wrapper.text()).toContain('No agent on this project')
    expect(wrapper.find('form#start-run').exists()).toBe(false)
  })

  it('shows a dispatch refused for a claim that got there first', async () => {
    stubFetch({
      '/playbooks': playbooks,
      '/agents': agents,
      '/factory-settings': { defaultAgentId: 'a2' },
      '/members': members,
    })
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: unknown, init?: RequestInit) => {
        const url = String(input)
        if (url.endsWith('/items/PROJ-1/runs')) {
          return new Response(
            JSON.stringify({
              type: 'https://aictiq.com/problems/item-claimed',
              title: 'This item already has a live claim or run.',
              status: 409,
            }),
            { status: 409, headers: { 'content-type': 'application/problem+json' } },
          )
        }
        for (const [pattern, body] of Object.entries({
          '/playbooks': playbooks,
          '/agents': agents,
          '/factory-settings': { defaultAgentId: 'a2' },
          '/members': members,
        })) {
          if (url.includes(pattern)) {
            return new Response(JSON.stringify(body), {
              status: 200,
              headers: { 'content-type': 'application/json' },
            })
          }
        }
        throw new Error(`unexpected ${url} ${JSON.stringify(init)}`)
      }),
    )

    const wrapper = await mountDialog()
    await wrapper.find('form#start-run').trigger('submit.prevent')
    await flushPromises()

    expect(wrapper.text()).toContain('This item already has a live claim or run.')
    expect(wrapper.emitted('dispatched')).toBeUndefined()
  })
  describe('Start later', () => {
    const routes = {
      '/playbooks': playbooks,
      '/agents': agents,
      '/factory-settings': { defaultAgentId: 'a2' },
      '/members': members,
    }

    beforeEach(() => {
      vi.useFakeTimers({ toFake: ['Date'] })
      vi.setSystemTime(new Date(2026, 9, 2, 14, 30))
    })

    afterEach(() => {
      vi.useRealTimers()
    })

    it('starts now by default and prefills now + 6 h, in local time, when turned on', async () => {
      stubFetch(routes)
      const wrapper = await mountDialog()
      expect(wrapper.find('[data-testid="start-run-start-at"]').exists()).toBe(false)

      await wrapper.find('[data-testid="start-run-schedule"]').setValue(true)

      const input = wrapper.find('[data-testid="start-run-start-at"]')
      expect((input.element as HTMLInputElement).value).toBe('2026-10-02T20:30')
      expect(wrapper.find('[data-testid="start-run-timezone"]').text()).toBe(
        Intl.DateTimeFormat().resolvedOptions().timeZone,
      )
    })

    it('sends the chosen local time as UTC', async () => {
      const fetchMock = stubFetch({
        ...routes,
        '/items/PROJ-1/runs': { id: 'r-1', scheduledFor: '2026-10-02T20:00:00Z' },
      })
      const wrapper = await mountDialog()
      await wrapper.find('[data-testid="start-run-schedule"]').setValue(true)
      await wrapper.find('[data-testid="start-run-start-at"]').setValue('2026-10-02T22:00')
      await wrapper.find('form#start-run').trigger('submit.prevent')
      await flushPromises()

      const [, init] = fetchMock.mock.calls.find(([input]) =>
        String(input).endsWith('/items/PROJ-1/runs'),
      )!
      expect(JSON.parse(String(init?.body))).toEqual({
        playbookId: 'p1',
        agentId: 'a2',
        runnerId: null,
        scheduledFor: new Date(2026, 9, 2, 22, 0).toISOString(),
      })
      expect(wrapper.emitted('dispatched')).toHaveLength(1)
    })

    it('refuses a time in the past without asking the server', async () => {
      const fetchMock = stubFetch(routes)
      const wrapper = await mountDialog()
      await wrapper.find('[data-testid="start-run-schedule"]').setValue(true)
      await wrapper.find('[data-testid="start-run-start-at"]').setValue('2026-10-02T09:00')
      await wrapper.find('form#start-run').trigger('submit.prevent')
      await flushPromises()

      expect(wrapper.text()).toContain('Choose a start time in the future.')
      expect(
        fetchMock.mock.calls.some(([input]) => String(input).endsWith('/items/PROJ-1/runs')),
      ).toBe(false)
      expect(wrapper.emitted('dispatched')).toBeUndefined()
    })
  })
})
