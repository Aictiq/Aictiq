import { apiFetch } from '@/utils/api'

export interface SavedView { id: string; ownerId: string; name: string; filter: string; sort: string; columns: string[]; isShared: boolean; version: number }
export const listSavedViews = (slug: string, projectKey: string) => apiFetch<SavedView[]>(`/orgs/${slug}/projects/${projectKey}/views/`)
export const createSavedView = (slug: string, projectKey: string, body: { name: string; filter: string; sort: string; columns: string[]; isShared: boolean }) => apiFetch<SavedView>(`/orgs/${slug}/projects/${projectKey}/views/`, { method: 'POST', body })
