import { flushPromises, mount } from '@vue/test-utils'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import StartRunDialog from '@/components/factory/StartRunDialog.vue'

/**
 * Starting a run should be two clicks, so the dialog's defaults carry the weight: the
 * playbook and agent this project used last — or its defaults, when nothing is
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
    expect(JSON.parse(String(init?.body))).toEqual({ playbookId: 'p1', agentId: 'a2' })

    expect(wrapper.emitted('dispatched')).toHaveLength(1)
    expect(wrapper.emitted('update:open')?.at(-1)).toEqual([false])
    expect(storage.get('aictiq.run.PROJ')).toBe(JSON.stringify({ playbookId: 'p1', agentId: 'a2' }))
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
})
