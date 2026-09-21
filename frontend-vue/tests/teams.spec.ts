import { afterEach, describe, expect, it, vi } from 'vitest'

import {
  createTeam,
  deleteTeam,
  isValidSprintLength,
  isValidWorkingDays,
  listTeamMembers,
  listTeams,
  removeTeamMember,
  setTeamMember,
  updateTeam,
} from '@/api/teams'

/**
 * Teams are addressed through the project that owns them — a team id alone would find a
 * row, and scoping the URL is what stops one project's team being reached through
 * another's. That URL shape is most of what is worth asserting here; the rest is the
 * settings validation the form mirrors so it can say no before the round trip.
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

describe('planning settings the form can refuse on its own', () => {
  it('accepts a sprint between one and twenty-eight days', () => {
    expect(isValidSprintLength(1)).toBe(true)
    expect(isValidSprintLength(14)).toBe(true)
    expect(isValidSprintLength(28)).toBe(true)
  })

  it('refuses a sprint of no days or more than four weeks', () => {
    // Past four weeks it has stopped being a sprint; at zero it has no capacity at all.
    expect(isValidSprintLength(0)).toBe(false)
    expect(isValidSprintLength(29)).toBe(false)
    expect(isValidSprintLength(7.5)).toBe(false)
  })

  it('accepts one to seven distinct days of the week', () => {
    expect(isValidWorkingDays([1, 2, 3, 4, 5])).toBe(true)
    expect(isValidWorkingDays([0])).toBe(true)
    expect(isValidWorkingDays([0, 1, 2, 3, 4, 5, 6])).toBe(true)
  })

  it('refuses a week with no working days, an eighth day, or the same day twice', () => {
    // A team that works no days would get a burndown that divides by zero.
    expect(isValidWorkingDays([])).toBe(false)
    expect(isValidWorkingDays([7])).toBe(false)
    expect(isValidWorkingDays([1, 1])).toBe(false)
  })
})

describe('the team endpoints', () => {
  it('addresses every team through the project that owns it', async () => {
    const fetchMock = stubFetch([])

    await listTeams('acme', 'WEB')
    await listTeamMembers('acme', 'WEB', 't1')

    expect(String(fetchMock.mock.calls[0]![0])).toBe('/api/v1/orgs/acme/projects/WEB/teams')
    expect(String(fetchMock.mock.calls[1]![0])).toBe(
      '/api/v1/orgs/acme/projects/WEB/teams/t1/members',
    )
  })

  it('creates with the CSRF header and only what was filled in', async () => {
    const fetchMock = stubFetch({ id: 't1' })

    await createTeam('acme', 'WEB', { name: 'Platform' })

    const [url, init] = fetchMock.mock.calls[0]!
    expect(String(url)).toBe('/api/v1/orgs/acme/projects/WEB/teams')
    expect(init!.method).toBe('POST')
    expect(JSON.parse(String(init!.body))).toEqual({ name: 'Platform' })
    expect(new Headers(init!.headers).get('X-Aictiq-Request')).toBe('1')
  })

  it('patches settings with the version echoed back', async () => {
    const fetchMock = stubFetch({ id: 't1' })

    await updateTeam('acme', 'WEB', 't1', {
      sprintLengthDays: 7,
      workingDays: [1, 2, 3, 4],
      // Empty clears the override and returns the team to the organization's zone.
      timeZone: '',
      version: 3,
    })

    const [url, init] = fetchMock.mock.calls[0]!
    expect(String(url)).toBe('/api/v1/orgs/acme/projects/WEB/teams/t1')
    expect(init!.method).toBe('PATCH')
    expect(JSON.parse(String(init!.body))).toEqual({
      sprintLengthDays: 7,
      workingDays: [1, 2, 3, 4],
      timeZone: '',
      version: 3,
    })
  })

  it('promotes a team by asking for isDefault, never by demoting another', async () => {
    const fetchMock = stubFetch({ id: 't1' })

    await updateTeam('acme', 'WEB', 't1', { isDefault: true, version: 3 })

    // The API demotes the incumbent in the same transaction — there is no second call to
    // make, and no window in which a project has two defaults or none.
    expect(fetchMock.mock.calls).toHaveLength(1)
    expect(JSON.parse(String(fetchMock.mock.calls[0]![1]!.body))).toEqual({
      isDefault: true,
      version: 3,
    })
  })

  it('puts a roster change and deletes a membership', async () => {
    const fetchMock = stubFetch({ userId: 'u1' })

    await setTeamMember('acme', 'WEB', 't1', 'u1', { capacityHoursPerDay: 6 })
    await removeTeamMember('acme', 'WEB', 't1', 'u1')
    await deleteTeam('acme', 'WEB', 't1')

    const memberPath = '/api/v1/orgs/acme/projects/WEB/teams/t1/members/u1'
    expect(String(fetchMock.mock.calls[0]![0])).toBe(memberPath)
    expect(fetchMock.mock.calls[0]![1]!.method).toBe('PUT')
    // Absent fields keep their value, so a capacity edit cannot quietly demote a lead.
    expect(JSON.parse(String(fetchMock.mock.calls[0]![1]!.body))).toEqual({
      capacityHoursPerDay: 6,
    })
    expect(String(fetchMock.mock.calls[1]![0])).toBe(memberPath)
    expect(fetchMock.mock.calls[1]![1]!.method).toBe('DELETE')
    expect(String(fetchMock.mock.calls[2]![0])).toBe('/api/v1/orgs/acme/projects/WEB/teams/t1')
    expect(fetchMock.mock.calls[2]![1]!.method).toBe('DELETE')
  })
})
