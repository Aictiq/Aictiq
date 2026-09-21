import { afterEach, describe, expect, it, vi } from 'vitest'

import {
  canAssignRole,
  canRemoveMember,
  canSetFactoryOperator,
  factoryFlagIsChoosable,
  listMembers,
  removeMember,
  setMemberFactoryOperator,
  setMemberRole,
} from '@/api/members'
import type { OrgRole } from '@/api/organizations'

/**
 * Two things are worth testing here. The permission mirror, because it decides what the
 * page even offers — and being *more* permissive than the API would mean showing people
 * buttons that answer 403. And the request shapes, because the roster is the first paged,
 * searchable endpoint the client talks to.
 *
 * These checks are a courtesy, never the guard: the API enforces the same rule and the
 * backend's role-matrix tests are what prove it.
 */

const roles: OrgRole[] = ['owner', 'admin', 'member', 'guest']

function stubFetch(body: unknown = {}) {
  // Typed like `fetch` itself, so the assertions below can read what was sent.
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

describe('who may change whose role', () => {
  it('lets an owner assign anything to anyone', () => {
    for (const target of roles) {
      for (const desired of roles) {
        expect(canAssignRole('owner', target, desired)).toBe(true)
      }
    }
  })

  it('lets an admin manage members and guests only', () => {
    expect(canAssignRole('admin', 'member', 'guest')).toBe(true)
    expect(canAssignRole('admin', 'guest', 'member')).toBe(true)

    // Not a peer, and not upwards: an admin who could appoint admins could appoint
    // themselves out of every check above them.
    expect(canAssignRole('admin', 'admin', 'guest')).toBe(false)
    expect(canAssignRole('admin', 'owner', 'guest')).toBe(false)
    expect(canAssignRole('admin', 'member', 'admin')).toBe(false)
    expect(canAssignRole('admin', 'member', 'owner')).toBe(false)
  })

  it('gives members and guests no say at all', () => {
    for (const actor of ['member', 'guest'] as OrgRole[]) {
      for (const target of roles) {
        expect(canAssignRole(actor, target, 'guest')).toBe(false)
        expect(canRemoveMember(actor, target)).toBe(false)
      }
    }
  })

  it('lets an owner remove anyone and an admin remove those below them', () => {
    expect(canRemoveMember('owner', 'owner')).toBe(true)
    expect(canRemoveMember('admin', 'guest')).toBe(true)
    expect(canRemoveMember('admin', 'member')).toBe(true)
    expect(canRemoveMember('admin', 'admin')).toBe(false)
    expect(canRemoveMember('admin', 'owner')).toBe(false)
  })
})

describe('who may start AI work', () => {
  it('is a choice only for a member: the role decides for everyone else', () => {
    expect(factoryFlagIsChoosable('member')).toBe(true)
    for (const role of ['owner', 'admin', 'guest'] as OrgRole[]) {
      expect(factoryFlagIsChoosable(role)).toBe(false)
    }
  })

  it('is decided by owners and admins, for members only', () => {
    expect(canSetFactoryOperator('owner', 'member')).toBe(true)
    expect(canSetFactoryOperator('admin', 'member')).toBe(true)

    // Never offered where the API would refuse: an admin's or a guest's answer is fixed,
    // and members and guests decide nothing about anyone.
    expect(canSetFactoryOperator('owner', 'admin')).toBe(false)
    expect(canSetFactoryOperator('owner', 'guest')).toBe(false)
    expect(canSetFactoryOperator('member', 'member')).toBe(false)
    expect(canSetFactoryOperator('guest', 'member')).toBe(false)
  })
})

describe('the members endpoints', () => {
  it('sends the filter and the page as query parameters', async () => {
    const fetchMock = stubFetch({ items: [], page: 1, pageSize: 20, totalCount: 0, totalPages: 0 })

    await listMembers('acme', { page: 2, pageSize: 20, search: 'ada l' })

    const url = String(fetchMock.mock.calls[0]![0])
    expect(url).toContain('/api/v1/orgs/acme/members')
    expect(url).toContain('page=2')
    expect(url).toContain('pageSize=20')
    // Encoded, not concatenated — a space in a name must not split the query string.
    expect(url).toContain('search=ada+l')
  })

  it('omits what was not asked for rather than sending empty parameters', async () => {
    const fetchMock = stubFetch({ items: [], page: 1, pageSize: 20, totalCount: 0, totalPages: 0 })

    await listMembers('acme')

    expect(String(fetchMock.mock.calls[0]![0])).toBe('/api/v1/orgs/acme/members')
  })

  it('puts a role change at the membership, carrying the CSRF header', async () => {
    const fetchMock = stubFetch({ userId: 'u1', role: 'admin', joinedAt: '2026-01-01T00:00:00Z' })

    const updated = await setMemberRole('acme', 'u1', 'admin')

    const [url, init] = fetchMock.mock.calls[0]!
    expect(String(url)).toBe('/api/v1/orgs/acme/members/u1')
    expect(init!.method).toBe('PUT')
    expect(JSON.parse(String(init!.body))).toEqual({ role: 'admin' })
    expect(new Headers(init!.headers).get('X-Aictiq-Request')).toBe('1')
    expect(updated.role).toBe('admin')
  })

  it('sends only the factory flag when that is all that changes', async () => {
    const fetchMock = stubFetch({
      userId: 'u1',
      role: 'member',
      joinedAt: '2026-01-01T00:00:00Z',
      canOperateFactory: false,
    })

    const updated = await setMemberFactoryOperator('acme', 'u1', false)

    const [url, init] = fetchMock.mock.calls[0]!
    expect(String(url)).toBe('/api/v1/orgs/acme/members/u1')
    expect(init!.method).toBe('PUT')
    // No role in the body: sending the current one back would race a concurrent role change.
    expect(JSON.parse(String(init!.body))).toEqual({ canOperateFactory: false })
    expect(updated.canOperateFactory).toBe(false)
  })

  it('removes a membership with DELETE and no body', async () => {
    const fetchMock = stubFetch()

    await removeMember('acme', 'u1')

    const [url, init] = fetchMock.mock.calls[0]!
    expect(String(url)).toBe('/api/v1/orgs/acme/members/u1')
    expect(init!.method).toBe('DELETE')
    expect(init!.body).toBeFalsy()
  })
})
