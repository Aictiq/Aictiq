import { apiFetch } from '@/utils/api'
import type { WorkItem } from '@/api/items'

export type SprintState = 'planned' | 'active' | 'completed'

export interface SprintProgress {
  totalItems: number
  completedItems: number
  pointsTotal: number
  pointsDone: number
  remainingHours: number
}

export interface Sprint {
  id: string
  teamId: string
  name: string
  goal: string
  startsOn: string
  endsOn: string
  state: SprintState
  autoCreateNext: boolean
  daysLeft: number
  progress: SprintProgress
  version: number
}

export interface SprintCapacityMember {
  userId: string
  displayName: string
  isAgent: boolean
  hoursPerDay: number
  daysOff: number
  workingDays: number
  capacityHours: number
  assignedRemainingHours: number
  utilizationPercent: number
}

export interface TaskboardCell {
  key: string
  name: string
  category: string | null
  remainingHours: number
  tasks: WorkItem[]
}

export interface TaskboardRow {
  parentKey: string | null
  name: string
  remainingHours: number
  cells: TaskboardCell[]
}

export interface SprintTaskboard {
  sprintId: string
  rows: TaskboardRow[]
  unparentedTasks: TaskboardCell[]
}

export interface CreateSprint {
  name: string
  goal?: string
  startsOn: string
  endsOn: string
  autoCreateNext?: boolean
}

export interface UpdateSprint {
  name?: string
  goal?: string
  startsOn?: string
  endsOn?: string
  autoCreateNext?: boolean
  version: number
}

export interface SprintCapacity {
  sprintId: string
  workingDays: number
  members: SprintCapacityMember[]
  capacityHours: number
  assignedRemainingHours: number
  utilizationPercent: number
}

export interface TeamVelocity {
  sprints: {
    sprintId: string
    sprintName: string
    startsOn: string
    endsOn: string
    committedPoints: number
    completedPoints: number
  }[]
  averageVelocity: number
  rollingAverageVelocity: number
  forecast: {
    sprintId: string
    sprintName: string
    scopePoints: number
    velocity: number
    differencePoints: number
  } | null
}

const teamBase = (slug: string, teamId: string) => `/orgs/${slug}/teams/${teamId}`

export const listSprints = (slug: string, teamId: string) =>
  apiFetch<Sprint[]>(`${teamBase(slug, teamId)}/sprints`)

export const createSprint = (slug: string, teamId: string, body: CreateSprint) =>
  apiFetch<Sprint>(`${teamBase(slug, teamId)}/sprints`, { method: 'POST', body })

export const updateSprint = (slug: string, sprintId: string, body: UpdateSprint) =>
  apiFetch<Sprint>(`/orgs/${slug}/sprints/${sprintId}`, { method: 'PATCH', body })

export const startSprint = (slug: string, sprintId: string) =>
  apiFetch<Sprint>(`/orgs/${slug}/sprints/${sprintId}/start`, { method: 'POST' })

export const completeSprint = (
  slug: string,
  sprintId: string,
  body: { moveUnfinishedTo?: string; moveUnfinishedToBacklog?: boolean },
) => apiFetch<Sprint>(`/orgs/${slug}/sprints/${sprintId}/complete`, { method: 'POST', body })

export const getSprintCapacity = (slug: string, sprintId: string) =>
  apiFetch<SprintCapacity>(`/orgs/${slug}/sprints/${sprintId}/capacity`)

export const updateSprintCapacity = (
  slug: string,
  sprintId: string,
  members: { userId: string; hoursPerDay: number; daysOff: number }[],
) =>
  apiFetch<SprintCapacity>(`/orgs/${slug}/sprints/${sprintId}/capacity`, {
    method: 'PUT',
    body: { members },
  })

export const getSprintTaskboard = (slug: string, sprintId: string, stateIds?: string[]) =>
  apiFetch<SprintTaskboard>(`/orgs/${slug}/sprints/${sprintId}/taskboard`, {
    query: stateIds?.length ? { stateIds: stateIds.join(',') } : undefined,
  })

export const getTeamVelocity = (slug: string, teamId: string, last = 6) =>
  apiFetch<TeamVelocity>(`${teamBase(slug, teamId)}/velocity`, { query: { last } })
