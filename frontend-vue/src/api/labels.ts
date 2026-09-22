import { apiFetch } from '@/utils/api'

/**
 * Labels: a project's own namespaced tags. A `group` is what turns "frontend" into
 * "type: frontend" - the same label vocabulary a filter and a chip both read.
 */
export interface Label {
  id: string
  /** 1..50 chars, trimmed. Unique within the project, case-insensitively. */
  name: string
  /** `#RRGGBB`, or null for the chip's design-token fallback. */
  color: string | null
  /** <= 280 chars. */
  description: string | null
  /** <= 50 chars, e.g. "type" so a chip reads "type: frontend". Null is ungrouped. */
  group: string | null
  /** Labelled items in this project. Read-only. */
  itemCount: number
  /** xmin. Echo it back on PATCH or the API answers 409. */
  version: number
}

/** The compact form embedded in a work item - everything a chip needs, nothing more. */
export interface ItemLabel {
  id: string
  name: string
  color: string | null
  group: string | null
}

export interface CreateLabelBody {
  name: string
  color?: string
  description?: string
  group?: string
}

/**
 * The PATCH replaces every mutable field rather than merging: an omitted `color`, `group`
 * or `description` is read as "no value" and clears it, the same way the item endpoints
 * treat their nullable fields. Send the whole record, not a delta. Blank and absent mean
 * the same thing, so `''` is how a cleared colour goes over the wire. `version` is the
 * xmin read with the label; a stale one answers 409.
 */
export interface UpdateLabelBody {
  name?: string
  color?: string
  description?: string
  group?: string
  version: number
}

const base = (slug: string, projectKey: string) => `/orgs/${slug}/projects/${projectKey}/labels`

/** Ordered by group then name, ungrouped last - the order a settings list and a picker both want. */
export const listLabels = (slug: string, projectKey: string) =>
  apiFetch<Label[]>(base(slug, projectKey))

export const createLabel = (slug: string, projectKey: string, body: CreateLabelBody) =>
  apiFetch<Label>(base(slug, projectKey), { method: 'POST', body })

export const updateLabel = (
  slug: string,
  projectKey: string,
  labelId: string,
  body: UpdateLabelBody,
) => apiFetch<Label>(`${base(slug, projectKey)}/${labelId}`, { method: 'PATCH', body })

/** Admin-only: cascades to every `item_labels` row pointing at it. */
export const deleteLabel = (slug: string, projectKey: string, labelId: string) =>
  apiFetch<void>(`${base(slug, projectKey)}/${labelId}`, { method: 'DELETE' })

export const mergeLabel = (slug: string, projectKey: string, labelId: string, targetId: string) =>
  apiFetch<void>(`${base(slug, projectKey)}/${labelId}/merge-into/${targetId}`, { method: 'POST' })

/**
 * Item labelling is addressed through the organization and the item's own permanent key,
 * not the project - the same shape as every other item-scoped write. Both ends are
 * idempotent: applying or removing a label that is already in that state is still a 204.
 */
export const addItemLabel = (slug: string, itemKey: string, labelId: string) =>
  apiFetch<void>(`/orgs/${slug}/items/${itemKey}/labels/${labelId}`, { method: 'PUT' })

export const removeItemLabel = (slug: string, itemKey: string, labelId: string) =>
  apiFetch<void>(`/orgs/${slug}/items/${itemKey}/labels/${labelId}`, { method: 'DELETE' })
