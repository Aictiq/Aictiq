import { flushPromises, mount } from '@vue/test-utils'
import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query'
import { createPinia, setActivePinia } from 'pinia'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createMemoryHistory, createRouter } from 'vue-router'
import NotificationsPopover from '@/components/notifications/NotificationsPopover.vue'
import { useOrganizationsStore } from '@/stores/organizations'
import { useSessionStore } from '@/stores/session'
import type { Notification } from '@/api/notifications'

const api = vi.hoisted(() => ({ listNotifications: vi.fn(), markNotificationsRead: vi.fn() }))
vi.mock('@/api/notifications', () => api)
const toastError = vi.hoisted(() => vi.fn())
vi.mock('@/composables/useToast', () => ({ useToast: () => ({ error: toastError }) }))

let rows: Notification[]
let wrapper: ReturnType<typeof mount>
let client: QueryClient
beforeEach(() => {
  setActivePinia(createPinia())
  rows = [
    {
      id: 'n1',
      organizationId: 'other-org',
      kind: 'mentioned',
      projectId: 'p1',
      itemId: 'i1',
      itemKey: 'OTHER-31',
      message: 'You were mentioned.',
      createdAt: '2026-10-01T10:00:00Z',
      readAt: null,
    },
    {
      id: 'n2',
      organizationId: 'other-org',
      kind: 'commented',
      projectId: 'p1',
      itemId: 'i2',
      itemKey: 'OTHER-32',
      message: 'New comment.',
      createdAt: '2026-10-01T09:00:00Z',
      readAt: null,
    },
  ]
  api.listNotifications.mockImplementation(async () => rows.map((row) => ({ ...row })))
  api.markNotificationsRead.mockImplementation(async (ids?: string[], all = false) => {
    let read = 0
    rows.forEach((row) => {
      if (!row.readAt && (all || ids?.includes(row.id))) {
        row.readAt = '2026-10-01T12:00:00Z'
        read++
      }
    })
    return { read }
  })
  useSessionStore().set({ id: 'me', unreadCount: 2 } as never)
  vi.spyOn(useSessionStore(), 'load').mockImplementation(async () => {
    useSessionStore().apply({ unreadCount: rows.filter((row) => !row.readAt).length })
  })
  useOrganizationsStore().organizations = [
    { id: 'other-org', slug: 'other', name: 'Other', role: 'member', canOperateFactory: false },
  ]
  useOrganizationsStore().select('current')
  toastError.mockClear()
})
afterEach(() => {
  wrapper?.unmount()
  client?.clear()
  document.body.innerHTML = ''
  vi.restoreAllMocks()
})

async function render() {
  const router = createRouter({
    history: createMemoryHistory(),
    routes: [
      { path: '/o/:slug/factory/runs/:runId', component: { template: '<div />' } },
      { path: '/', component: { template: '<div />' } },
      { path: '/inbox', component: { template: '<div />' } },
      {
        path: '/o/:slug/p/:projectKey/items/:itemKey',
        name: 'item-detail',
        component: { template: '<div />' },
      },
    ],
  })
  await router.push('/')
  client = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  })
  wrapper = mount(NotificationsPopover, {
    attachTo: document.body,
    global: { plugins: [router, [VueQueryPlugin, { queryClient: client }]] },
  })
  await flushPromises()
  await wrapper.get('button[aria-label="Notifications"]').trigger('click')
  await vi.waitFor(() => expect(document.querySelector('[role="dialog"]')).not.toBeNull())
  return router
}

function popup() {
  return document.querySelector('[role="dialog"]')!
}
function markAll() {
  Array.from(popup().querySelectorAll('button'))
    .find((button) => button.textContent?.trim() === 'Mark all read')!
    .click()
}

describe('header notifications', () => {
  it('opens a popup without navigating and closes on Escape', async () => {
    const router = await render()
    expect(router.currentRoute.value.fullPath).toBe('/')
    expect(popup().textContent).toContain('You were mentioned.')
    expect(popup().querySelectorAll('.font-semibold')).toHaveLength(3)
    popup().dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }))
    await vi.waitFor(() => expect(document.querySelector('[role="dialog"]')).toBeNull())
  })

  it('marks all read, removes bold styling and hides the badge', async () => {
    await render()
    expect(wrapper.get('[data-testid="notification-badge"]').text()).toBe('2')
    markAll()
    await vi.waitFor(() =>
      expect(wrapper.find('[data-testid="notification-badge"]').exists()).toBe(false),
    )
    expect(api.markNotificationsRead).toHaveBeenCalledWith(undefined, true)
    expect(popup().querySelectorAll('li .font-semibold')).toHaveLength(0)
    expect(popup().querySelectorAll('[aria-label="Unread"]')).toHaveLength(0)
    expect(useSessionStore().load).toHaveBeenCalled()
  })

  it('links to the notification organization and marks only that entry read', async () => {
    const router = await render()
    const link = popup().querySelector('li a') as HTMLAnchorElement
    expect(link.getAttribute('href')).toBe('/o/other/p/OTHER/items/OTHER-31')
    link.click()
    await vi.waitFor(() =>
      expect(router.currentRoute.value.fullPath).toBe('/o/other/p/OTHER/items/OTHER-31'),
    )
    await vi.waitFor(() => expect(useSessionStore().user?.unreadCount).toBe(1))
    expect(api.markNotificationsRead).toHaveBeenCalledWith(['n1'], false)
    expect(document.querySelector('[role="dialog"]')).toBeNull()
  })

  it('links run updates to their run for factory operators', async () => {
    rows[0]!.runId = 'run-31'
    useOrganizationsStore().organizations[0]!.canOperateFactory = true
    await render()
    expect(popup().querySelector('li a')?.getAttribute('href')).toBe('/o/other/factory/runs/run-31')
  })

  it('opens the related item for a run update when factory access is unavailable', async () => {
    rows[0]!.runId = 'run-31'
    await render()
    expect(popup().querySelector('li a')?.getAttribute('href')).toBe(
      '/o/other/p/OTHER/items/OTHER-31',
    )
  })

  it('preserves unread styling and count when marking read fails', async () => {
    api.markNotificationsRead.mockRejectedValueOnce(new Error('offline'))
    await render()
    markAll()
    await vi.waitFor(() => expect(toastError).toHaveBeenCalled())
    expect(wrapper.get('[data-testid="notification-badge"]').text()).toBe('2')
    expect(popup().querySelectorAll('li .font-semibold')).toHaveLength(2)
  })

  it('shows an empty state with no zero badge', async () => {
    rows = []
    useSessionStore().apply({ unreadCount: 0 })
    await render()
    expect(popup().textContent).toContain('You’re all caught up.')
    expect(wrapper.find('[data-testid="notification-badge"]').exists()).toBe(false)
  })

  it('shows a retry action when loading fails', async () => {
    api.listNotifications.mockRejectedValueOnce(new Error('offline'))
    await render()
    await vi.waitFor(() =>
      expect(popup().textContent).toContain('Notifications could not be loaded.'),
    )
    const retry = Array.from(popup().querySelectorAll('button')).find(
      (button) => button.textContent?.trim() === 'Try again',
    )!
    retry.click()
    await vi.waitFor(() => expect(popup().textContent).toContain('You were mentioned.'))
  })
})
