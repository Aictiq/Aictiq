import { createPinia, setActivePinia } from 'pinia'
import { defineComponent } from 'vue'
import { createMemoryHistory, createRouter } from 'vue-router'
import { mount } from '@vue/test-utils'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import { useLogout } from '@/composables/useLogout'
import { useOrganizationsStore } from '@/stores/organizations'
import { useSessionStore } from '@/stores/session'

/**
 * Signing out has to leave nothing behind, and it has to work when the network does not.
 * Both surfaces that offer it (the sidebar menu and the command palette) share this
 * function, so these are the guarantees for both.
 */

const alice = {
  id: 'u1',
  email: 'alice@aictiq.local',
  firstName: 'Alice',
  lastName: 'Ng',
  fullName: 'Alice Ng',
  roles: ['User'],
  isAgent: false,
  avatarKey: null,
  timeZone: null,
}

/** `useRouter` needs a real instance, and the composable navigates for real. */
function harness() {
  const router = createRouter({
    history: createMemoryHistory(),
    routes: [
      { path: '/', component: { template: '<div />' } },
      { path: '/login', component: { template: '<div />' } },
    ],
  })

  let logout!: () => Promise<void>
  const Host = defineComponent({
    setup() {
      logout = useLogout()
      return () => null
    },
  })

  mount(Host, { global: { plugins: [router] } })
  return { router, logout: () => logout() }
}

beforeEach(() => {
  setActivePinia(createPinia())
  // The store's remembered slug lives in localStorage, which happy-dom does not provide.
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

describe('useLogout', () => {
  it('ends the session, forgets the organization and leaves for the login page', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => new Response(null, { status: 204 })))
    const session = useSessionStore()
    const organizations = useOrganizationsStore()
    session.set(alice)
    const { router, logout } = harness()
    await router.push('/')

    await logout()

    expect(session.user).toBeNull()
    expect(session.status).toBe('anonymous')
    expect(organizations.currentSlug).toBeNull()
    expect(router.currentRoute.value.path).toBe('/login')
  })

  // A network failure must not strand someone in an authenticated shell whose session
  // this browser has already thrown away.
  it('still clears and navigates when the request fails', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(() => Promise.reject(new Error('offline'))),
    )
    const session = useSessionStore()
    session.set(alice)
    const { router, logout } = harness()
    await router.push('/')

    await expect(logout()).resolves.toBeUndefined()

    expect(session.user).toBeNull()
    expect(router.currentRoute.value.path).toBe('/login')
  })
})
