import { flushPromises, mount, RouterLinkStub } from '@vue/test-utils'
import { createPinia } from 'pinia'
import { ref } from 'vue'
import { afterEach, describe, expect, it, vi } from 'vitest'

import type { Organization } from '@/api/organizations'
import type { Playbook } from '@/api/playbooks'
import { orgScopeKey, type OrgScope } from '@/composables/useSettingsScope'
import FactoryPlaybooksView from '@/views/factory/FactoryPlaybooksView.vue'

/**
 * Follow-up playbooks are for the few who chain a review or QA pass after the builder: a
 * playbook without stages shows one quiet "Add follow-up playbook" button and nothing else,
 * and every change to a chain saves its whole new order.
 */
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

const project = {
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

const playbook = (id: string, name: string, stagePlaybookIds: string[] = []): Playbook => ({
  id,
  projectId: 'pr1',
  name,
  wikiPageId: `page-${id}`,
  harness: 'claude',
  onSuccessStateId: null,
  onFailureStateId: null,
  workOnDefaultBranch: false,
  maxMinutes: 60,
  isDefault: id === 'build',
  stagePlaybookIds,
  createdBy: 'u1',
  createdAt: '2026-10-01T00:00:00Z',
  updatedAt: '2026-10-01T00:00:00Z',
  version: 1,
})

function stubFetch(playbooks: Playbook[], role = 'admin', refuse = false) {
  const fetchMock = vi.fn(async (input: unknown, init?: RequestInit) => {
    const url = String(input).split('?')[0] ?? ''
    if (init?.method === 'PUT' && url.endsWith('/stages')) {
      if (refuse) {
        return new Response(
          JSON.stringify({
            title: 'Validation failed',
            status: 422,
            errors: { stagePlaybookIds: ['A playbook cannot follow itself.'] },
          }),
          { status: 422, headers: { 'content-type': 'application/problem+json' } },
        )
      }
      const id = url.split('/').at(-2)!
      const { stagePlaybookIds } = JSON.parse(String(init.body)) as { stagePlaybookIds: string[] }
      const saved = { ...playbooks.find((entry) => entry.id === id)!, stagePlaybookIds }
      return new Response(JSON.stringify(saved), {
        status: 200,
        headers: { 'content-type': 'application/json' },
      })
    }
    const body = url.endsWith('/projects')
      ? [{ ...project, role }]
      : url.endsWith('/playbooks')
        ? playbooks
        : []
    return new Response(JSON.stringify(body), {
      status: 200,
      headers: { 'content-type': 'application/json' },
    })
  })
  vi.stubGlobal('fetch', fetchMock)
  return fetchMock
}

/** Menus and dialogs render inline, so a test can pick a follow-up without opening a portal. */
async function mountPlaybooks() {
  const scope: OrgScope = {
    slug: ref('acme'),
    record: ref(organization),
    loading: ref(false),
    notFound: ref(false),
    reload: async () => {},
    set: () => {},
  }
  const passthrough = { template: '<div><slot /></div>' }
  const wrapper = mount(FactoryPlaybooksView, {
    global: {
      plugins: [createPinia()],
      provide: { [orgScopeKey]: scope },
      stubs: {
        RouterLink: RouterLinkStub,
        DropdownMenu: passthrough,
        DropdownMenuTrigger: passthrough,
        DropdownMenuContent: passthrough,
        DropdownMenuItem: {
          template: '<button type="button" @click="$emit(\'select\')"><slot /></button>',
          emits: ['select'],
        },
        Dialog: passthrough,
        DialogContent: passthrough,
        DialogDescription: passthrough,
        DialogFooter: passthrough,
        DialogHeader: passthrough,
        DialogTitle: passthrough,
        MarkdownEditor: true,
      },
    },
  })
  await flushPromises()
  return wrapper
}

const putBodies = (fetchMock: ReturnType<typeof stubFetch>) =>
  fetchMock.mock.calls
    .filter(([, init]) => init?.method === 'PUT')
    .map(([input, init]) => [String(input), JSON.parse(String(init!.body))])

afterEach(() => vi.unstubAllGlobals())

describe('FactoryPlaybooksView follow-up playbooks', () => {
  it('shows no chain on a playbook without stages, only the add button', async () => {
    stubFetch([playbook('build', 'Build'), playbook('review', 'Code review')])
    const wrapper = await mountPlaybooks()

    expect(wrapper.find('[data-testid="playbook-stage-list"]').exists()).toBe(false)
    expect(wrapper.find('[data-testid="playbook-stage-hint"]').exists()).toBe(false)
    expect(wrapper.findAll('[data-testid="playbook-add-stage"]')).toHaveLength(2)
  })

  it('offers only other playbooks not already in the chain, and saves the new order', async () => {
    const fetchMock = stubFetch([
      playbook('build', 'Build', ['review']),
      playbook('review', 'Code review'),
      playbook('qa', 'QA'),
    ])
    const wrapper = await mountPlaybooks()

    const build = wrapper.findAll('ul > li')[0]!
    const options = build.findAll('[data-testid="playbook-add-stage-option"]')
    expect(options.map((option) => option.text())).toEqual(['QA'])

    await options[0]!.trigger('click')
    await flushPromises()

    expect(putBodies(fetchMock)).toEqual([
      [
        '/api/v1/orgs/acme/projects/PROJ/playbooks/build/stages',
        { stagePlaybookIds: ['review', 'qa'] },
      ],
    ])
    const list = wrapper.get('[data-testid="playbook-stage-list"]')
    expect(list.text()).toContain('1. Code review')
    expect(list.text()).toContain('2. QA')
    expect(wrapper.get('[data-testid="playbook-stage-hint"]').text()).toBe(
      "Runs in order after this playbook's pull request, before human review.",
    )
    // Every playbook of the project is already in the chain.
    expect(wrapper.findAll('ul > li')[0]!.find('[data-testid="playbook-add-stage"]').exists()).toBe(
      false,
    )
  })

  it('moves and removes stages, saving the full order each time', async () => {
    const fetchMock = stubFetch([
      playbook('build', 'Build', ['review', 'qa']),
      playbook('review', 'Code review'),
      playbook('qa', 'QA'),
    ])
    const wrapper = await mountPlaybooks()

    const stages = () => wrapper.findAll('[data-testid="playbook-stage"]')
    expect(stages()[0]!.find('[data-testid="playbook-stage-up"]').exists()).toBe(false)
    expect(stages()[1]!.find('[data-testid="playbook-stage-down"]').exists()).toBe(false)

    await stages()[1]!.get('[data-testid="playbook-stage-up"]').trigger('click')
    await flushPromises()
    expect(stages().map((stage) => stage.text())).toEqual(['1. QA', '2. Code review'])

    await stages()[0]!.get('[data-testid="playbook-stage-remove"]').trigger('click')
    await flushPromises()

    expect(putBodies(fetchMock).map(([, body]) => body)).toEqual([
      { stagePlaybookIds: ['qa', 'review'] },
      { stagePlaybookIds: ['review'] },
    ])
    expect(stages().map((stage) => stage.text())).toEqual(['1. Code review'])
  })

  it('keeps the chain as it was when the server refuses it', async () => {
    stubFetch(
      [playbook('build', 'Build', ['review', 'qa']), playbook('review', 'Code review'), playbook('qa', 'QA')],
      'admin',
      true,
    )
    const wrapper = await mountPlaybooks()

    await wrapper.findAll('[data-testid="playbook-stage-remove"]')[0]!.trigger('click')
    await flushPromises()

    expect(wrapper.findAll('[data-testid="playbook-stage"]')).toHaveLength(2)
  })

  it('shows a Member the chain but none of the controls', async () => {
    stubFetch([playbook('build', 'Build', ['review']), playbook('review', 'Code review')], 'member')
    const wrapper = await mountPlaybooks()

    expect(wrapper.get('[data-testid="playbook-stage-list"]').text()).toContain('1. Code review')
    expect(wrapper.find('[data-testid="playbook-add-stage"]').exists()).toBe(false)
    expect(wrapper.find('[data-testid="playbook-stage-remove"]').exists()).toBe(false)
  })
})
