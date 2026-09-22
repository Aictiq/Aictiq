import { apiFetch } from '@/utils/api'

export interface HistoryChange {
  id: string
  field: string
  oldValue: unknown
  newValue: unknown
}

export interface HistoryEvent {
  eventId: string
  itemKey: string
  actor: { id: string; displayName: string; avatarKey: string | null; isAgent: boolean } | null
  at: string
  changes: HistoryChange[]
}

export interface HistoryPage {
  items: HistoryEvent[]
  page: number
  pageSize: number
  total: number
}

export const itemHistory = (slug: string, itemKey: string, page = 1) =>
  apiFetch<HistoryPage>(`/orgs/${slug}/items/${itemKey}/history?page=${page}`)

export const projectActivity = (slug: string, projectKey: string, page = 1, actorId?: string, teamId?: string) => {
  const query = new URLSearchParams({ page: String(page) })
  if (actorId) query.set('actorId', actorId)
  if (teamId) query.set('teamId', teamId)
  return apiFetch<HistoryPage>(`/orgs/${slug}/projects/${projectKey}/activity?${query}`)
}
