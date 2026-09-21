import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import { apiFetch, setUnauthenticatedHandler } from '@/utils/api'

/**
 * The refresh flow is the part of the api client that can log a user out by accident.
 * The API revokes a whole refresh-token family when a spent token is replayed, so a
 * burst of parallel 401s must produce exactly one refresh — not one per request.
 */

interface Call {
  url: string
  init: RequestInit | undefined
}

let calls: Call[] = []

function jsonResponse(status: number, body: unknown) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'content-type': status >= 400 ? 'application/problem+json' : 'application/json' },
  })
}

/** Queues one canned response per call, in order, per URL. */
function mockFetch(routes: Record<string, Array<() => Response>>) {
  return vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input)
    calls.push({ url, init })

    const key = Object.keys(routes).find((route) => url.includes(route))
    const queue = key ? routes[key] : undefined
    if (!queue || queue.length === 0) {
      throw new Error(`unexpected request: ${url}`)
    }

    const next = queue.length === 1 ? queue[0]! : queue.shift()!
    return Promise.resolve(next())
  })
}

beforeEach(() => {
  calls = []
  setUnauthenticatedHandler(() => {})
})

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('apiFetch', () => {
  it('refreshes once and retries the request when the access cookie has expired', async () => {
    const fetchMock = mockFetch({
      '/auth/refresh': [() => jsonResponse(200, {})],
      '/work-items': [
        () => jsonResponse(401, { status: 401, title: 'Unauthorized' }),
        () => jsonResponse(200, { items: [] }),
      ],
    })
    vi.stubGlobal('fetch', fetchMock)

    await expect(apiFetch('/work-items')).resolves.toEqual({ items: [] })

    expect(calls.map((c) => new URL(c.url, 'http://localhost').pathname)).toEqual([
      '/api/v1/work-items',
      '/api/v1/auth/refresh',
      '/api/v1/work-items',
    ])
  })

  it('refreshes only once for requests that 401 together', async () => {
    let refreshes = 0
    const fetchMock = mockFetch({
      '/auth/refresh': [
        () => {
          refreshes += 1
          return jsonResponse(200, {})
        },
      ],
      '/work-items': [
        () => jsonResponse(401, { status: 401, title: 'Unauthorized' }),
        () => jsonResponse(401, { status: 401, title: 'Unauthorized' }),
        () => jsonResponse(401, { status: 401, title: 'Unauthorized' }),
        () => jsonResponse(200, { items: [] }),
      ],
    })
    vi.stubGlobal('fetch', fetchMock)

    await Promise.all([apiFetch('/work-items'), apiFetch('/work-items'), apiFetch('/work-items')])

    expect(refreshes).toBe(1)
  })

  it('gives up after one retry and reports the session as over', async () => {
    const onUnauthenticated = vi.fn()
    setUnauthenticatedHandler(onUnauthenticated)

    vi.stubGlobal(
      'fetch',
      mockFetch({
        '/auth/refresh': [() => jsonResponse(200, {})],
        '/work-items': [() => jsonResponse(401, { status: 401, title: 'Unauthorized' })],
      }),
    )

    await expect(apiFetch('/work-items')).rejects.toMatchObject({ status: 401 })
    expect(onUnauthenticated).toHaveBeenCalledOnce()
  })

  it('reports the session as over when the refresh itself is refused', async () => {
    const onUnauthenticated = vi.fn()
    setUnauthenticatedHandler(onUnauthenticated)

    vi.stubGlobal(
      'fetch',
      mockFetch({
        '/auth/refresh': [
          () => jsonResponse(401, { status: 401, title: 'Invalid refresh token.' }),
        ],
        '/work-items': [() => jsonResponse(401, { status: 401, title: 'Unauthorized' })],
      }),
    )

    await expect(apiFetch('/work-items')).rejects.toMatchObject({ status: 401 })
    expect(onUnauthenticated).toHaveBeenCalledOnce()
  })

  it('never tries to refresh a failed sign-in — a 401 there means wrong password', async () => {
    const onUnauthenticated = vi.fn()
    setUnauthenticatedHandler(onUnauthenticated)

    vi.stubGlobal(
      'fetch',
      mockFetch({
        '/auth/login': [
          () => jsonResponse(401, { status: 401, title: 'Invalid email or password.' }),
        ],
      }),
    )

    await expect(
      apiFetch('/auth/login', { method: 'POST', body: { email: 'a@b.c', password: 'x' } }),
    ).rejects.toMatchObject({ title: 'Invalid email or password.' })

    expect(calls).toHaveLength(1)
    expect(onUnauthenticated).not.toHaveBeenCalled()
  })

  it('sends the CSRF header and the session cookies on every request', async () => {
    vi.stubGlobal('fetch', mockFetch({ '/work-items': [() => jsonResponse(200, {})] }))

    await apiFetch('/work-items', { method: 'POST', body: { title: 'x' } })

    const headers = new Headers(calls[0]!.init?.headers)
    expect(headers.get('x-aictiq-request')).toBe('1')
    expect(calls[0]!.init?.credentials).toBe('include')
  })
})
