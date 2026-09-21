import type { ItemLabel, WorkItemType } from '@/api/items'
import { apiFetch } from '@/utils/api'

export interface PortfolioRollup {
  totalCount: number
  completedCount: number
  pointsTotal: number
  pointsCompleted: number
  percentDoneByCount: number
  percentDoneByPoints: number
}

export interface PortfolioItem {
  id: string
  projectId: string
  projectKey: string
  /** Present on the organization response; omitted for a single-project portfolio. */
  projectName: string | null
  type: Extract<WorkItemType, 'epic' | 'feature'>
  key: string
  title: string
  stateCategory: string
  dueDate: string | null
  createdAt: string
  teamId: string | null
  teamName: string | null
  ownerId: string | null
  owner: { id: string; displayName: string; avatarKey: string | null; isAgent: boolean } | null
  labels: ItemLabel[]
  rollup: PortfolioRollup
}

export const getProjectPortfolio = (slug: string, projectKey: string) =>
  apiFetch<PortfolioItem[]>(`/orgs/${slug}/projects/${projectKey}/portfolio`)

export const getOrganizationPortfolio = (slug: string) =>
  apiFetch<PortfolioItem[]>(`/orgs/${slug}/portfolio`)
