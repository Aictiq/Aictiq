import { afterEach, describe, expect, it, vi } from 'vitest'

import {
  describeExternalError,
  externalLinkUrl,
  externalSignInUrl,
  listLogins,
  listProviders,
  unlinkLogin,
} from '@/api/auth'

/**
 * Starting an OAuth flow is a navigation, not a request — the first hop is a 302 to
 * another origin — so what the client owns here is the *URL*, and that is what these
 * assert. The refusal messages matter too: the callback has no body to explain itself in,
 * so it redirects back with a code and this is where the code becomes a sentence.
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

describe('where the browser is sent to start a provider sign-in', () => {
  it('names the provider in the path and nothing else when there is nothing else', () => {
    expect(externalSignInUrl('google')).toBe('/api/v1/auth/external/google')
  })

  it('carries next and an invitation token as query parameters', () => {
    const url = externalSignInUrl('github', { next: '/items', invite: 'tok-123' })

    expect(url).toContain('/api/v1/auth/external/github?')
    expect(url).toContain('next=%2Fitems')
    expect(url).toContain('invite=tok-123')
  })

  it('escapes the provider rather than pasting it into the path', () => {
    expect(externalSignInUrl('../admin')).toBe('/api/v1/auth/external/..%2Fadmin')
  })

  it('uses the link path when the person is already signed in', () => {
    // The same handshake, but the callback attaches the login instead of resolving one.
    expect(externalLinkUrl('google', '/settings')).toBe(
      '/api/v1/auth/external/google/link?next=%2Fsettings',
    )
  })
})

describe('the refusals a callback can come back with', () => {
  it('explains an unverified provider address in terms of what to do next', () => {
    const message = describeExternalError('email-unverified')

    expect(message).toContain('would not confirm')
    expect(message).toContain('Verify it')
  })

  it('tells someone whose address is already taken to sign in and link', () => {
    expect(describeExternalError('account-exists-link-required')).toContain('link this provider')
  })

  it('falls back to a generic message rather than showing a raw code', () => {
    expect(describeExternalError('something-new')).toBe(
      describeExternalError('provider-failed'),
    )
  })

  it('says nothing at all when there was no error', () => {
    expect(describeExternalError(undefined)).toBeNull()
    expect(describeExternalError(['a', 'b'])).toBeNull()
  })
})

describe('the endpoints behind the buttons', () => {
  it('asks the API which providers this instance actually has', async () => {
    const fetchMock = stubFetch([])

    await listProviders()

    // Not a build-time flag: the API is what knows which credentials it holds.
    expect(String(fetchMock.mock.calls[0]![0])).toBe('/api/v1/auth/providers')
  })

  it('lists and removes the logins on the current account', async () => {
    const fetchMock = stubFetch([])

    await listLogins()
    await unlinkLogin('google')

    expect(String(fetchMock.mock.calls[0]![0])).toBe('/api/v1/me/logins')
    expect(String(fetchMock.mock.calls[1]![0])).toBe('/api/v1/me/logins/google')
    expect(fetchMock.mock.calls[1]![1]!.method).toBe('DELETE')
    expect(new Headers(fetchMock.mock.calls[1]![1]!.headers).get('X-Aictiq-Request')).toBe('1')
  })
})
