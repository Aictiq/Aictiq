import { flushPromises, mount, RouterLinkStub, type VueWrapper } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query'
import { createMemoryHistory, createRouter } from 'vue-router'
import { afterEach, describe, expect, it, vi } from 'vitest'

import ItemDetail from '@/components/items/ItemDetail.vue'
import type { WorkItem } from '@/api/items'
import { useOrganizationsStore } from '@/stores/organizations'
import { useSessionStore } from '@/stores/session'
import { useTeamsStore } from '@/stores/teams'
import { useProjectsStore } from '@/stores/projects'
import type { Team } from '@/api/teams'

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
  sprintId: 'sprint-1',
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
    crossProject?: boolean
  } = {},
) {
  let item = { ...story }
  const teams = ['team-1', 'team-2'].map((id, index) => ({
    id,
    name: index ? 'Platform' : 'Product',
    isDefault: !index,
  })) as Team[]
  const writes: Record<string, unknown>[] = []
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: unknown, init?: RequestInit) => {
      const url = String(input).split('?')[0] ?? ''
      if (init?.method === 'PATCH' && url.endsWith('/items/PROJ-1')) {
        const body = JSON.parse(String(init.body)) as Record<string, unknown>
        writes.push(body)
        if (options.patch) return options.patch(body)
        item = {
          ...item,
          ...(body as Partial<WorkItem>),
          sprintId: null,
          version: item.version + 1,
        }
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
      if (url.endsWith('/teams')) return response(teams)
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
  useProjectsStore().select(options.crossProject ? 'OTHER' : 'PROJ')
  const teamsStore = useTeamsStore()
  await flushPromises()
  teamsStore.teams = options.crossProject
    ? [{ ...teams[0]!, id: 'wrong-team', name: 'Other project' }]
    : teams
  teamsStore.status = 'ready'
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
  return { wrapper, writes, invalidate, client }
}

afterEach(() => {
  wrappers.splice(0).forEach((wrapper) => wrapper.unmount())
  clients.splice(0).forEach((client) => client.clear())
  document.body.innerHTML = ''
  vi.unstubAllGlobals()
})

describe('ItemDetail team selector', () => {
  it('saves the selected team, refreshes both planning views, and reflects the cleared sprint', async () => {
    const { wrapper, writes, invalidate, client } = await mountDetail()
    const select = wrapper.get<HTMLSelectElement>('#item-team')
    expect(select.element.value).toBe('team-1')
    expect(wrapper.get('#item-team-sprint-note').text()).toContain(
      'removes this item from its sprint',
    )
    await select.setValue('team-2')
    await flushPromises()
    expect(writes).toEqual([
      expect.objectContaining({
        teamId: 'team-2',
        title: story.title,
        type: 'story',
        parentId: null,
        version: 1,
      }),
    ])
    expect(select.element.value).toBe('team-2')
    expect(client.getQueryData<WorkItem>(['acme', 'PROJ-1'])?.sprintId).toBeNull()
    expect(wrapper.find('#item-team-sprint-note').exists()).toBe(false)
    for (const team of ['team-1', 'team-2']) {
      expect(invalidate).toHaveBeenCalledWith({ queryKey: ['board', 'acme', 'PROJ', team] })
      expect(invalidate).toHaveBeenCalledWith({ queryKey: ['acme', team, 'items', 'backlog'] })
    }
    expect(invalidate).toHaveBeenCalledWith({ queryKey: ['acme', 'sprint-1', 'capacity'] })
  })

  it('can clear the team with No team', async () => {
    const { wrapper, writes } = await mountDetail()
    await wrapper.get('#item-team').setValue('')
    await flushPromises()
    expect(writes[0]).toMatchObject({ teamId: null, version: 1 })
    expect(wrapper.get<HTMLSelectElement>('#item-team').element.value).toBe('')
  })

  it.each([
    ['guest', false],
    ['member', true],
  ] as const)('shows the team read-only for %s archived=%s', async (role, archived) => {
    const { wrapper } = await mountDetail({ role, archived })
    expect(wrapper.get<HTMLSelectElement>('#item-team').element.disabled).toBe(true)
    expect(wrapper.get<HTMLSelectElement>('#item-team').element.value).toBe('team-1')
  })

  it('uses the item project for a dialog outside the selected project', async () => {
    const { wrapper } = await mountDetail({ crossProject: true })
    const select = wrapper.get('#item-team')
    expect(select.text()).toContain('Platform')
    expect(select.text()).not.toContain('Other project')
  })

  it('restores the server team and reports a refused edit', async () => {
    const { wrapper, writes } = await mountDetail({
      patch: () => response({ title: 'Team not found', status: 400 }, 400),
    })
    await wrapper.get('#item-team').setValue('team-2')
    await flushPromises()
    expect(writes).toHaveLength(1)
    expect(wrapper.get<HTMLSelectElement>('#item-team').element.value).toBe('team-1')
    expect(wrapper.get('[role="alert"]').text()).toBe('Team not found')
  })

  it('refetches on a stale version without retrying the write', async () => {
    const { wrapper, writes } = await mountDetail({
      patch: () => response({ title: 'Conflict', status: 409 }, 409),
    })
    await wrapper.get('#item-team').setValue('team-2')
    await flushPromises()
    expect(writes).toHaveLength(1)
    expect(wrapper.get<HTMLSelectElement>('#item-team').element.value).toBe('team-1')
    expect(wrapper.get('[role="alert"]').text()).toBe('Conflict')
  })
})
