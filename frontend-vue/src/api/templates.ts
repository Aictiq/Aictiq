import type { ItemLabel } from '@/api/labels'
import { apiFetch } from '@/utils/api'

export type WorkItemType = 'epic' | 'feature' | 'story' | 'task' | 'bug'
export type WorkItemPriority = 'none' | 'low' | 'medium' | 'high' | 'urgent'

/** A type-specific create prefill; it never creates an item by itself. */
export interface ItemTemplate {
  id: string
  type: WorkItemType
  name: string
  descriptionMarkdown: string
  defaultLabelIds: string[]
  defaultPriority: WorkItemPriority | null
  isDefault: boolean
  version: number
}

export interface SaveItemTemplateBody {
  type?: WorkItemType
  name?: string
  descriptionMarkdown?: string
  defaultLabelIds?: string[]
  defaultPriority?: WorkItemPriority | null
  isDefault?: boolean
  version?: number
}

export interface BulkItemSet {
  stateId?: string
  assigneeId?: string
  priority?: WorkItemPriority
  teamId?: string
  sprintId?: string
  addLabels?: string[]
  removeLabels?: string[]
}

export interface BulkItemResult<TItem = unknown> {
  key: string
  status: number
  detail: string | null
  item: TItem | null
}

export interface BulkUpdateResponse<TItem = unknown> {
  results: BulkItemResult<TItem>[]
}

const base = (slug: string, projectKey: string) =>
  `/orgs/${slug}/projects/${projectKey}/templates`

export const listItemTemplates = (slug: string, projectKey: string) =>
  apiFetch<ItemTemplate[]>(base(slug, projectKey))

export const createItemTemplate = (slug: string, projectKey: string, body: SaveItemTemplateBody) =>
  apiFetch<ItemTemplate>(base(slug, projectKey), { method: 'POST', body })

export const updateItemTemplate = (
  slug: string,
  projectKey: string,
  templateId: string,
  body: SaveItemTemplateBody,
) => apiFetch<ItemTemplate>(`${base(slug, projectKey)}/${templateId}`, { method: 'PATCH', body })

export const deleteItemTemplate = (slug: string, projectKey: string, templateId: string) =>
  apiFetch<void>(`${base(slug, projectKey)}/${templateId}`, { method: 'DELETE' })

export const bulkUpdateItems = <TItem>(
  slug: string,
  projectKey: string,
  keys: string[],
  set: BulkItemSet,
  versions: Record<string, number>,
) =>
  apiFetch<BulkUpdateResponse<TItem>>(`/orgs/${slug}/projects/${projectKey}/items/bulk`, {
    method: 'POST',
    body: { keys, set, versions },
  })

/** Resolves the compact label values a template asks to prefill. */
export function templateLabels(template: ItemTemplate, labels: ItemLabel[]): ItemLabel[] {
  const wanted = new Set(template.defaultLabelIds)
  return labels.filter((label) => wanted.has(label.id))
}
