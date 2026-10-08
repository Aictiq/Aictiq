import { flushPromises, mount, RouterLinkStub, type VueWrapper } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query'
import { createMemoryHistory, createRouter } from 'vue-router'
import { afterEach, describe, expect, it, vi } from 'vitest'

import ItemDetail from '@/components/items/ItemDetail.vue'
import type { WorkItem } from '@/api/items'
import { useOrganizationsStore } from '@/stores/organizations'
import { useSessionStore } from '@/stores/session'

vi.mock('@/composables/useProjectRealtime', () => ({ useProjectRealtime: vi.fn() }))

const parent: WorkItem = {
  id: 'i1',
  key: 'PROJ-1',
  type: 'story',
  title: 'Crash on save',
  stateId: 's1',
  descriptionMarkdown: '',
  descriptionHtml: '',
  stateCategory: 'active',
  priority: 'none',
  assigneeId: null,
  teamId: 'team-1',
  sprintId: null,
  parentId: null,
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

const response = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), {
    status,
    headers: { 'content-type': 'application/json' },
  })
const wrappers: VueWrapper[] = []
const clients: QueryClient[] = []

async function mountDetail(
  options: {
    type?: WorkItem['type']
    role?: string
    archived?: boolean
    teamId?: string | null
    create?: (body: Record<string, unknown>) => Response | Promise<Response>
  } = {},
) {
  const item = {
    ...parent,
    type: options.type ?? parent.type,
    teamId: options.teamId === undefined ? parent.teamId : options.teamId,
  }
  const children: WorkItem[] = []
  const writes: Record<string, unknown>[] = []
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: unknown, init?: RequestInit) => {
      const url = String(input).split('?')[0] ?? ''
      if (init?.method === 'POST' && url.endsWith('/projects/PROJ/items/')) {
        const body = JSON.parse(String(init.body)) as Record<string, unknown>
        writes.push(body)
        if (options.create) return options.create(body)
        const child: WorkItem = {
          ...parent,
          id: `child-${writes.length}`,
          key: `PROJ-${writes.length + 2}`,
          type: 'task',
          title: String(body.title),
          parentId: item.id,
          teamId: item.teamId,
        }
        children.push(child)
        return response(child)
      }
      if (/\/items\/PROJ-\d+$/.test(url)) {
        const key = url.split('/').at(-1)!
        return response({ ...item, key, id: key === 'PROJ-1' ? 'i1' : 'i2' })
      }
      if (url.endsWith('/children')) return response(children)
      if (url.endsWith('/runs')) return response({ items: [], totalCount: 0 })
      if (url.endsWith('/comments') || url.endsWith('/history')) return response({ items: [] })
      if (url.endsWith('/projects/PROJ'))
        return response({
          id: 'pr1',
          key: 'PROJ',
          name: 'Prototype',
          role: options.role ?? 'member',
          isArchived: options.archived ?? false,
        })
      if (url.endsWith('/board')) return response({ columns: [] })
      return response([])
    }),
  )
  const pinia = createPinia()
  setActivePinia(pinia)
  const router = createRouter({
    history: createMemoryHistory(),
    routes: [{ path: '/o/:slug/p/:projectKey/items/:itemKey', component: { template: '<div />' } }],
  })
  await router.push('/o/acme/p/PROJ/items/PROJ-1')
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
  const client = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: Infinity } } })
  clients.push(client)
  const invalidate = vi.spyOn(client, 'invalidateQueries')
  const wrapper = mount(ItemDetail, {
    props: { slug: 'acme', projectKey: 'PROJ', itemKey: 'PROJ-1' },
    attachTo: document.body,
    global: {
      plugins: [pinia, router, [VueQueryPlugin, { queryClient: client }]],
      stubs: {
        RouterLink: RouterLinkStub,
        Teleport: true,
        MarkdownEditor: true,
        RefinementPanel: true,
        StartRunDialog: true,
        TimeTrackingPopover: true,
      },
    },
  })
  wrappers.push(wrapper)
  await flushPromises()
  return { wrapper, writes, invalidate, client }
}

afterEach(() => {
  wrappers.splice(0).forEach((wrapper) => wrapper.unmount())
  clients.splice(0).forEach((client) => client.clear())
  document.body.innerHTML = ''
  vi.unstubAllGlobals()
})

describe('ItemDetail quick subtasks', () => {
  it.each([
    ['story', 'member', false, true],
    ['bug', 'member', false, true],
    ['feature', 'member', false, false],
    ['task', 'member', false, false],
    ['epic', 'member', false, false],
    ['story', 'guest', false, false],
    ['story', 'member', true, false],
  ] as const)(
    'offers creation for %s with role %s and archived=%s: %s',
    async (type, role, archived, allowed) => {
      const { wrapper } = await mountDetail({ type, role, archived })
      expect(wrapper.find('[aria-label="Add subtask to PROJ-1"]').exists()).toBe(allowed)
    },
  )

  it.each(['team-1', null])(
    'creates consecutive title-only tasks with the parent and team %s',
    async (teamId) => {
      const { wrapper, writes, invalidate } = await mountDetail({ teamId })
      const section = wrapper.find('[data-testid="item-subtasks"]')
      await section.get('[aria-label="Add subtask to PROJ-1"]').trigger('click')
      const input = section.get<HTMLInputElement>('#subtask-title')
      await input.setValue('  First task  ')
      await wrapper.get('[data-testid="item-subtasks"] form').trigger('submit')
      await flushPromises()
      expect(writes).toEqual([{ type: 'task', title: 'First task', parentId: 'i1', teamId }])
      expect(section.text()).toContain('First task')
      expect(input.element.value).toBe('')
      expect(document.activeElement).toBe(input.element)
      expect(invalidate).toHaveBeenCalledWith(
        expect.objectContaining({ queryKey: ['acme', 'PROJ', 'items'] }),
      )
      if (teamId)
        expect(invalidate).toHaveBeenCalledWith(
          expect.objectContaining({ queryKey: ['board', 'acme', 'PROJ', teamId] }),
        )
      await input.setValue('Second task')
      await wrapper.get('[data-testid="item-subtasks"] form').trigger('submit')
      await flushPromises()
      expect(writes).toHaveLength(2)
      expect(section.text()).toContain('Second task')
    },
  )

  it('ignores blank titles and repeated submission while the request is pending', async () => {
    let finish!: (value: Response) => void
    const pending = new Promise<Response>((resolve) => {
      finish = resolve
    })
    const { wrapper, writes } = await mountDetail({ create: () => pending })
    await wrapper.get('[aria-label="Add subtask to PROJ-1"]').trigger('click')
    const input = wrapper.get('#subtask-title')
    const form = wrapper.get('[data-testid="item-subtasks"] form')
    await input.setValue('   ')
    await form.trigger('submit')
    expect(writes).toHaveLength(0)
    await input.setValue('Pending task')
    await form.trigger('submit')
    await form.trigger('submit')
    expect(writes).toHaveLength(1)
    finish(
      response({
        ...parent,
        id: 'c1',
        key: 'PROJ-3',
        type: 'task',
        title: 'Pending task',
        parentId: parent.id,
      }),
    )
    await flushPromises()
  })

  it('keeps the title after failure so creation can be retried', async () => {
    const { wrapper, writes } = await mountDetail({
      create: () => response({ title: 'Unavailable', status: 503 }, 503),
    })
    await wrapper.get('[aria-label="Add subtask to PROJ-1"]').trigger('click')
    const input = wrapper.get<HTMLInputElement>('#subtask-title')
    await input.setValue('Keep this title')
    await wrapper.get('[data-testid="item-subtasks"] form').trigger('submit')
    await flushPromises()
    expect(input.element.value).toBe('Keep this title')
    await wrapper.get('[data-testid="item-subtasks"] form').trigger('submit')
    await flushPromises()
    expect(writes).toHaveLength(2)
  })

  it('keeps a pending creation with its original parent after navigation', async () => {
    let finish!: (value: Response) => void
    const pending = new Promise<Response>((resolve) => {
      finish = resolve
    })
    const { wrapper, client } = await mountDetail({ create: () => pending })
    await wrapper.get('[aria-label="Add subtask to PROJ-1"]').trigger('click')
    await wrapper.get('#subtask-title').setValue('Old parent task')
    await wrapper.get('[data-testid="item-subtasks"] form').trigger('submit')
    await wrapper.setProps({ itemKey: 'PROJ-2' })
    await flushPromises()
    await wrapper.get('[aria-label="Add subtask to PROJ-2"]').trigger('click')
    expect(wrapper.get('#subtask-title').attributes('disabled')).toBeDefined()
    const child: WorkItem = {
      ...parent,
      id: 'c1',
      key: 'PROJ-3',
      type: 'task',
      title: 'Old parent task',
      parentId: parent.id,
    }
    finish(response(child))
    await flushPromises()
    expect(wrapper.get<HTMLInputElement>('#subtask-title').element.value).toBe('')
    expect(wrapper.get('#subtask-title').attributes('disabled')).toBeUndefined()
    expect(wrapper.get('[data-testid="item-subtasks"]').text()).not.toContain('Old parent task')
    expect(client.getQueryData<WorkItem[]>(['acme', 'PROJ-1', 'children'])).toContainEqual(child)
    expect(client.getQueryData<WorkItem[]>(['acme', 'PROJ-2', 'children'])).toEqual([])
  })

  it('dismisses the composer with Escape or Cancel and resets it when the item changes', async () => {
    const { wrapper, writes } = await mountDetail()
    const open = () => wrapper.get('[aria-label="Add subtask to PROJ-1"]').trigger('click')
    await open()
    await wrapper.get('#subtask-title').setValue('Discard with Escape')
    await wrapper.get('#subtask-title').trigger('keydown', { key: 'Escape' })
    expect(wrapper.find('#subtask-title').exists()).toBe(false)
    await open()
    await wrapper.get('#subtask-title').setValue('Discard with Cancel')
    await wrapper.get('[aria-label="Cancel subtask"]').trigger('click')
    expect(wrapper.find('#subtask-title').exists()).toBe(false)
    await open()
    await wrapper.get('#subtask-title').setValue('Old parent draft')
    await wrapper.setProps({ itemKey: 'PROJ-2' })
    await flushPromises()
    expect(wrapper.find('#subtask-title').exists()).toBe(false)
    await wrapper.get('[aria-label="Add subtask to PROJ-2"]').trigger('click')
    expect(wrapper.get<HTMLInputElement>('#subtask-title').element.value).toBe('')
    expect(writes).toHaveLength(0)
  })
})
