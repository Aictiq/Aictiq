import { apiFetch } from '@/utils/api'

/**
 * Teams: the slice of a project a backlog, a board and a sprint actually belong to.
 *
 * Reading a team needs project Guest; changing one needs project Admin **or** the team's
 * own lead. That is why `isLead` is a flag rather than a role — it grants authority over
 * the team, not over the work in it.
 */

export type EstimationUnit = 'points' | 'hours'

export interface Team {
  id: string
  name: string
  /** Short code for chips and sprint names. Unique within the project. */
  key: string
  /** 1–28. */
  sprintLengthDays: number
  /** `Date.getDay()` numbers: 0 is Sunday. Capacity and burndown are counted in these. */
  workingDays: number[]
  estimationUnit: EstimationUnit
  /** Overrides the organization's zone for this team's day boundaries, or null to follow it. */
  timeZone: string | null
  /** Exactly one team per project is the default — where work lands when nobody says otherwise. */
  isDefault: boolean
  memberCount: number
  /** Whether *the caller* leads this team. What the UI enables its controls by. */
  isLead: boolean
  version: number
}

export interface TeamMember {
  userId: string
  displayName: string
  email: string | null
  avatarKey: string | null
  isAgent: boolean
  isLead: boolean
  /** Hours per working day, used to size a sprint. Null means the team's default. */
  capacityHoursPerDay: number | null
  addedAt: string
}

export interface CreateTeamBody {
  name: string
  key?: string
}

/**
 * PATCH: absent means unchanged, and an empty `timeZone` clears the override. `isDefault`
 * is only meaningful as `true` — it promotes this team and demotes the incumbent; there
 * is no way to say "no default", because every project has one.
 */
export interface UpdateTeamBody {
  name?: string
  sprintLengthDays?: number
  workingDays?: number[]
  estimationUnit?: EstimationUnit
  timeZone?: string
  isDefault?: boolean
  version: number
}

export interface UpdateTeamMemberBody {
  isLead?: boolean
  capacityHoursPerDay?: number
}

export const dayNames = [
  'Sunday',
  'Monday',
  'Tuesday',
  'Wednesday',
  'Thursday',
  'Friday',
  'Saturday',
]

/** The same bounds the API enforces, so the form can say so before the round trip. */
export function isValidSprintLength(days: number): boolean {
  return Number.isInteger(days) && days >= 1 && days <= 28
}

export function isValidWorkingDays(days: number[]): boolean {
  return (
    days.length > 0 &&
    days.length <= 7 &&
    days.every((day) => Number.isInteger(day) && day >= 0 && day <= 6) &&
    new Set(days).size === days.length
  )
}

const base = (slug: string, projectKey: string) => `/orgs/${slug}/projects/${projectKey}/teams`

export const listTeams = (slug: string, projectKey: string) =>
  apiFetch<Team[]>(base(slug, projectKey))

export const getTeam = (slug: string, projectKey: string, teamId: string) =>
  apiFetch<Team>(`${base(slug, projectKey)}/${teamId}`)

export const createTeam = (slug: string, projectKey: string, body: CreateTeamBody) =>
  apiFetch<Team>(base(slug, projectKey), { method: 'POST', body })

export const updateTeam = (
  slug: string,
  projectKey: string,
  teamId: string,
  body: UpdateTeamBody,
) => apiFetch<Team>(`${base(slug, projectKey)}/${teamId}`, { method: 'PATCH', body })

export const deleteTeam = (slug: string, projectKey: string, teamId: string) =>
  apiFetch<void>(`${base(slug, projectKey)}/${teamId}`, { method: 'DELETE' })

export const listTeamMembers = (slug: string, projectKey: string, teamId: string) =>
  apiFetch<TeamMember[]>(`${base(slug, projectKey)}/${teamId}/members`)

export const setTeamMember = (
  slug: string,
  projectKey: string,
  teamId: string,
  userId: string,
  body: UpdateTeamMemberBody,
) =>
  apiFetch<TeamMember>(`${base(slug, projectKey)}/${teamId}/members/${userId}`, {
    method: 'PUT',
    body,
  })

export const removeTeamMember = (
  slug: string,
  projectKey: string,
  teamId: string,
  userId: string,
) =>
  apiFetch<void>(`${base(slug, projectKey)}/${teamId}/members/${userId}`, { method: 'DELETE' })
