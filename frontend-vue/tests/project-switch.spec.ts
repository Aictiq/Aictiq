import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createMemoryHistory, createRouter } from 'vue-router'

import AppSidebar from '@/components/shell/AppSidebar.vue'
import * as teamsApi from '@/api/teams'
import appRouter from '@/router'
import { useOrganizationsStore } from '@/stores/organizations'
import { useProjectsStore } from '@/stores/projects'
import { useTeamsStore } from '@/stores/teams'
import { useUiStore } from '@/stores/ui'

beforeEach(() => {
  setActivePinia(createPinia())
  const entries = new Map([['aictiq.team.acme.NEW', 'new-remembered']])
  vi.stubGlobal('localStorage', {
    getItem: (key: string) => entries.get(key) ?? null,
    setItem: (key: string, value: string) => void entries.set(key, value),
    removeItem: (key: string) => void entries.delete(key),
  })
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input)
      const body = url.endsWith('/teams')
        ? url.includes('/NEW/')
          ? [
              { id: 'new-default', name: 'Default', isDefault: true },
              { id: 'new-remembered', name: 'Remembered', isDefault: false },
            ]
          : [{ id: 'old-team', name: 'Old team', isDefault: true }]
        : [
            { id: 'old', key: 'OLD', name: 'Old project', role: 'admin', isArchived: false },
            { id: 'new', key: 'NEW', name: 'New project', role: 'admin', isArchived: false },
            { id: 'member', key: 'MEM', name: 'Member project', role: 'member', isArchived: false },
          ]
      return new Response(JSON.stringify(body), { headers: { 'content-type': 'application/json' } })
    }),
  )
})

afterEach(() => {
  vi.unstubAllGlobals()
  vi.restoreAllMocks()
})

async function render(path: string, mobile = false) {
  const router = createRouter({ history: createMemoryHistory(), routes: appRouter.options.routes })
  useOrganizationsStore().select('acme')
  useProjectsStore().select('OLD')
  await router.push(path)
  const wrapper = mount(AppSidebar, {
    props: { mobile },
    global: { plugins: [router], stubs: { OrgSwitcher: true } },
  })
  await flushPromises()
  return { router, wrapper }
}

async function choose(
  wrapper: Awaited<ReturnType<typeof render>>['wrapper'],
  name = 'New project',
) {
  await wrapper
    .findAll('button')
    .find((button) => button.text().includes(name))!
    .trigger('click')
  await flushPromises()
}

describe('switching projects from the sidebar', () => {
  it.each([
    ['/o/acme/p/OLD/dashboard', '/o/acme/p/NEW/dashboard'],
    ['/o/acme/p/OLD/items?stateId=old-state', '/o/acme/p/NEW/items'],
    ['/o/acme/p/OLD/wiki/old-page/intro', '/o/acme/p/NEW/wiki'],
    ['/o/acme/p/OLD/settings/labels', '/o/acme/p/NEW/settings/labels'],
    ['/o/acme/p/OLD/settings/teams/old-team', '/o/acme/p/NEW/settings/teams'],
    ['/o/acme/p/OLD/teams/old-team/backlog', '/o/acme/p/NEW/teams/new-remembered/backlog'],
    ['/o/acme/p/OLD/teams/old-team/sprints', '/o/acme/p/NEW/teams/new-remembered/sprints'],
    [
      '/o/acme/p/OLD/teams/old-team/sprints/old-sprint',
      '/o/acme/p/NEW/teams/new-remembered/sprints',
    ],
    ['/o/acme/p/OLD/items/OLD-1', '/o/acme/p/NEW/items'],
    ['/o/acme/p/OLD/board', '/o/acme/p/NEW/board'],
    ['/board?item=OLD-1', '/board'],
    ['/', '/'],
    ['/inbox', '/inbox'],
    ['/o/acme/settings/agents', '/o/acme/settings/agents'],
    ['/o/acme/settings/members', '/o/acme/settings/members'],
    ['/o/acme/settings/general', '/o/acme/settings/general'],
    ['/o/acme/factory/runs?state=running', '/o/acme/factory/runs?state=running'],
    ['/projects', '/projects'],
    ['/settings/profile', '/settings/profile'],
  ])('keeps the view when switching from %s', async (from, to) => {
    const { wrapper, router } = await render(from)
    await choose(wrapper)
    await vi.waitFor(() => expect(router.currentRoute.value.fullPath).toBe(to))
    expect(useProjectsStore().currentKey).toBe('NEW')
    expect(useTeamsStore().currentId).toBe('new-remembered')
    wrapper.unmount()
  })

  it('uses the default team when the destination has no remembered team', async () => {
    localStorage.removeItem('aictiq.team.acme.NEW')
    const { wrapper, router } = await render('/o/acme/p/OLD/teams/old-team/backlog')
    await choose(wrapper)
    expect(router.currentRoute.value.path).toBe('/o/acme/p/NEW/teams/new-default/backlog')
    wrapper.unmount()
  })

  it('falls back to Items when the destination project does not allow settings', async () => {
    const { wrapper, router } = await render('/o/acme/p/OLD/settings/general')
    await choose(wrapper, 'Member project')
    expect(router.currentRoute.value.path).toBe('/o/acme/p/MEM/items')
    wrapper.unmount()
  })

  it('falls back to Items when the destination has no available team', async () => {
    const { wrapper, router } = await render('/o/acme/p/OLD/teams/old-team/backlog')
    vi.spyOn(teamsApi, 'listTeams').mockResolvedValue([])
    await choose(wrapper)
    expect(router.currentRoute.value.path).toBe('/o/acme/p/NEW/items')
    expect(useTeamsStore().currentId).toBeNull()
    wrapper.unmount()
  })

  it('ignores a delayed switch when the user has already chosen another project', async () => {
    const from = '/o/acme/p/OLD/teams/old-team/backlog'
    const { wrapper, router } = await render(from)
    let finish!: (value: teamsApi.Team[]) => void
    vi.spyOn(teamsApi, 'listTeams').mockImplementationOnce(
      () =>
        new Promise((resolve) => {
          finish = resolve
        }),
    )
    await choose(wrapper)
    await choose(wrapper, 'Old project')
    finish([{ id: 'new-default', name: 'Default', isDefault: true } as teamsApi.Team])
    await flushPromises()
    expect(router.currentRoute.value.path).toBe(from)
    expect(useProjectsStore().currentKey).toBe('OLD')
    expect(useTeamsStore().currentId).toBe('old-team')
    wrapper.unmount()
  })

  it('closes mobile navigation and leaves the current project URL untouched', async () => {
    const { wrapper, router } = await render('/o/acme/p/OLD/items?stateId=old-state', true)
    useUiStore().setMobileSidebarOpen(true)
    await choose(wrapper, 'Old project')
    expect(router.currentRoute.value.fullPath).toBe('/o/acme/p/OLD/items?stateId=old-state')
    expect(useUiStore().mobileSidebarOpen).toBe(false)
    wrapper.unmount()
  })
})
