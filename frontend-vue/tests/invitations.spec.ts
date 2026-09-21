import { afterEach, describe, expect, it, vi } from 'vitest'

import {
  acceptInvitation,
  createInvitation,
  listInvitations,
  parseAddresses,
  previewInvitation,
  resendInvitation,
  revokeInvitation,
  stakeholderInvitation,
} from '@/api/invitations'

/**
 * Two things matter here. The paste parser, because it is the only piece of real logic on
 * the client side of invitations — everything else is the API's decision — and it meets
 * whatever a mail client, a spreadsheet or a chat message hands over. And the request
 * shapes, because the redemption endpoints are addressed by *token* rather than by
 * organization, which is a URL shape nothing else in the client uses.
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

describe('reading a paste of addresses', () => {
  it('accepts commas, semicolons, newlines and tabs as the same separator', () => {
    expect(parseAddresses('a@x.com, b@x.com;c@x.com\nd@x.com\te@x.com')).toEqual([
      'a@x.com',
      'b@x.com',
      'c@x.com',
      'd@x.com',
      'e@x.com',
    ])
  })

  it('unwraps the form a mail client actually pastes', () => {
    // "Ada Lovelace <ada@example.com>" is what copying a recipient chip produces.
    expect(parseAddresses('Ada Lovelace <ada@example.com>, Grace <grace@example.com>')).toEqual([
      'ada@example.com',
      'grace@example.com',
    ])
  })

  it('collapses duplicates and normalises case rather than erroring', () => {
    // Pasting the same column twice is a common accident, not something worth refusing.
    expect(parseAddresses('Ada@Example.com, ada@example.com')).toEqual(['ada@example.com'])
  })

  it('ignores anything that is not an address', () => {
    expect(parseAddresses('names, with, no, at, signs')).toEqual([])
    expect(parseAddresses('   ')).toEqual([])
  })
})

describe('the management endpoints', () => {
  it('lists the pending invitations for one organization', async () => {
    const fetchMock = stubFetch([])

    await listInvitations('acme')

    expect(String(fetchMock.mock.calls[0]![0])).toBe('/api/v1/orgs/acme/invitations')
  })

  it('posts one address at a time, carrying the CSRF header', async () => {
    const fetchMock = stubFetch({ invitation: {}, token: 't', acceptUrl: 'u', emailSent: false })

    await createInvitation('acme', 'ada@example.com', 'member')

    const [url, init] = fetchMock.mock.calls[0]!
    expect(String(url)).toBe('/api/v1/orgs/acme/invitations')
    expect(init!.method).toBe('POST')
    expect(JSON.parse(String(init!.body))).toEqual({ email: 'ada@example.com', role: 'member' })
    expect(new Headers(init!.headers).get('X-Aictiq-Request')).toBe('1')
  })

  it('invites a stakeholder as a member of one project who cannot start AI work', async () => {
    const fetchMock = stubFetch({ invitation: {}, token: 't', acceptUrl: 'u', emailSent: false })

    const { role, ...options } = stakeholderInvitation('project-1')
    await createInvitation('acme', 'client@example.com', role, options)

    expect(JSON.parse(String(fetchMock.mock.calls[0]![1]!.body))).toEqual({
      email: 'client@example.com',
      role: 'member',
      projectId: 'project-1',
      projectRole: 'member',
      canOperateFactory: false,
    })
  })

  it('resends by invitation id and revokes with DELETE', async () => {
    const fetchMock = stubFetch({ invitation: {}, token: 't', acceptUrl: 'u', emailSent: true })

    await resendInvitation('acme', 'inv-1')
    await revokeInvitation('acme', 'inv-1')

    expect(String(fetchMock.mock.calls[0]![0])).toBe('/api/v1/orgs/acme/invitations/inv-1/resend')
    expect(fetchMock.mock.calls[0]![1]!.method).toBe('POST')
    expect(String(fetchMock.mock.calls[1]![0])).toBe('/api/v1/orgs/acme/invitations/inv-1')
    expect(fetchMock.mock.calls[1]![1]!.method).toBe('DELETE')
  })
})

describe('redeeming a link', () => {
  it('addresses the preview and the accept by token, with no organization in the path', async () => {
    const fetchMock = stubFetch({})

    await previewInvitation('tok-123')
    await acceptInvitation('tok-123')

    // The token is what decides the organization — there is no slug to send.
    expect(String(fetchMock.mock.calls[0]![0])).toBe('/api/v1/invitations/tok-123')
    expect(fetchMock.mock.calls[0]![1]!.method ?? 'GET').toBe('GET')
    expect(String(fetchMock.mock.calls[1]![0])).toBe('/api/v1/invitations/tok-123/accept')
    expect(fetchMock.mock.calls[1]![1]!.method).toBe('POST')
  })

  it('escapes the token rather than pasting it into the path', async () => {
    const fetchMock = stubFetch({})

    await previewInvitation('a/b?c')

    expect(String(fetchMock.mock.calls[0]![0])).toBe('/api/v1/invitations/a%2Fb%3Fc')
  })
})
