import { apiFetch } from '@/utils/api'

export type RelationKind = 'related' | 'blocks' | 'duplicates'
export type RelationDirection = 'related' | 'blocks' | 'blockedBy' | 'duplicates' | 'duplicatedBy'

export interface ItemRelation {
  targetKey: string
  targetTitle: string
  kind: RelationKind
  direction: RelationDirection
}

export interface ItemLink {
  id: string
  kind: 'url' | 'commit' | 'pullRequest' | 'branch'
  provider: string
  externalId: string
  url: string
  title: string | null
  faviconUrl: string | null
  state: string | null
  createdAt: string
  authorName?: string | null
  branch?: string | null
}

const base = (slug: string, itemKey: string) => `/orgs/${slug}/items/${itemKey}`

export const listRelations = (slug: string, itemKey: string) => apiFetch<ItemRelation[]>(`${base(slug, itemKey)}/relations`)
export const putRelation = (slug: string, itemKey: string, targetKey: string, kind: RelationKind, markSourceDuplicate = false) =>
  apiFetch<void>(`${base(slug, itemKey)}/relations`, { method: 'PUT', body: { targetKey, kind, markSourceDuplicate } })
export const deleteRelation = (slug: string, itemKey: string, targetKey: string, kind: RelationKind) =>
  apiFetch<void>(`${base(slug, itemKey)}/relations`, { method: 'DELETE', body: { targetKey, kind } })
export const listLinks = (slug: string, itemKey: string) => apiFetch<ItemLink[]>(`${base(slug, itemKey)}/links`)
export const createLink = (slug: string, itemKey: string, url: string) =>
  apiFetch<ItemLink>(`${base(slug, itemKey)}/links`, { method: 'POST', body: { url } })
export const deleteLink = (slug: string, itemKey: string, linkId: string) =>
  apiFetch<void>(`${base(slug, itemKey)}/links/${linkId}`, { method: 'DELETE' })

/** Stable branch helper for the later item-detail action. */
export function branchName(itemKey: string, title: string) {
  const slug = title.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '').slice(0, 48)
  return `${itemKey.toLowerCase()}-${slug || 'work-item'}`
}
