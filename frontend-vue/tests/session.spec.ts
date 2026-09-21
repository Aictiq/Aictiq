import { createPinia, setActivePinia } from 'pinia'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import { useSessionStore } from '@/stores/session'

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

function jsonResponse(status: number, body: unknown) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'content-type': status >= 400 ? 'application/problem+json' : 'application/json' },
  })
}

beforeEach(() => {
  setActivePinia(createPinia())
})

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('session store', () => {
  it('starts unknown, so the app can show a splash rather than guess', () => {
    const session = useSessionStore()

    expect(session.status).toBe('unknown')
    expect(session.isResolved).toBe(false)
    expect(session.isAuthenticated).toBe(false)
  })

  it('resolves an existing cookie session to authenticated', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(() => Promise.resolve(jsonResponse(200, alice))),
    )

    const session = useSessionStore()
    await session.load()

    expect(session.status).toBe('authenticated')
    expect(session.user?.email).toBe(alice.email)
  })

  it('resolves a missing session to anonymous rather than leaving it unknown', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(() => Promise.resolve(jsonResponse(401, { status: 401, title: 'Unauthorized' }))),
    )

    const session = useSessionStore()
    await session.load()

    expect(session.status).toBe('anonymous')
    expect(session.user).toBeNull()
  })

  it('loads once for concurrent callers — the guard and the app boot both ask', async () => {
    const fetchMock = vi.fn(() => Promise.resolve(jsonResponse(200, alice)))
    vi.stubGlobal('fetch', fetchMock)

    const session = useSessionStore()
    await Promise.all([session.load(), session.load(), session.load()])

    expect(fetchMock).toHaveBeenCalledOnce()
  })

  it('signs in from the login response without a second round trip', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(() =>
        Promise.resolve(jsonResponse(200, { accessToken: null, refreshToken: null, user: alice })),
      ),
    )

    const session = useSessionStore()
    await session.login({ email: alice.email, password: 'a-long-enough-password' })

    expect(session.status).toBe('authenticated')
    expect(session.user?.fullName).toBe('Alice Ng')
  })

  it('leaves the session untouched when sign-in fails', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(() =>
        Promise.resolve(jsonResponse(401, { status: 401, title: 'Invalid email or password.' })),
      ),
    )

    const session = useSessionStore()
    await expect(session.login({ email: alice.email, password: 'wrong' })).rejects.toMatchObject({
      title: 'Invalid email or password.',
    })

    expect(session.status).toBe('unknown')
    expect(session.user).toBeNull()
  })

  it('clears the client session even if the logout request fails', async () => {
    const session = useSessionStore()
    session.set(alice)

    vi.stubGlobal(
      'fetch',
      vi.fn(() => Promise.reject(new TypeError('offline'))),
    )

    // The cookies are gone from this browser's point of view either way; refusing to
    // sign out because the network is down would strand the user in a signed-in shell.
    await expect(session.logout()).rejects.toThrow()
    expect(session.status).toBe('anonymous')
    expect(session.user).toBeNull()
  })

  it('re-reads the session on demand', async () => {
    const fetchMock = vi.fn(() => Promise.resolve(jsonResponse(200, alice)))
    vi.stubGlobal('fetch', fetchMock)

    const session = useSessionStore()
    await session.load()
    await session.reload()

    expect(fetchMock).toHaveBeenCalledTimes(2)
    expect(session.status).toBe('authenticated')
  })
})
