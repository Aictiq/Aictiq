import { flushPromises, mount, RouterLinkStub } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { VueQueryPlugin } from '@tanstack/vue-query'
import { afterEach, describe, expect, it, vi } from 'vitest'

import CreateTicketDialog from '@/components/items/CreateTicketDialog.vue'
import RefinementPanel from '@/components/items/RefinementPanel.vue'
import { draftTitle } from '@/lib/refinement'
import { useOrganizationsStore } from '@/stores/organizations'

/**
 * Refining a ticket: the dialog files a short description and hands it to the refine
 * playbook; the panel on the item shows the agent's questions or its finished ticket, and
 * confirming moves the ticket where the project sends refined work.
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

describe('draftTitle', () => {
  it('takes the first line of prose, without Markdown', () => {
    expect(draftTitle('![shot](/a.png)\n\n## The **login** button does nothing\nmore')).toBe(
      'The login button does nothing',
    )
    expect(draftTitle('See [the docs](https://x.y) first')).toBe('See the docs first')
  })

  it('shortens a long line and names an empty one', () => {
    expect(draftTitle('x'.repeat(200))).toHaveLength(80)
    expect(draftTitle('  ![only an image](/a.png) ')).toBe('Untitled ticket')
  })
})

describe('CreateTicketDialog', () => {
  async function mountDialog(canOperateFactory: boolean, enabled: boolean) {
    const calls = stubFetch([
      ['GET', /\/refinement-settings\/$/, settings(enabled)],
      ['POST', /\/projects\/PROJ\/items\/$/, { ...item, title: 'The login button does nothing' }],
      ['POST', /\/items\/PROJ-7\/refinement\/$/, refinement('refining')],
    ])
    const pinia = withOrganization(canOperateFactory)
    const wrapper = mount(CreateTicketDialog, {
      props: { slug: 'acme', projectKey: 'PROJ', open: true },
      global: { plugins: [pinia, VueQueryPlugin], stubs: dialogStubs },
    })
    await flushPromises()
    return { wrapper, calls }
  }

  it('offers Refine ticket only to factory operators of a project that set it up', async () => {
    expect(
      (await mountDialog(true, true)).wrapper.find('[data-testid="create-ticket-refine"]').exists(),
    ).toBe(true)
    vi.unstubAllGlobals()
    expect(
      (await mountDialog(false, true)).wrapper
        .find('[data-testid="create-ticket-refine"]')
        .exists(),
    ).toBe(false)
    vi.unstubAllGlobals()
    expect(
      (await mountDialog(true, false)).wrapper
        .find('[data-testid="create-ticket-refine"]')
        .exists(),
    ).toBe(false)
  })

  it('needs a description to refine and a title to create by hand', async () => {
    const { wrapper } = await mountDialog(true, true)
    const refine = wrapper.find('[data-testid="create-ticket-refine"]')
    const create = wrapper.find('[data-testid="create-ticket-submit"]')
    expect(refine.attributes('disabled')).toBeDefined()
    expect(create.attributes('disabled')).toBeDefined()

    await wrapper.find('[data-testid="description"]').setValue('The login button does nothing')
    expect(refine.attributes('disabled')).toBeUndefined()
    expect(create.attributes('disabled')).toBeDefined()
  })

  it('files the ticket with a draft title, then asks for a refinement', async () => {
    const { wrapper, calls } = await mountDialog(true, true)
    await wrapper.find('select').setValue('bug')
    await wrapper.find('[data-testid="description"]').setValue('The login button does nothing')
    await wrapper.find('[data-testid="create-ticket-refine"]').trigger('click')
    await flushPromises()

    const created = calls.find((call) => call.method === 'POST' && call.url.endsWith('/items/'))
    expect(created?.body).toMatchObject({
      type: 'bug',
      title: 'The login button does nothing',
      descriptionMarkdown: 'The login button does nothing',
    })
    expect(
      calls.some(
        (call) => call.method === 'POST' && call.url.endsWith('/items/PROJ-7/refinement/'),
      ),
    ).toBe(true)
    expect(wrapper.emitted('created')?.[0]?.[0]).toMatchObject({ key: 'PROJ-7' })
  })

  it('creates by hand without refining', async () => {
    const { wrapper, calls } = await mountDialog(true, true)
    await wrapper.find('#create-ticket-title').setValue('Login button broken')
    await wrapper.find('form').trigger('submit')
    await flushPromises()

    expect(calls.some((call) => call.url.includes('/refinement/') && call.method === 'POST')).toBe(
      false,
    )
    expect(wrapper.emitted('created')).toHaveLength(1)
  })
})

describe('RefinementPanel', () => {
  async function mountPanel(current: unknown, dirty = false) {
    const calls = stubFetch([
      ['GET', /\/refinement-settings\/$/, settings(true)],
      ['GET', /\/items\/PROJ-7\/refinement\/$/, current],
      ['POST', /\/items\/PROJ-7\/refinement\/$/, refinement('refining')],
      ['POST', /\/items\/PROJ-7\/transition$/, { ...item, stateId: 's-ready', version: 4 }],
      ['POST', /\/items\/PROJ-7\/refinement\/confirm$/, refinement('confirmed')],
    ])
    const pinia = withOrganization(true)
    const wrapper = mount(RefinementPanel, {
      props: { slug: 'acme', projectKey: 'PROJ', item: item as never, dirty, busy: false },
      global: { plugins: [pinia, VueQueryPlugin], stubs: { RouterLink: RouterLinkStub } },
    })
    await flushPromises()
    return { wrapper, calls }
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

  it('offers Refine ticket on an item that was never refined', async () => {
    // A never-refined item answers 204 No Content.
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: unknown) => {
        const url = String(input)
        if (url.includes('/refinement-settings/'))
          return new Response(JSON.stringify(settings(true)), {
            status: 200,
            headers: { 'content-type': 'application/json' },
          })
        return new Response(null, { status: 204 })
      }),
    )
    const pinia = withOrganization(true)
    const wrapper = mount(RefinementPanel, {
      props: { slug: 'acme', projectKey: 'PROJ', item: item as never, dirty: false, busy: false },
      global: { plugins: [pinia, VueQueryPlugin], stubs: { RouterLink: RouterLinkStub } },
    })
    await flushPromises()
    expect(wrapper.find('[data-testid="refinement-panel"]').exists()).toBe(false)
    expect(wrapper.find('[data-testid="refinement-start"]').exists()).toBe(true)
  })
})
