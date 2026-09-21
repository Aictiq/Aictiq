import { flushPromises, mount } from '@vue/test-utils'
import { afterEach, describe, expect, it, vi } from 'vitest'

import type { WorkflowState } from '@/api/workflows'
import RuleDialog from '@/components/factory/RuleDialog.vue'

/**
 * The rule dialog creates and edits, reusing the states and labels the Rules tab already
 * loaded and fetching its own playbooks and assignable agents — the same two lists
 * `StartRunDialog` loads. Editing sends only what changed, plus the version read with
 * the rule, so two edits never clobber a concurrent one blindly.
 */
const states: WorkflowState[] = [
  { id: 's-ready', name: 'Ready', category: 'active', position: 1, color: null, isInitial: false },
  { id: 's-done', name: 'Done', category: 'completed', position: 2, color: null, isInitial: false },
]

const labels = [
  { id: 'l-agent', name: 'agent', color: null, description: null, group: null, itemCount: 1, version: 1 },
]

const playbooks = [
  {
    id: 'p1',
    projectId: 'pr1',
    name: 'Implement',
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
]

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

const agents = [agentLike('a1', 'claude-dev')]
const members = [
  {
    userId: 'a1',
    displayName: 'claude-dev',
    email: null,
    avatarKey: null,
    isAgent: true,
    role: 'member',
    isImplicit: false,
    addedAt: null,
  },
]

const existingRule = {
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
  version: 5,
  lastFiring: null,
}

function stubFetch(routes: Record<string, unknown> = {}) {
  const fetchMock = vi.fn(async (input: unknown, init?: RequestInit) => {
    const url = String(input)
    if (/\/rules\/[^/]+$/.test(url) && init?.method === 'PATCH') {
      return new Response(JSON.stringify({ ...existingRule, ...JSON.parse(String(init.body)) }), {
        status: 200,
        headers: { 'content-type': 'application/json' },
      })
    }
    if (url.endsWith('/rules') && init?.method === 'POST') {
      return new Response(JSON.stringify({ ...existingRule, id: 'r-new', ...JSON.parse(String(init.body)) }), {
        status: 201,
        headers: { 'content-type': 'application/json' },
      })
    }
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

afterEach(() => vi.unstubAllGlobals())

const dialogStubs = {
  Dialog: { template: '<div><slot /></div>' },
  DialogContent: { template: '<div><slot /></div>' },
  DialogHeader: { template: '<div><slot /></div>' },
  DialogTitle: { template: '<div><slot /></div>' },
  DialogDescription: { template: '<div><slot /></div>' },
  DialogFooter: { template: '<div><slot /></div>' },
}

async function mountDialog(props: Record<string, unknown> = {}) {
  const wrapper = mount(RuleDialog, {
    props: {
      slug: 'acme',
      projectKey: 'PROJ',
      states,
      labels,
      rule: null,
      open: true,
      ...props,
    },
    global: { stubs: dialogStubs },
  })
  await flushPromises()
  return wrapper
}

describe('RuleDialog', () => {
  it('creates a rule with the chosen state, label, playbook and agent', async () => {
    const fetchMock = stubFetch({ '/playbooks': playbooks, '/agents': agents, '/members': members })
    const order: string[] = []
    const wrapper = await mountDialog({
      onSaved: () => order.push('saved'),
      'onUpdate:open': () => order.push('close'),
    })

    await wrapper.find('#rule-name').setValue('New rule')
    const selects = wrapper.findAll('select')
    await selects[0]!.setValue('s-done')
    await selects[1]!.setValue('l-agent')
    await wrapper.find('form#rule-form').trigger('submit.prevent')
    await flushPromises()

    const call = fetchMock.mock.calls.find(
      ([input, init]) => String(input).endsWith('/rules') && init?.method === 'POST',
    )!
    const body = JSON.parse(String(call[1]!.body))
    expect(body).toEqual({
      name: 'New rule',
      triggerStateId: 's-done',
      requiredLabelId: 'l-agent',
      playbookId: 'p1',
      agentId: 'a1',
      enabled: true,
    })
    expect(wrapper.emitted('saved')).toHaveLength(1)
    // The Rules tab forgets which project it was adding to once the dialog closes, so
    // the saved rule must reach it first.
    expect(order).toEqual(['saved', 'close'])
  })

  it('maps validation errors from the API onto the matching fields', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: unknown, init?: RequestInit) => {
        const url = String(input)
        if (url.endsWith('/rules') && init?.method === 'POST') {
          return new Response(
            JSON.stringify({
              title: 'Validation failed',
              status: 400,
              errors: { triggerStateId: ['A rule needs a trigger state of this project.'] },
            }),
            { status: 400, headers: { 'content-type': 'application/problem+json' } },
          )
        }
        for (const [pattern, body] of Object.entries({
          '/playbooks': playbooks,
          '/agents': agents,
          '/members': members,
        })) {
          if (url.includes(pattern)) {
            return new Response(JSON.stringify(body), {
              status: 200,
              headers: { 'content-type': 'application/json' },
            })
          }
        }
        throw new Error(`unexpected ${url}`)
      }),
    )

    const wrapper = await mountDialog()
    await wrapper.find('#rule-name').setValue('New rule')
    await wrapper.find('select').setValue('s-ready')
    await wrapper.find('form#rule-form').trigger('submit.prevent')
    await flushPromises()

    expect(wrapper.text()).toContain('A rule needs a trigger state of this project.')
    expect(wrapper.emitted('saved')).toBeUndefined()
  })

  it('edits by sending only the changed fields plus the version', async () => {
    const fetchMock = stubFetch({ '/playbooks': playbooks, '/agents': agents, '/members': members })
    const wrapper = await mountDialog({ rule: existingRule })

    const nameInput = wrapper.find('#rule-name')
    await nameInput.setValue('Renamed rule')
    await wrapper.find('form#rule-form').trigger('submit.prevent')
    await flushPromises()

    const call = fetchMock.mock.calls.find(
      ([input, init]) => /\/rules\/r1$/.test(String(input)) && init?.method === 'PATCH',
    )!
    expect(JSON.parse(String(call[1]!.body))).toEqual({ version: 5, name: 'Renamed rule' })
  })

  it('sends no PATCH fields beyond version when nothing changed', async () => {
    const fetchMock = stubFetch({ '/playbooks': playbooks, '/agents': agents, '/members': members })
    const wrapper = await mountDialog({ rule: existingRule })

    await wrapper.find('form#rule-form').trigger('submit.prevent')
    await flushPromises()

    const call = fetchMock.mock.calls.find(
      ([input, init]) => /\/rules\/r1$/.test(String(input)) && init?.method === 'PATCH',
    )!
    expect(JSON.parse(String(call[1]!.body))).toEqual({ version: 5 })
  })
})
