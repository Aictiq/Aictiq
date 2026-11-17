import { DOMWrapper, flushPromises, mount, RouterLinkStub, type VueWrapper } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query'
import { createMemoryHistory, createRouter } from 'vue-router'
import { afterEach, describe, expect, it, vi } from 'vitest'

import ItemDetail from '@/components/items/ItemDetail.vue'
import type { WorkItem, WorkItemType } from '@/api/items'
import { useOrganizationsStore } from '@/stores/organizations'
import { useSessionStore } from '@/stores/session'

vi.mock('@/composables/useProjectRealtime', () => ({ useProjectRealtime: vi.fn() }))

function workItem(
  id: string,
  number: number,
  type: WorkItemType,
  title: string,
  parentId: string | null = null,
): WorkItem {
  return {
    id,
    key: `PROJ-${number}`,
    type,
    title,
    descriptionMarkdown: '',
    stateId: 's1',
    descriptionHtml: '',
    stateCategory: 'active',
    priority: 'none',
    assigneeId: null,
    teamId: 'team-1',
    sprintId: null,
    parentId,
    points: null,
    estimateHours: null,
    remainingHours: null,
    completedHours: null,
    dueDate: null,
    version: 1,
    labels: [],
    updatedAt: '',
    isWatching: false,
    watcherCount: 0,
    claimedBy: null,
    claimedAt: null,
    claimHeartbeatAt: null,
    rollup: {
      totalCount: 0,
      completedCount: 0,
      pointsTotal: 0,
      pointsCompleted: 0,
      remainingHours: 0,
    },
  }
}
const epic = workItem('e1', 2, 'epic', 'Checkout revamp')
// Planning items need not be on the open team; the picker searches the whole project.
const feature = { ...workItem('f1', 3, 'feature', 'Saved carts', 'e1'), teamId: null }
const story = workItem('s1', 4, 'story', 'Other story')
const bug = workItem('b1', 5, 'bug', 'Other bug')

const response = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/json' } })
const wrappers: VueWrapper[] = []
const clients: QueryClient[] = []

async function mountDetail(
  subject: WorkItem,
  options: { role?: string; reparent?: (body: Record<string, unknown>) => Response } = {},
) {
  let item = { ...subject }
  const project = [epic, feature, story, bug, item]
  const writes: Record<string, unknown>[] = []
  const searches: string[] = []
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: unknown, init?: RequestInit) => {
      const url = new URL(String(input), 'http://test')
      const path = url.pathname
      if (init?.method === 'POST' && path.endsWith(`/items/${item.key}/reparent`)) {
        const body = JSON.parse(String(init.body)) as Record<string, unknown>
        writes.push(body)
        if (options.reparent) return options.reparent(body)
        const parent = project.find((entry) => entry.key === body.parentKey)
        item = { ...item, parentId: parent?.id ?? null, version: item.version + 1 }
        return response(item)
      }
      if (path.endsWith(`/items/${item.key}/ancestors`)) {
        const parent = project.find((entry) => entry.id === item.parentId)
        return response(parent ? [parent] : [])
      }
      if (path.endsWith(`/items/${item.key}`)) return response(item)
      if (path.endsWith('/projects/PROJ/items/')) {
        const type = url.searchParams.get('filter')?.replace('type:', '')
        searches.push(type ?? '')
        const items = project.filter((entry) => entry.type === type)
        return response({ items, page: 1, pageSize: 50, totalCount: items.length })
      }
      if (path.endsWith('/runs')) return response({ items: [], totalCount: 0 })
      if (path.endsWith('/comments') || path.endsWith('/history')) return response({ items: [] })
      if (path.endsWith('/projects/PROJ'))
        return response({
          id: 'pr1',
          key: 'PROJ',
          name: 'Prototype',
          role: options.role ?? 'member',
          isArchived: false,
        })
      if (path.endsWith('/board')) return response({ columns: [] })
      return response([])
    }),
  )
  const pinia = createPinia()
  setActivePinia(pinia)
  const router = createRouter({
    history: createMemoryHistory(),
    routes: [{ path: '/o/:slug/p/:projectKey/items/:itemKey', component: { template: '<div />' } }],
  })
  await router.push(`/o/acme/p/PROJ/items/${item.key}`)
  await router.isReady()
  const organizations = useOrganizationsStore()
  organizations.organizations = [
    { id: 'org-1', slug: 'acme', name: 'Acme', role: 'member', canOperateFactory: false },
  ]
  organizations.select('acme')
  useSessionStore().set({
    id: 'u1',
    email: 'u1@acme.dev',
    firstName: 'U',
    lastName: 'One',
    fullName: 'U One',
    roles: [],
    isAgent: false,
    avatarKey: null,
    timeZone: null,
  })
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false, gcTime: Infinity } },
  })
  clients.push(client)
  const wrapper = mount(ItemDetail, {
    props: { slug: 'acme', projectKey: 'PROJ', itemKey: item.key },
    attachTo: document.body,
    global: {
      plugins: [pinia, router, [VueQueryPlugin, { queryClient: client }]],
      stubs: {
        RouterLink: RouterLinkStub,
        MarkdownEditor: true,
        RefinementPanel: true,
        StartRunDialog: true,
        TimeTrackingPopover: true,
      },
    },
  })
  wrappers.push(wrapper)
  await flushPromises()
  return { wrapper, writes, searches }
}

async function openPicker(wrapper: VueWrapper) {
  await wrapper.get('[data-testid="item-parent-change"]').trigger('click')
  // The search is debounced; the first page needs no search.
  await flushPromises()
  // The dialog is teleported to the body, outside the wrapper.
  return new DOMWrapper(document.body).get('[data-testid="parent-picker"]')
}
const findPicker = () => new DOMWrapper(document.body).find('[data-testid="parent-picker"]')
const optionTexts = (picker: Pick<DOMWrapper<Element>, 'findAll'>) =>
  picker.findAll('[role="option"]').map((option) => option.text())

afterEach(() => {
  wrappers.splice(0).forEach((wrapper) => wrapper.unmount())
  clients.splice(0).forEach((client) => client.clear())
  document.body.innerHTML = ''
  vi.unstubAllGlobals()
})

describe('ItemDetail parent picker', () => {
  it('sets the parent of an orphan story from Epics and Features only, including unteamed ones', async () => {
    const { wrapper, writes, searches } = await mountDetail(workItem('i1', 1, 'story', 'Orphan'))
    expect(wrapper.get('[data-testid="item-parent"]').text()).toBe('No parent')
    const picker = await openPicker(wrapper)
    expect(searches.sort()).toEqual(['epic', 'feature'])
    expect(optionTexts(picker)).toEqual([
      expect.stringContaining('No parent'),
      expect.stringContaining('PROJ-2'),
      expect.stringContaining('PROJ-3'),
    ])
    const saved = picker
      .findAll('[role="option"]')
      .find((option) => option.text().includes('PROJ-3'))!
    await saved.trigger('click')
    await flushPromises()
    expect(writes).toEqual([{ parentKey: 'PROJ-3', version: 1 }])
    expect(findPicker().exists()).toBe(false)
    expect(wrapper.get('[data-testid="item-parent"]').text()).toContain('PROJ-3')
    expect(wrapper.get('[data-testid="item-parent"]').text()).toContain('Saved carts')
  })

  it('clears an existing parent with No parent', async () => {
    const { wrapper, writes } = await mountDetail(workItem('i1', 1, 'bug', 'Child bug', 'f1'))
    expect(wrapper.get('[data-testid="item-parent"]').text()).toContain('Saved carts')
    expect(wrapper.get('[data-testid="item-parent-change"]').text()).toBe('Change')
    const picker = await openPicker(wrapper)
    expect(
      picker
        .findAll('[role="option"]')
        .find((option) => option.text().includes('PROJ-3'))!
        .attributes('disabled'),
    ).toBeDefined()
    await picker.get('[data-testid="parent-picker-none"]').trigger('click')
    await flushPromises()
    expect(writes).toEqual([{ parentKey: null, version: 1 }])
    expect(wrapper.get('[data-testid="item-parent"]').text()).toBe('No parent')
  })

  it('offers a Task only Stories and Bugs and never No parent', async () => {
    const { wrapper, searches } = await mountDetail(workItem('i1', 1, 'task', 'Subtask', 's1'))
    const picker = await openPicker(wrapper)
    expect(searches.sort()).toEqual(['bug', 'story'])
    expect(picker.find('[data-testid="parent-picker-none"]').exists()).toBe(false)
    expect(picker.text()).toContain('Allowed parent types: Story, Bug')
  })

  it('keeps the picker open and shows why the server refused', async () => {
    const { wrapper, writes } = await mountDetail(workItem('i1', 1, 'story', 'Orphan'), {
      reparent: () =>
        response(
          {
            title: 'One or more validation errors occurred.',
            status: 400,
            errors: { parentKey: ['That parent type is not allowed for this item.'] },
          },
          400,
        ),
    })
    const picker = await openPicker(wrapper)
    await picker
      .findAll('[role="option"]')
      .find((option) => option.text().includes('PROJ-2'))!
      .trigger('click')
    await flushPromises()
    expect(writes).toHaveLength(1)
    expect(findPicker().get('[role="alert"]').text()).toBe(
      'That parent type is not allowed for this item.',
    )
    expect(wrapper.get('[data-testid="item-parent"]').text()).toBe('No parent')
  })

  it('shows the parent read-only to a guest', async () => {
    const { wrapper } = await mountDetail(workItem('i1', 1, 'story', 'Child', 'e1'), {
      role: 'guest',
    })
    expect(wrapper.get('[data-testid="item-parent"]').text()).toContain('Checkout revamp')
    expect(wrapper.find('[data-testid="item-parent-change"]').exists()).toBe(false)
  })
})
