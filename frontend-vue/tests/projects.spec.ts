import { afterEach, describe, expect, it, vi } from 'vitest'

import {
  canCreateProjects,
  createProject,
  hasProjectRole,
  listProjects,
  removeProjectMember,
  setProjectArchived,
  setProjectMemberRole,
  suggestKey,
  updateProject,
} from '@/api/projects'
import { listWorkflows } from '@/api/workflows'

/**
 * The key suggestion is the only real logic on this side - it decides what someone sees
 * before they commit to a prefix that can never change - and the request shapes matter
 * because every project route is nested under the organization that establishes the
 * tenant. The permission mirrors are a courtesy: the API enforces the same rules, and the
 * backend's role-matrix tests are what prove them.
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

describe('suggesting a key from a name', () => {
  it('takes the initials of a multi-word name', () => {
    expect(suggestKey('Acme Website')).toBe('AW')
    expect(suggestKey('Customer Relationship Manager')).toBe('CRM')
  })

  it('takes the letters of a single word', () => {
    expect(suggestKey('Website')).toBe('WEBSITE')
  })

  it('folds accents rather than dropping the letters', () => {
    expect(suggestKey('Ćuljak Software')).toBe('CS')
  })

  it('never starts with a digit and never exceeds ten characters', () => {
    // A key has to start with a letter so that "ACME-123" parses unambiguously.
    expect(suggestKey('2024 Roadmap')).toBe('R')
    expect(suggestKey('Extraordinarily')).toBe('EXTRAORDIN')
  })

  it('returns nothing when there is nothing to work with', () => {
    // The API derives one of its own; the field just shows no preview.
    expect(suggestKey('🚀')).toBe('')
  })
})

describe('who may create a project', () => {
  it('lets owners and admins create whatever the setting says', () => {
    for (const allowed of [true, false]) {
      expect(canCreateProjects('owner', allowed)).toBe(true)
      expect(canCreateProjects('admin', allowed)).toBe(true)
    }
  })

  it('binds members to the organization setting', () => {
    expect(canCreateProjects('member', true)).toBe(true)
    expect(canCreateProjects('member', false)).toBe(false)
  })

  it('never lets a guest create one', () => {
    expect(canCreateProjects('guest', true)).toBe(false)
    expect(canCreateProjects(undefined, true)).toBe(false)
  })
})

describe('the project role ladder', () => {
  it('ranks admin above member above guest', () => {
    expect(hasProjectRole('admin', 'guest')).toBe(true)
    expect(hasProjectRole('member', 'member')).toBe(true)
    expect(hasProjectRole('guest', 'member')).toBe(false)
    expect(hasProjectRole(undefined, 'guest')).toBe(false)
  })
})

describe('the project endpoints', () => {
  it('nests every project route under the organization', async () => {
    const fetchMock = stubFetch([])

    await listProjects('acme')
    await listProjects('acme', true)

    expect(String(fetchMock.mock.calls[0]![0])).toBe('/api/v1/orgs/acme/projects')
    // Only sent when asked for - an empty parameter is not the same as absent.
    expect(String(fetchMock.mock.calls[1]![0])).toContain('includeArchived=true')
  })

  it('creates with the CSRF header and only the fields that were filled in', async () => {
    const fetchMock = stubFetch({ key: 'AW' })

    await createProject('acme', { name: 'Acme Website', visibility: 'private' })

    const [url, init] = fetchMock.mock.calls[0]!
    expect(String(url)).toBe('/api/v1/orgs/acme/projects')
    expect(init!.method).toBe('POST')
    expect(JSON.parse(String(init!.body))).toEqual({
      name: 'Acme Website',
      visibility: 'private',
    })
    expect(new Headers(init!.headers).get('X-Aictiq-Request')).toBe('1')
  })

  it('patches by key and echoes the version back', async () => {
    const fetchMock = stubFetch({ key: 'AW' })

    await updateProject('acme', 'AW', { name: 'Renamed', description: '', version: 7 })

    const [url, init] = fetchMock.mock.calls[0]!
    expect(String(url)).toBe('/api/v1/orgs/acme/projects/AW')
    expect(init!.method).toBe('PATCH')
    // The empty description is deliberate: to the API that means "clear it".
    expect(JSON.parse(String(init!.body))).toEqual({
      name: 'Renamed',
      description: '',
      version: 7,
    })
  })

  it('archives and un-archives through two different paths', async () => {
    const fetchMock = stubFetch({ key: 'AW' })

    await setProjectArchived('acme', 'AW', true)
    await setProjectArchived('acme', 'AW', false)

    expect(String(fetchMock.mock.calls[0]![0])).toBe('/api/v1/orgs/acme/projects/AW/archive')
    expect(String(fetchMock.mock.calls[1]![0])).toBe('/api/v1/orgs/acme/projects/AW/unarchive')
  })

  it('puts a project role and deletes a membership', async () => {
    const fetchMock = stubFetch({ userId: 'u1', role: 'member' })

    await setProjectMemberRole('acme', 'AW', 'u1', 'member')
    await removeProjectMember('acme', 'AW', 'u1')

    expect(String(fetchMock.mock.calls[0]![0])).toBe('/api/v1/orgs/acme/projects/AW/members/u1')
    expect(fetchMock.mock.calls[0]![1]!.method).toBe('PUT')
    expect(fetchMock.mock.calls[1]![1]!.method).toBe('DELETE')
  })

  it('gets workflow states from the project-scoped workflow endpoint', async () => {
    const fetchMock = stubFetch([])

    await listWorkflows('acme', 'AW')

    expect(String(fetchMock.mock.calls[0]![0])).toBe(
      '/api/v1/orgs/acme/projects/AW/workflows',
    )
    expect(fetchMock.mock.calls[0]![1]?.method).toBeUndefined()
  })
})
