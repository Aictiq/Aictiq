import { afterEach, describe, expect, it, vi } from 'vitest'

import {
  avatarUrl,
  describeDevice,
  forgotPassword,
  requestEmailChange,
  resetPassword,
  revokeOtherSessions,
  uploadAvatar,
} from '@/api/profile'

/**
 * The two pieces of judgement on this side of the wire: the avatar URL (which has to be
 * safe to cache, and therefore has to change when the picture does) and the three-step
 * upload, whose middle step must *not* go through `apiFetch` - the object store is another
 * origin that must receive no cookies and no CSRF header.
 */

function stubFetch(...responses: unknown[]) {
  let call = 0
  const fetchMock = vi.fn(async (input: RequestInfo | URL, _init?: RequestInit) => {
    // The presigned PUT is a plain 200 with no body; everything else is JSON.
    if (String(input).startsWith('http://store.test')) {
      return new Response(null, { status: 200 })
    }
    const body = responses[Math.min(call++, responses.length - 1)]
    return new Response(JSON.stringify(body ?? {}), {
      status: 200,
      headers: { 'content-type': 'application/json' },
    })
  })
  vi.stubGlobal('fetch', fetchMock)
  return fetchMock
}

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('pointing at a picture', () => {
  it('has no URL for someone with no picture, rather than one that 404s on every row', () => {
    expect(avatarUrl('user-1', null)).toBeNull()
    expect(avatarUrl('user-1', undefined)).toBeNull()
  })

  it('changes when the key does, which is what makes the redirect safe to cache', () => {
    const before = avatarUrl('user-1', 'users/user-1/avatar/aaa.png')
    const after = avatarUrl('user-1', 'users/user-1/avatar/bbb.png')

    expect(before).toContain('/api/v1/users/user-1/avatar')
    expect(before).not.toBe(after)
    expect(after).toContain('v=bbb.png')
  })
})

describe('naming a device', () => {
  it('reads a browser and a platform out of a user agent', () => {
    expect(
      describeDevice(
        'Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0 Safari/537.36',
      ),
    ).toBe('Chrome on Linux')
  })

  it('says so plainly when there was no user agent to read', () => {
    expect(describeDevice(null)).toBe('Unknown device')
  })
})

describe('uploading a picture', () => {
  it('asks the API, PUTs to the store without our credentials, then commits the key', async () => {
    const fetchMock = stubFetch(
      { key: 'users/u1/avatar/a.png', uploadUrl: 'http://store.test/a.png', maxBytes: 2097152 },
      { id: 'u1', avatarKey: 'users/u1/avatar/a.png' },
    )

    const file = new File([new Uint8Array([1, 2, 3])], 'me.png', { type: 'image/png' })
    await uploadAvatar(file)

    const [presign, upload, commit] = fetchMock.mock.calls

    expect(String(presign![0])).toBe('/api/v1/me/avatar')
    expect(JSON.parse(String(presign![1]!.body))).toEqual({
      contentType: 'image/png',
      contentLength: 3,
    })

    // The store signs the content type and accepts nothing else; and it must never be
    // sent the session cookie or the CSRF header, which belong to our origin alone.
    expect(String(upload![0])).toBe('http://store.test/a.png')
    expect(upload![1]!.method).toBe('PUT')
    expect(new Headers(upload![1]!.headers).get('Content-Type')).toBe('image/png')
    expect(new Headers(upload![1]!.headers).get('X-Aictiq-Request')).toBeNull()
    expect(upload![1]!.credentials).toBeUndefined()

    expect(String(commit![0])).toBe('/api/v1/me/avatar')
    expect(commit![1]!.method).toBe('PUT')
    expect(JSON.parse(String(commit![1]!.body))).toEqual({ key: 'users/u1/avatar/a.png' })
  })

  it('does not commit a key the store refused', async () => {
    const fetchMock = vi.fn(async (input: RequestInfo | URL) =>
      String(input).startsWith('http://store.test')
        ? new Response(null, { status: 403 })
        : new Response(JSON.stringify({ key: 'k', uploadUrl: 'http://store.test/a.png' }), {
            status: 200,
            headers: { 'content-type': 'application/json' },
          }),
    )
    vi.stubGlobal('fetch', fetchMock)

    await expect(
      uploadAvatar(new File([new Uint8Array([1])], 'me.png', { type: 'image/png' })),
    ).rejects.toThrow()

    // Two calls, not three: a failed upload must not record a key pointing at nothing.
    expect(fetchMock).toHaveBeenCalledTimes(2)
  })
})

describe('the account endpoints', () => {
  it('asks for a reset link by address alone', async () => {
    const fetchMock = stubFetch({ emailConfigured: true })

    await forgotPassword('ada@example.com')

    const [url, init] = fetchMock.mock.calls[0]!
    expect(String(url)).toBe('/api/v1/auth/forgot')
    expect(JSON.parse(String(init!.body))).toEqual({ email: 'ada@example.com' })
  })

  it('spends a reset link and the new password together', async () => {
    const fetchMock = stubFetch({})

    await resetPassword('tok', 'a-long-enough-password')

    expect(String(fetchMock.mock.calls[0]![0])).toBe('/api/v1/auth/reset')
    expect(JSON.parse(String(fetchMock.mock.calls[0]![1]!.body))).toEqual({
      token: 'tok',
      newPassword: 'a-long-enough-password',
    })
  })

  it('sends the current password with an address change', async () => {
    const fetchMock = stubFetch({ emailConfigured: false })

    await requestEmailChange({ newEmail: 'new@example.com', currentPassword: 'secret' })

    expect(String(fetchMock.mock.calls[0]![0])).toBe('/api/v1/me/email')
    expect(JSON.parse(String(fetchMock.mock.calls[0]![1]!.body))).toEqual({
      newEmail: 'new@example.com',
      currentPassword: 'secret',
    })
  })

  it('signs out everywhere else with a DELETE on the collection, not on a session', async () => {
    const fetchMock = stubFetch({})

    await revokeOtherSessions()

    expect(String(fetchMock.mock.calls[0]![0])).toBe('/api/v1/me/sessions')
    expect(fetchMock.mock.calls[0]![1]!.method).toBe('DELETE')
  })
})
