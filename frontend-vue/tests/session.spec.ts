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

  it('loads once for concurrent callers - the guard and the app boot both ask', async () => {
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

  it('waits for the second factor when the password alone is not the whole sign-in', async () => {
    const fetchMock = vi.fn((url: string) =>
      Promise.resolve(
        url.endsWith('/auth/login/two-factor')
          ? jsonResponse(200, { accessToken: null, refreshToken: null, user: alice })
          : jsonResponse(202, {
              ticket: 't-1',
              expiresAt: '2026-10-09T10:00:00Z',
              twoFactorRequired: true,
            }),
      ),
    )
    vi.stubGlobal('fetch', fetchMock)

    const session = useSessionStore()
    const outcome = await session.login({ email: alice.email, password: 'a-long-enough-password' })

    expect(outcome).toEqual({ status: 'two-factor', ticket: 't-1' })
    expect(session.isAuthenticated).toBe(false)

    await session.completeTwoFactor('t-1', { code: '123456' })

    expect(session.status).toBe('authenticated')
    const [, init] = fetchMock.mock.calls[1] as unknown as [string, RequestInit]
    expect(JSON.parse(init.body as string)).toEqual({ ticket: 't-1', code: '123456' })
  })

  it('signs in with a passkey in one go', async () => {
    const credential = { id: 'cred', type: 'public-key', response: {} }
    vi.stubGlobal(
      'PublicKeyCredential',
      Object.assign(function PublicKeyCredential() {}, {
        parseRequestOptionsFromJSON: (options: unknown) => options,
      }),
    )
    vi.stubGlobal('navigator', {
      ...navigator,
      credentials: { get: vi.fn(() => Promise.resolve({ toJSON: () => credential })) },
    })
    const fetchMock = vi.fn((url: string) =>
      Promise.resolve(
        url.endsWith('/auth/passkey/options')
          ? jsonResponse(200, { ticket: 'p-1', options: { challenge: 'abc' } })
          : jsonResponse(200, { accessToken: null, refreshToken: null, user: alice }),
      ),
    )
    vi.stubGlobal('fetch', fetchMock)

    const session = useSessionStore()
    await session.loginWithPasskey()

    expect(session.status).toBe('authenticated')
    const [url, init] = fetchMock.mock.calls[1] as unknown as [string, RequestInit]
    expect(url).toMatch(/\/auth\/passkey\/login$/)
    expect(JSON.parse(init.body as string)).toEqual({ ticket: 'p-1', credential })
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

  it('stays resolved while it re-reads, so the app is not swapped for the splash', async () => {
    let answer: (response: Response) => void = () => {}
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(jsonResponse(200, alice))
      .mockImplementationOnce(() => new Promise<Response>((resolve) => (answer = resolve)))
    vi.stubGlobal('fetch', fetchMock)

    const session = useSessionStore()
    await session.load()
    const reloading = session.reload()

    expect(session.isResolved).toBe(true)
    expect(session.isAuthenticated).toBe(true)
    answer(jsonResponse(200, { ...alice, unreadCount: 3 }))
    await reloading
    expect(session.user?.unreadCount).toBe(3)
  })

  it('keeps the session through a failed re-read and signs out only on a 401', async () => {
    const session = useSessionStore()
    vi.stubGlobal('fetch', vi.fn().mockResolvedValueOnce(jsonResponse(200, alice)))
    await session.load()

    vi.stubGlobal('fetch', vi.fn(() => Promise.reject(new TypeError('offline'))))
    await session.reload()
    expect(session.status).toBe('authenticated')

    vi.stubGlobal(
      'fetch',
      vi.fn(() => Promise.resolve(jsonResponse(401, { status: 401, title: 'Unauthorized' }))),
    )
    await session.reload()
    expect(session.status).toBe('anonymous')
  })
})
