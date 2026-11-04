import { flushPromises, mount, RouterLinkStub } from '@vue/test-utils'
import { createPinia } from 'pinia'
import { ref } from 'vue'
import { afterEach, describe, expect, it, vi } from 'vitest'

import { boardTargets } from '@/api/boards'
import type { Organization } from '@/api/organizations'
import type { Playbook } from '@/api/playbooks'
import { orgScopeKey, type OrgScope } from '@/composables/useSettingsScope'
import FactoryPlaybooksView from '@/views/factory/FactoryPlaybooksView.vue'

/**
 * A playbook's On success / On failure can name one board column of several that share a
 * state, so a failed run lands in "Failed" rather than wherever the board puts state New.
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

const states = [
  { id: 'new', name: 'New', category: 'proposed', position: 0 },
  { id: 'review', name: 'In Review', category: 'resolved', position: 1 },
  { id: 'closed', name: 'Closed', category: 'completed', position: 2 },
]

const column = (id: string, name: string, stateIds: string[]) => ({
  id,
  name,
  stateIds,
  generalState: 'new',
  wipLimit: null,
  count: 0,
  wipExceeded: false,
  cards: [],
})

const columns = [
  column('col-new', 'New', ['new']),
  column('col-refined', 'Refined', ['new']),
  column('col-failed', 'Failed', ['new']),
  column('col-review', 'In Review', ['review']),
]

const playbook = (overrides: Partial<Playbook> = {}): Playbook => ({
  id: 'build',
  projectId: 'pr1',
  name: 'Build',
  wikiPageId: 'page-build',
  harness: 'claude',
  onSuccessStateId: 'review',
  onFailureStateId: 'new',
  onSuccessColumnId: null,
  onFailureColumnId: 'col-failed',
  workOnDefaultBranch: false,
  maxMinutes: 60,
  isDefault: true,
  stagePlaybookIds: [],
  createdBy: 'u1',
  createdAt: '2026-10-01T00:00:00Z',
  updatedAt: '2026-10-01T00:00:00Z',
  version: 1,
  ...overrides,
})

const respond = (body: unknown) =>
  new Response(JSON.stringify(body), {
    status: 200,
    headers: { 'content-type': 'application/json' },
  })

function stubFetch(playbooks: Playbook[], teams = [{ id: 't1', name: 'Core' }]) {
  const fetchMock = vi.fn(async (input: unknown, init?: RequestInit) => {
    const url = String(input).split('?')[0] ?? ''
    if (init?.method === 'PATCH') {
      return respond({ ...playbooks[0]!, ...JSON.parse(String(init.body)), version: 2 })
    }
    if (url.endsWith('/projects')) return respond([project])
    if (url.endsWith('/playbooks')) return respond(playbooks)
    if (url.endsWith('/instructions')) {
      return respond({
        wikiPageId: 'page-build',
        pageTitle: 'Build',
        markdown: 'Do it',
        inFactorySection: true,
      })
    }
    if (url.endsWith('/workflows')) return respond([{ id: 'wf', isDefault: true, states }])
    if (url.endsWith('/teams')) return respond(teams)
    if (url.endsWith('/board')) {
      const teamId = url.split('/').at(-2)
      return respond({
        columns: teamId === 't1' ? columns : [column('col-other', 'Failed', ['new'])],
      })
    }
    return respond([])
  })
  vi.stubGlobal('fetch', fetchMock)
  return fetchMock
}

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

async function openEditor(wrapper: Awaited<ReturnType<typeof mountPlaybooks>>) {
  await wrapper
    .findAll('button')
    .find((button) => button.text() === 'Edit')!
    .trigger('click')
  await flushPromises()
}

const optionLabels = (wrapper: Awaited<ReturnType<typeof mountPlaybooks>>, id: string) =>
  wrapper
    .get(`#${id}`)
    .findAll('option')
    .map((option) => option.text())

afterEach(() => vi.unstubAllGlobals())

describe('playbook outcome columns', () => {
  it('lists each board column separately, and states no board shows by name', async () => {
    stubFetch([playbook()])
    const wrapper = await mountPlaybooks()
    await openEditor(wrapper)

    expect(optionLabels(wrapper, 'playbook-failure')).toEqual([
      'Do not move the item',
      'New',
      'Refined',
      'Failed',
      'In Review',
      'Closed',
    ])
    expect((wrapper.get('#playbook-failure').element as HTMLSelectElement).value).toBe(
      'new:col-failed',
    )
  })

  it('names the column in the playbook summary', async () => {
    stubFetch([playbook()])
    const wrapper = await mountPlaybooks()

    expect(wrapper.text()).toContain('Success: In Review · Failure: Failed ·')
  })

  it('saves the chosen column with its state', async () => {
    const fetchMock = stubFetch([playbook({ onFailureColumnId: null })])
    const wrapper = await mountPlaybooks()
    await openEditor(wrapper)

    await wrapper.get('#playbook-failure').setValue('new:col-failed')
    await wrapper.get('#playbook-form').trigger('submit')
    await flushPromises()

    const patch = fetchMock.mock.calls.find(([, init]) => init?.method === 'PATCH')!
    expect(JSON.parse(String(patch[1]!.body))).toMatchObject({
      onSuccessStateId: 'review',
      onSuccessColumnId: 'col-review',
      onFailureStateId: 'new',
      onFailureColumnId: 'col-failed',
    })
  })

  it('keeps a playbook saved with a state alone as it was', async () => {
    const fetchMock = stubFetch([playbook({ onSuccessStateId: null, onFailureColumnId: null })])
    const wrapper = await mountPlaybooks()
    await openEditor(wrapper)

    expect(optionLabels(wrapper, 'playbook-failure')).toContain('New / Refined / Failed')
    expect((wrapper.get('#playbook-failure').element as HTMLSelectElement).value).toBe('new')

    await wrapper.get('#playbook-form').trigger('submit')
    await flushPromises()
    const patch = fetchMock.mock.calls.find(([, init]) => init?.method === 'PATCH')!
    expect(JSON.parse(String(patch[1]!.body))).toMatchObject({
      onFailureStateId: 'new',
      onFailureColumnId: null,
    })
  })
})

describe('boardTargets', () => {
  it('names the team when the project has several boards', async () => {
    stubFetch(
      [],
      [
        { id: 't1', name: 'Core' },
        { id: 't2', name: 'Mobile' },
      ],
    )
    const targets = await boardTargets('acme', 'PROJ', states)

    expect(targets.filter((target) => target.name === 'Failed')).toEqual([
      { stateId: 'new', columnId: 'col-failed', name: 'Failed', team: 'Core' },
      { stateId: 'new', columnId: 'col-other', name: 'Failed', team: 'Mobile' },
    ])
  })
})
