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

const story: WorkItem = {
  id: 'i1',
  key: 'PROJ-1',
  type: 'story',
  title: 'Crash on save',
  descriptionMarkdown: 'Steps to reproduce',
  stateId: 's1',
  descriptionHtml: '',
  stateCategory: 'active',
  priority: 'none',
  assigneeId: null,
  teamId: 'team-1',
  sprintId: null,
  parentId: null,
  points: 3,
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
    role?: string
    archived?: boolean
    patch?: (body: Record<string, unknown>) => Response
  } = {},
) {
  let item = { ...story }
  const writes: Record<string, unknown>[] = []
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: unknown, init?: RequestInit) => {
      const url = String(input).split('?')[0] ?? ''
      if (init?.method === 'PATCH' && url.endsWith('/items/PROJ-1')) {
        const body = JSON.parse(String(init.body)) as Record<string, unknown>
        writes.push(body)
        if (options.patch) return options.patch(body)
        item = { ...item, ...(body as Partial<WorkItem>), version: item.version + 1 }
        return response(item)
      }
      if (url.endsWith('/items/PROJ-1')) return response(item)
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
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false, gcTime: Infinity } },
  })
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
  return { wrapper, writes, invalidate }
}

afterEach(() => {
  wrappers.splice(0).forEach((wrapper) => wrapper.unmount())
  clients.splice(0).forEach((client) => client.clear())
  document.body.innerHTML = ''
  vi.unstubAllGlobals()
})

describe('ItemDetail type selector', () => {
  it.each([
    ['member', false, true],
    ['admin', false, true],
    ['guest', false, false],
    ['member', true, false],
  ] as const)(
    'with role %s and archived=%s offers the selector: %s',
    async (role, archived, shown) => {
      const { wrapper } = await mountDetail({ role, archived })
      expect(wrapper.find('#item-type').exists()).toBe(shown)
    },
  )

  it('saves the new type through the full edit and refreshes the lists', async () => {
    const { wrapper, writes, invalidate } = await mountDetail()
    const select = wrapper.get<HTMLSelectElement>('#item-type')
    expect(select.element.value).toBe('story')
    expect(wrapper.get('label[for="item-type"]').text()).toBe('Type')
    await select.setValue('bug')
    await flushPromises()
    expect(writes).toEqual([
      expect.objectContaining({
        type: 'bug',
        title: 'Crash on save',
        descriptionMarkdown: 'Steps to reproduce',
        teamId: 'team-1',
        parentId: null,
        points: 3,
        version: 1,
      }),
    ])
    expect(select.element.value).toBe('bug')
    expect(wrapper.text()).toContain('bug · active')
    expect(invalidate).toHaveBeenCalledWith(
      expect.objectContaining({ queryKey: ['acme', 'PROJ', 'items'] }),
    )
    expect(invalidate).toHaveBeenCalledWith(
      expect.objectContaining({ queryKey: ['board', 'acme', 'PROJ', 'team-1'] }),
    )
  })

  it.each([
    [
      'a missing parent',
      response(
        {
          title: 'One or more validation errors occurred.',
          status: 400,
          errors: { parentId: ['Features need an Epic parent and Tasks a Story or Bug parent.'] },
        },
        400,
      ),
      'Could not change to Task: Features need an Epic parent and Tasks a Story or Bug parent.',
    ],
    [
      'incompatible children',
      response(
        {
          title: 'Invalid parent.',
          status: 409,
          detail: 'Changing this type would invalidate one or more children.',
        },
        409,
      ),
      'Could not change to Task: Changing this type would invalidate one or more children.',
    ],
    [
      'a stale version',
      response(
        { title: 'Conflict', status: 409, detail: 'The item was modified by someone else.' },
        409,
      ),
      'Could not change to Task: The item was modified by someone else.',
    ],
  ])('keeps the original type and explains %s', async (_case, refusal, message) => {
    const { wrapper, writes } = await mountDetail({ patch: () => refusal.clone() })
    const select = wrapper.get<HTMLSelectElement>('#item-type')
    await select.setValue('task')
    await flushPromises()
    expect(writes).toHaveLength(1)
    expect(select.element.value).toBe('story')
    expect(wrapper.get('[role="alert"]').text()).toBe(message)
    expect(wrapper.text()).toContain('story · active')
  })

  it('explains an estimate the new type cannot carry', async () => {
    const { wrapper } = await mountDetail({
      patch: () =>
        response(
          {
            title: 'One or more validation errors occurred.',
            status: 400,
            errors: { points: ['Tasks use hours, not points.'] },
          },
          400,
        ),
    })
    await wrapper.get('#item-type').setValue('task')
    await flushPromises()
    expect(wrapper.get('[role="alert"]').text()).toContain('Tasks use hours, not points.')
    expect(wrapper.get<HTMLInputElement>('#item-points').element.value).toBe('3')
  })
})
