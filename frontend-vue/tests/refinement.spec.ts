import { flushPromises, mount, RouterLinkStub } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query'
import { afterEach, describe, expect, it, vi } from 'vitest'

import CreateTicketDialog from '@/components/items/CreateTicketDialog.vue'
import RefinementPanel from '@/components/items/RefinementPanel.vue'
import { useOrganizationsStore } from '@/stores/organizations'

/**
 * Creation saves the ticket as written. Refinement starts on an existing ticket's details;
 * its panel shows questions or the finished ticket and lets the person confirm it.
 */
type Call = { method: string; url: string; body: unknown }

function stubFetch(routes: [string, RegExp, unknown][]) {
  const calls: Call[] = []
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: unknown, init?: RequestInit) => {
      const url = String(input).split('?')[0] ?? ''
      const method = (init?.method ?? 'GET').toUpperCase()
      const body = typeof init?.body === 'string' ? JSON.parse(init.body) : (init?.body ?? null)
      calls.push({ method, url, body })
      for (const [verb, pattern, response] of routes) {
        if (verb === method && pattern.test(url)) {
          return new Response(JSON.stringify(response), {
            status: 200,
            headers: { 'content-type': 'application/json' },
          })
        }
      }
      return new Response(JSON.stringify({ title: 'Not found', status: 404 }), {
        status: 404,
        headers: { 'content-type': 'application/problem+json' },
      })
    }),
  )
  return calls
}

const settings = (enabled: boolean) => ({
  projectId: 'pr1',
  playbookId: enabled ? 'pb1' : null,
  agentId: null,
  productDescription: '',
  writingInstructions: '',
  namingConventions: '',
  platforms: '',
  refinedStateId: null,
  enabled,
  updatedAt: null,
  version: 1,
})

const item = {
  id: 'i1',
  key: 'PROJ-7',
  type: 'bug',
  title: 'Login button broken',
  stateId: 's-new',
  version: 3,
  claimedBy: null,
}

const refinement = (status: string, extra: Record<string, unknown> = {}) => ({
  id: 'rf1',
  itemId: 'i1',
  itemKey: 'PROJ-7',
  status,
  questions: [],
  answered: [],
  summary: null,
  lastRunId: 'run-1',
  requestedBy: 'u1',
  createdAt: '2026-10-01T00:00:00Z',
  updatedAt: '2026-10-01T00:00:00Z',
  confirmedAt: null,
  confirmedBy: null,
  refinedStateId: 's-ready',
  version: 11,
  ...extra,
})

const dialogStubs = {
  Dialog: { template: '<div><slot /></div>' },
  DialogContent: { template: '<div><slot /></div>' },
  DialogHeader: { template: '<div><slot /></div>' },
  DialogTitle: { template: '<div><slot /></div>' },
  DialogDescription: { template: '<div><slot /></div>' },
  DialogFooter: { template: '<div><slot /></div>' },
  MarkdownEditor: {
    props: ['modelValue'],
    emits: ['update:modelValue'],
    template:
      '<textarea data-testid="description" :value="modelValue" @input="$emit(\'update:modelValue\', $event.target.value)" />',
  },
}

function withOrganization(canOperateFactory: boolean) {
  const pinia = createPinia()
  setActivePinia(pinia)
  const organizations = useOrganizationsStore()
  organizations.organizations = [
    { id: 'org-1', slug: 'acme', name: 'Acme', role: 'member', canOperateFactory } as never,
  ]
  organizations.select('acme')
  return pinia
}

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('CreateTicketDialog', () => {
  async function mountDialog(canOperateFactory: boolean, enabled: boolean) {
    const calls = stubFetch([
      ['GET', /\/item-templates\/$/, []],
      ['GET', /\/refinement-settings\/$/, settings(enabled)],
      ['POST', /\/projects\/PROJ\/items\/$/, item],
      ['POST', /\/items\/PROJ-7\/refinement\/$/, refinement('refining')],
    ])
    const pinia = withOrganization(canOperateFactory)
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    const wrapper = mount(CreateTicketDialog, {
      props: { slug: 'acme', projectKey: 'PROJ', open: true, teamId: 'team-1' },
      global: { plugins: [pinia, [VueQueryPlugin, { queryClient }]], stubs: dialogStubs },
    })
    await flushPromises()
    return { wrapper, calls }
  }

  it.each([
    [true, true],
    [false, true],
    [true, false],
  ])('creates normally for factory permission %s and refinement enabled %s', async (canOperate, enabled) => {
    const { wrapper, calls } = await mountDialog(canOperate, enabled)
    expect(wrapper.find('[data-testid="create-ticket-refine"]').exists()).toBe(false)
    expect(wrapper.text()).not.toContain('Refine ticket')
    expect(wrapper.find('#create-ticket-title').attributes('placeholder')).toBe('What is it?')

    await wrapper.find('#create-ticket-type').setValue('bug')
    await wrapper.find('#create-ticket-title').setValue('  Login button broken  ')
    await wrapper.find('[data-testid="description"]').setValue('The login button does nothing')
    await wrapper.find('form').trigger('submit')
    await flushPromises()

    const posts = calls.filter((call) => call.method === 'POST')
    expect(posts).toHaveLength(1)
    expect(posts[0]).toMatchObject({
      url: '/api/v1/orgs/acme/projects/PROJ/items/',
      body: {
        type: 'bug',
        title: 'Login button broken',
        descriptionMarkdown: 'The login button does nothing',
        teamId: 'team-1',
      },
    })
    expect(wrapper.emitted('created')?.[0]?.[0]).toMatchObject({ key: 'PROJ-7' })
    expect(wrapper.emitted('update:open')).toEqual([[false]])
    expect(calls.some((call) => call.url.includes('refinement'))).toBe(false)
    wrapper.unmount()
  })

  it('requires a title even with a description and refinement configured', async () => {
    const { wrapper, calls } = await mountDialog(true, true)
    const create = wrapper.find('[data-testid="create-ticket-submit"]')
    expect(create.attributes('disabled')).toBeDefined()
    await wrapper.find('[data-testid="description"]').setValue('The login button does nothing')
    await wrapper.find('#create-ticket-title').setValue('   ')
    expect(create.attributes('disabled')).toBeDefined()
    await wrapper.find('form').trigger('submit')
    await flushPromises()
    expect(calls.some((call) => call.method === 'POST')).toBe(false)
    expect(wrapper.emitted('created')).toBeUndefined()

    await wrapper.find('#create-ticket-title').setValue('Login button broken')
    expect(create.attributes('disabled')).toBeUndefined()
    wrapper.unmount()
  })
})

describe('RefinementPanel', () => {
  async function mountPanel(current: unknown, dirty = false, canOperateFactory = true) {
    const calls = stubFetch([
      ['GET', /\/refinement-settings\/$/, settings(true)],
      ['GET', /\/items\/PROJ-7\/refinement\/$/, current],
      ['POST', /\/items\/PROJ-7\/refinement\/$/, refinement('refining')],
      ['POST', /\/items\/PROJ-7\/transition$/, { ...item, stateId: 's-ready', version: 4 }],
      ['POST', /\/items\/PROJ-7\/refinement\/confirm$/, refinement('confirmed')],
    ])
    const pinia = withOrganization(canOperateFactory)
    const queryClient = new QueryClient()
    const wrapper = mount(RefinementPanel, {
      props: { slug: 'acme', projectKey: 'PROJ', item: item as never, dirty, busy: false },
      global: {
        plugins: [pinia, [VueQueryPlugin, { queryClient }]],
        stubs: { RouterLink: RouterLinkStub },
      },
    })
    await flushPromises()
    return { wrapper, calls, queryClient }
  }

  it('asks the agent questions and sends the answers back with a new refinement', async () => {
    const { wrapper, calls } = await mountPanel(
      refinement('needsInput', { questions: ['Which platform?', 'Which browser?'] }),
    )
    expect(wrapper.text()).toContain('Needs your input')
    const answers = wrapper.findAll('[data-testid="refinement-answer"]')
    expect(answers).toHaveLength(2)

    await answers[0]!.setValue('iOS 17')
    await wrapper.find('[data-testid="refinement-send-answers"]').trigger('submit')
    await flushPromises()

    const refine = calls.find(
      (call) => call.method === 'POST' && call.url.endsWith('/items/PROJ-7/refinement/'),
    )
    expect(refine?.body).toEqual({
      answers: [
        { question: 'Which platform?', answer: 'iOS 17' },
        { question: 'Which browser?', answer: '' },
      ],
    })
  })

  it('moves a confirmed ticket to the refined state, then confirms it', async () => {
    const { wrapper, calls } = await mountPanel(
      refinement('ready', { summary: 'Wrote steps and criteria.' }),
    )
    expect(wrapper.text()).toContain('Wrote steps and criteria.')
    await wrapper.find('[data-testid="refinement-confirm"]').trigger('click')
    await flushPromises()

    const posts = calls.filter((call) => call.method === 'POST').map((call) => call.url)
    expect(posts[0]).toMatch(/\/items\/PROJ-7\/transition$/)
    expect(posts[1]).toMatch(/\/refinement\/confirm$/)
    expect(calls.find((call) => call.url.endsWith('/transition'))?.body).toEqual({
      toStateId: 's-ready',
      version: 3,
    })
  })

  it('will not confirm over unsaved edits', async () => {
    const { wrapper } = await mountPanel(refinement('ready'), true)
    expect(wrapper.find('[data-testid="refinement-confirm"]').attributes('disabled')).toBeDefined()
  })

  it.each(['refining', 'needsInput', 'ready', 'failed', 'confirmed'])(
    'hides %s refinement and makes no refinement requests for stakeholders',
    async (status) => {
      const { wrapper, calls } = await mountPanel(
        refinement(status, { questions: ['Which platform?'], summary: 'Internal refinement' }),
        false,
        false,
      )
      expect(wrapper.text()).toBe('')
      expect(wrapper.find('[data-testid="refinement-panel"]').exists()).toBe(false)
      expect(wrapper.find('[data-testid="refinement-start"]').exists()).toBe(false)
      expect(calls).toHaveLength(0)
    },
  )

  it('hides cached refinement when factory permission is revoked', async () => {
    const { wrapper, calls, queryClient } = await mountPanel(refinement('ready'))
    expect(wrapper.find('[data-testid="refinement-confirm"]').exists()).toBe(true)
    const organizations = useOrganizationsStore()
    organizations.organizations[0]!.canOperateFactory = false
    await flushPromises()
    const previousCalls = calls.length
    await queryClient.invalidateQueries()
    await flushPromises()

    expect(wrapper.text()).toBe('')
    expect(calls).toHaveLength(previousCalls)
  })

  it('starts refinement from the details of an item that was never refined', async () => {
    const { wrapper, calls } = await mountPanel(null)
    expect(wrapper.find('[data-testid="refinement-panel"]').exists()).toBe(false)
    await wrapper.find('[data-testid="refinement-start"]').trigger('click')
    await flushPromises()
    expect(
      calls.filter((call) => call.method === 'POST').map((call) => call.url),
    ).toEqual(['/api/v1/orgs/acme/items/PROJ-7/refinement/'])
  })
})
