import { VueQueryPlugin } from '@tanstack/vue-query'
import { createPinia, setActivePinia } from 'pinia'
import { createMemoryHistory, createRouter } from 'vue-router'
import { flushPromises, mount } from '@vue/test-utils'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import AppSidebar from '@/components/shell/AppSidebar.vue'
import type { Project, ProjectRole } from '@/api/projects'
import { useOrganizationsStore } from '@/stores/organizations'
import { useProjectsStore } from '@/stores/projects'
import { type SessionUser, useSessionStore } from '@/stores/session'

/**
 * The project section of the rail links to that project's settings, but only for the people
 * who can open them: an admin to the whole hub, a member to the Jira import (their one tab),
 * a guest nowhere.
 */

const project = (role: ProjectRole): Project => ({
  id: 'p1',
  key: 'DSO',
  name: 'Dog Show',
  description: null,
  visibility: 'organization',
  icon: null,
  color: null,
  isArchived: false,
  createdAt: '2026-09-24T00:00:00Z',
  role,
  version: 1,
})

const organization = (slug: string, canOperateFactory: boolean) => ({
  id: `org-${slug}`,
  slug,
  name: slug,
  role: 'member' as const,
  canOperateFactory,
})

async function render(role: ProjectRole, canOperateFactory = true) {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input)
      const body = /\/projects(\?|$)/.test(url) ? [project(role)] : []
      return new Response(JSON.stringify(body), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      })
    }),
  )
  useOrganizationsStore().organizations = [
    organization('acme', canOperateFactory),
    organization('globex', canOperateFactory),
  ]
  useOrganizationsStore().select('acme')
  useProjectsStore().select('DSO')

  const router = createRouter({
    history: createMemoryHistory(),
    routes: [{ path: '/:pathMatch(.*)*', component: { template: '<div />' } }],
  })
  const wrapper = mount(AppSidebar, {
    global: { plugins: [router, VueQueryPlugin], stubs: { OrgSwitcher: true } },
  })
  await flushPromises()
  return wrapper
}

const hrefs = (wrapper: Awaited<ReturnType<typeof render>>) =>
  wrapper.findAll('a').map((a) => a.attributes('href'))

beforeEach(() => {
  setActivePinia(createPinia())
  const entries = new Map<string, string>()
  vi.stubGlobal('localStorage', {
    getItem: (key: string) => entries.get(key) ?? null,
    setItem: (key: string, value: string) => void entries.set(key, value),
    removeItem: (key: string) => void entries.delete(key),
    clear: () => entries.clear(),
  })
})

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('AppSidebar project settings link', () => {
  it('links a project admin to the project settings', async () => {
    const wrapper = await render('admin')

    expect(hrefs(wrapper)).toContain('/o/acme/p/DSO/settings/general')
  })

  it('links a project member straight to the Jira import, not the admin tabs', async () => {
    const wrapper = await render('member')

    expect(hrefs(wrapper)).not.toContain('/o/acme/p/DSO/settings/general')
    expect(hrefs(wrapper)).toContain('/o/acme/p/DSO/settings/import')
    // The organization settings link is still there; only the project one is gated.
    expect(hrefs(wrapper)).toContain('/o/acme/settings/general')
  })
})

describe('AppSidebar project settings link for guests', () => {
  it('leaves every project settings link out for a guest', async () => {
    const wrapper = await render('guest')

    expect(hrefs(wrapper).some((href) => href?.startsWith('/o/acme/p/DSO/settings'))).toBe(false)
  })
})

describe('AppSidebar agents link', () => {
  it('opens agent management for the selected organization and follows organization changes', async () => {
    const wrapper = await render('member')
    const agentsLink = () => wrapper.findAll('a').find((link) => link.text() === 'Agents')!

    expect(agentsLink().attributes('href')).toBe('/o/acme/settings/agents')

    useOrganizationsStore().select('globex')
    await flushPromises()
    expect(agentsLink().attributes('href')).toBe('/o/globex/settings/agents')
  })
})

describe('AppSidebar organization group', () => {
  const labels = (wrapper: Awaited<ReturnType<typeof render>>) =>
    wrapper.findAll('a').map((a) => a.text())

  it('shows an operator the factory, agents, members and settings', async () => {
    const wrapper = await render('member')

    expect(wrapper.text()).toContain('Organization')
    expect(labels(wrapper)).toEqual(
      expect.arrayContaining(['Factory', 'Agents', 'Members', 'Settings']),
    )
  })

  it('leaves the whole group out for a stakeholder or guest', async () => {
    // A project guest, so the project's own Settings link is absent too.
    const wrapper = await render('guest', false)

    expect(wrapper.text()).not.toContain('Organization')
    for (const label of ['Factory', 'Agents', 'Members', 'Settings'])
      expect(labels(wrapper)).not.toContain(label)
    expect(hrefs(wrapper).some((href) => href?.startsWith('/o/acme/settings'))).toBe(false)
  })
})

describe('AppSidebar inbox count', () => {
  const inboxCount = (wrapper: Awaited<ReturnType<typeof render>>) =>
    wrapper.find('a[href="/inbox"] .font-mono')

  it('shows the same unread count as the notification bell', async () => {
    useSessionStore().user = { unreadCount: 15 } as SessionUser
    const wrapper = await render('member')

    expect(inboxCount(wrapper).text()).toBe('15')
  })

  it('caps a large count like the bell does', async () => {
    useSessionStore().user = { unreadCount: 120 } as SessionUser
    const wrapper = await render('member')

    expect(inboxCount(wrapper).text()).toBe('99+')
  })

  it('shows no count when nothing is unread', async () => {
    useSessionStore().user = { unreadCount: 0 } as SessionUser
    const wrapper = await render('member')

    expect(inboxCount(wrapper).exists()).toBe(false)
  })
})
