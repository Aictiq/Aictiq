import { defineStore } from 'pinia'
import { computed, ref } from 'vue'

import { apiFetch } from '@/utils/api'

/**
 * Everything the client knows about who is signed in.
 *
 * Deliberately no token: the credentials are httpOnly cookies the API sets and the
 * browser attaches. This store holds *state*, never a credential — if you find yourself
 * wanting to put a token here, the design has gone wrong.
 */
export interface SessionUser {
  id: string
  email: string
  firstName: string
  lastName: string
  fullName: string
  roles: string[]
  isAgent: boolean
  /** Object-storage key for the picture, or null for generated initials. */
  avatarKey: string | null
  /** IANA id, or null to follow the organization's. */
  timeZone: string | null
  unreadCount?: number
}

/** `unknown` only until the first `load()` resolves — the app shows a splash until then. */
export type SessionStatus = 'unknown' | 'anonymous' | 'authenticated'

interface Credentials {
  email: string
  password: string
}

interface Registration extends Credentials {
  firstName: string
  lastName: string
}

export const useSessionStore = defineStore('session', () => {
  const user = ref<SessionUser | null>(null)
  const status = ref<SessionStatus>('unknown')

  const isAuthenticated = computed(() => status.value === 'authenticated')
  const isResolved = computed(() => status.value !== 'unknown')

  function set(next: SessionUser) {
    user.value = next
    status.value = 'authenticated'
  }

  /**
   * Folds a saved profile back into the session without a round trip. The shell renders
   * the name and the picture from here, so a save that did not update this store would
   * leave the header disagreeing with the form the person is looking at.
   */
  function apply(changes: Partial<SessionUser>) {
    if (user.value) user.value = { ...user.value, ...changes }
  }

  function clear() {
    user.value = null
    status.value = 'anonymous'
  }

  let loading: Promise<void> | null = null

  /**
   * Resolves the cookie session against the API. Single-flight: the router guard and the
   * app boot both call it, and one round trip is enough for both.
   */
  function load(): Promise<void> {
    loading ??= apiFetch<SessionUser>('/auth/session')
      .then(set, clear)
      .finally(() => {
        loading = null
      })

    return loading
  }

  /** Re-reads the session even if one was already resolved. */
  async function reload(): Promise<void> {
    status.value = 'unknown'
    await load()
  }

  async function login(credentials: Credentials): Promise<void> {
    // The API replies with Set-Cookie and a user; no token reaches this code.
    const { user: signedIn } = await apiFetch<{ user: SessionUser }>('/auth/login', {
      method: 'POST',
      body: credentials,
    })
    set(signedIn)
  }

  async function register(registration: Registration): Promise<void> {
    const { user: created } = await apiFetch<{ user: SessionUser }>('/auth/register', {
      method: 'POST',
      body: registration,
    })
    set(created)
  }

  async function logout(): Promise<void> {
    try {
      await apiFetch('/auth/logout', { method: 'POST' })
    } finally {
      // Whatever the server said, this browser is done with the session.
      clear()
    }
  }

  return {
    user,
    status,
    isAuthenticated,
    isResolved,
    load,
    reload,
    login,
    register,
    logout,
    set,
    apply,
    clear,
  }
})
