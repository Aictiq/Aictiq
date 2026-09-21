import { afterEach, describe, expect, it, vi } from 'vitest'

import { createToken, describeScopes, listTokens, revokeToken } from '@/api/tokens'

/**
 * The scope description is the piece worth testing on this side: an empty scope set means
 * *unscoped*, not powerless, and a UI that said "nothing" would be exactly backwards about
 * the rule that scopes only ever narrow.
 */

function stubFetch(body: unknown = {}) {
  const fetchMock = vi.fn(
    async (_input: RequestInfo | URL, _init?: RequestInit) =>
      new Response(JSON.stringify(body), {
        status: 200,
        headers: { 'content-type': 'application/json' },
      }),
  )
  vi.stubGlobal('fetch', fetchMock)
  return fetchMock
}

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('saying what a token may do', () => {
  it('describes an empty scope set as everything its owner can do', () => {
    // The surprising half of the rule, and the one a person most needs told.
    expect(describeScopes([])).toBe('Everything you can do')
  })

  it('names the scopes it was given, in the order they were given', () => {
    expect(describeScopes(['read', 'mcp'])).toBe('Read, MCP')
  })
})

describe('the token endpoints', () => {
  it('lists the caller’s own tokens', async () => {
    const fetchMock = stubFetch([])

    await listTokens()

    expect(String(fetchMock.mock.calls[0]![0])).toBe('/api/v1/me/tokens')
  })

  it('sends only the fields that were filled in, with the CSRF header', async () => {
    const fetchMock = stubFetch({ token: {}, secret: 'aiq_x' })

    await createToken({ name: 'laptop CLI', scopes: ['read'], expiresInDays: 90 })

    const [url, init] = fetchMock.mock.calls[0]!
    expect(String(url)).toBe('/api/v1/me/tokens')
    expect(init!.method).toBe('POST')
    expect(JSON.parse(String(init!.body))).toEqual({
      name: 'laptop CLI',
      scopes: ['read'],
      expiresInDays: 90,
    })
    expect(new Headers(init!.headers).get('X-Aictiq-Request')).toBe('1')
  })

  it('revokes by id with DELETE and no body', async () => {
    const fetchMock = stubFetch()

    await revokeToken('tok-1')

    const [url, init] = fetchMock.mock.calls[0]!
    expect(String(url)).toBe('/api/v1/me/tokens/tok-1')
    expect(init!.method).toBe('DELETE')
    expect(init!.body).toBeFalsy()
  })
})
