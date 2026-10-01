import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query'
import { mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { nextTick } from 'vue'
import { createMemoryHistory, createRouter } from 'vue-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import type { Project } from '@/api/projects'
import AppHeader from '@/components/shell/AppHeader.vue'
import { useOrganizationsStore } from '@/stores/organizations'
import { useProjectsStore } from '@/stores/projects'

const project = (key: string, name: string): Project => ({
  id: key,
  key,
  name,
  description: null,
  visibility: 'organization',
  icon: null,
  color: null,
  isArchived: false,
  createdAt: '2026-10-01T00:00:00Z',
  role: 'admin',
  version: 1,
})

const pages = [
  ['teams/team-1/backlog', 'Backlog'],
  ['board', 'Board'],
  ['items', 'Items'],
  ['items/IDA-4', 'Item'],
  ['wiki', 'Wiki'],
  ['teams/team-1/sprints', 'Sprints'],
  ['teams/team-1/sprints/sprint-1', 'Sprint'],
  ['dashboard', 'Dashboard'],
  ['analytics', 'Cycle insights'],
  ['portfolio', 'Portfolio'],
  ['settings/general', 'General'],
] as const

let wrapper: ReturnType<typeof mount>
let client: QueryClient

beforeEach(() => {
  const entries = new Map<string, string>()
  vi.stubGlobal('localStorage', {
    getItem: (key: string) => entries.get(key) ?? null,
    setItem: (key: string, value: string) => void entries.set(key, value),
    removeItem: (key: string) => void entries.delete(key),
  })
  setActivePinia(createPinia())
  // Seed the loaded scope without starting unrelated API calls.
  useOrganizationsStore().currentSlug = 'acme'
  const projects = useProjectsStore()
  projects.projects = [
    project('AICTIQ', 'Aictiq'),
    project('IDA', 'IDA'),
    project('HLQ', 'HairLinQ'),
  ]
  projects.select('AICTIQ')
})

afterEach(() => {
  wrapper?.unmount()
  client?.clear()
  vi.unstubAllGlobals()
})

async function render(path: string) {
  const component = { template: '<div />' }
  const router = createRouter({
    history: createMemoryHistory(),
    routes: [
      ...pages.map(([suffix, title]) => ({
        path: `/o/:slug/p/:projectKey/${suffix}`,
        component,
        meta: { requiresAuth: true, title },
      })),
      { path: '/board', name: 'board', component, meta: { requiresAuth: true, title: 'Board' } },
      { path: '/', name: 'home', component },
      { path: '/projects', component, meta: { requiresAuth: true, title: 'Projects' } },
      {
        path: '/o/:slug/settings/general',
        component,
        meta: { requiresAuth: true, title: 'General' },
      },
    ],
  })
  await router.push(path)
  client = new QueryClient()
  wrapper = mount(AppHeader, {
    global: {
      plugins: [router, [VueQueryPlugin, { queryClient: client }]],
      stubs: { NotificationsPopover: true },
    },
  })
  return router
}

const crumbs = () => wrapper.get('nav[aria-label="Breadcrumb"]').text()

describe('AppHeader project breadcrumbs', () => {
  it.each(pages)('uses the URL project on %s', async (suffix, title) => {
    await render(`/o/acme/p/IDA/${suffix}`)
    expect(crumbs()).toBe(`IDA/${title}`)
  })

  it('updates on navigation and browser back without remounting the header', async () => {
    const router = await render('/o/acme/p/IDA/items')
    await router.push('/o/acme/p/HLQ/items')
    expect(crumbs()).toBe('HairLinQ/Items')
    await router.push('/o/acme/p/AICTIQ/items')
    expect(crumbs()).toBe('Aictiq/Items')
    const navigated = new Promise<void>((resolve) => {
      const remove = router.afterEach(() => {
        remove()
        resolve()
      })
    })
    router.back()
    await navigated
    await nextTick()
    expect(crumbs()).toBe('HairLinQ/Items')
  })

  it('follows the selected project on the unscoped board', async () => {
    await render('/board')
    const projects = useProjectsStore()
    for (const [key, name] of [
      ['IDA', 'IDA'],
      ['HLQ', 'HairLinQ'],
      ['AICTIQ', 'Aictiq'],
    ] as const) {
      projects.select(key)
      await nextTick()
      expect(crumbs()).toBe(`${name}/Board`)
    }
  })

  it('uses the route key while project data is loading and then shows the name', async () => {
    const projects = useProjectsStore()
    projects.projects = []
    await render('/o/acme/p/HLQ/wiki')
    expect(crumbs()).toBe('HLQ/Wiki')
    projects.projects = [project('HLQ', 'HairLinQ')]
    await nextTick()
    expect(crumbs()).toBe('HairLinQ/Wiki')
    projects.replace(project('HLQ', 'HairLinQ renamed'))
    await nextTick()
    expect(crumbs()).toBe('HairLinQ renamed/Wiki')
  })

  it('does not substitute the selected project for an unknown route project', async () => {
    await render('/o/acme/p/UNKNOWN/items')
    expect(crumbs()).toBe('UNKNOWN/Items')
  })

  it('does not resolve a route project name from another organization', async () => {
    await render('/o/other/p/HLQ/items')
    expect(crumbs()).toBe('HLQ/Items')
  })

  it.each([
    ['/', 'My work'],
    ['/projects', 'Projects'],
    ['/o/acme/settings/general', 'General'],
  ])('keeps the app breadcrumb outside project pages: %s', async (path, title) => {
    useProjectsStore().select('IDA')
    await render(path)
    expect(crumbs()).toBe(`Aictiq/${title}`)
  })
})
