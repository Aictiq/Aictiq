import { apiFetch } from '@/utils/api'

export type WorkItemType = 'epic' | 'feature' | 'story' | 'task' | 'bug'
export type WorkItemPriority = 'none' | 'low' | 'medium' | 'high' | 'urgent'

export interface ItemLabel {
  id: string
  name: string
  color: string | null
  group: string | null
}
export interface WorkItem {
  id: string
  key: string
  type: WorkItemType
  title: string
  stateId: string
  boardColumnId?: string | null
  descriptionMarkdown: string
  descriptionHtml: string
  stateCategory: string
  priority: WorkItemPriority
  assigneeId: string | null
  teamId: string | null
  sprintId: string | null
  parentId: string | null
  points: number | null
  estimateHours: number | null
  remainingHours: number | null
  completedHours: number | null
  dueDate: string | null
  version: number
  labels: ItemLabel[]
  updatedAt: string
  isWatching: boolean
  watcherCount: number
  /** The lease an agent (or a person) holds while working. Not the same as `assigneeId`:
   *  an item can be assigned to someone and claimed by nobody, and an abandoned claim
   *  outlives the agent that made it - which is what `claimHeartbeatAt` is for. */
  claimedBy: string | null
  claimedAt: string | null
  claimHeartbeatAt: string | null
  rollup: {
    totalCount: number
    completedCount: number
    pointsTotal: number
    pointsCompleted: number
    remainingHours: number
  }
  /** Board responses include whether unresolved blocking relations hold this card. */
  blocked?: boolean
}
export interface Paged<T> {
  items: T[]
  page: number
  pageSize: number
  totalCount: number
}
export type BacklogSection = 'current' | 'next' | 'backlog'
/** Each section is paged by the server; the list is what was loaded and `*Count` is the
 * whole section. A page is a prefix of the section's tree, so no item arrives without its
 * parent, and the loaded items still come back in rank order. */
export interface TeamBacklog {
  currentSprint: WorkItem[]
  nextSprint: WorkItem[]
  backlog: WorkItem[]
  currentSprintCount: number
  nextSprintCount: number
  backlogCount: number
}
export interface BacklogQuery {
  /** The item-filter grammar; section counts are of the filtered items. */
  filter?: string
  /** Search box text. */
  q?: string
  /** Items returned per section (server default 50). */
  take?: number
  /** Per-section overrides of `take`, for sections that have been scrolled further. */
  expand?: Partial<Record<BacklogSection, number>>
}
export interface CreateItem {
  type: WorkItemType
  title: string
  parentId?: string | null
  teamId?: string | null
  stateId?: string | null
}

export const listProjectItems = (
  slug: string,
  projectKey: string,
  options: { filter?: string; q?: string; sort?: string; page?: number } = {},
) =>
  apiFetch<Paged<WorkItem>>(`/orgs/${slug}/projects/${projectKey}/items/`, {
    query: {
      filter: options.filter,
      q: options.q,
      sort: options.sort,
      page: options.page,
      pageSize: 50,
    },
  })

export const getTeamBacklog = (slug: string, teamId: string, options: BacklogQuery = {}) =>
  apiFetch<TeamBacklog>(`/orgs/${slug}/teams/${teamId}/backlog`, {
    query: {
      filter: options.filter || undefined,
      q: options.q || undefined,
      take: options.take,
      expand:
        options.expand && Object.keys(options.expand).length
          ? Object.entries(options.expand)
              .map(([section, count]) => `${section}:${count}`)
              .join(',')
          : undefined,
    },
  })

export const createItem = (slug: string, projectKey: string, item: CreateItem) =>
  apiFetch<WorkItem>(`/orgs/${slug}/projects/${projectKey}/items/`, { method: 'POST', body: item })

export const getItem = (slug: string, key: string) =>
  apiFetch<WorkItem>(`/orgs/${slug}/items/${key}`)
/** Direct children only. The hierarchy is deliberately not flattened: a Story's Tasks
 * should be shown with that Story, rather than mixed in with a descendant's work. */
export const listItemChildren = (slug: string, key: string) =>
  apiFetch<WorkItem[]>(`/orgs/${slug}/items/${key}/children`)
export const updateItem = (
  slug: string,
  key: string,
  body: Partial<WorkItem> & { version: number },
) => apiFetch<WorkItem>(`/orgs/${slug}/items/${key}`, { method: 'PATCH', body })

/** Moves an item through its workflow while enforcing its configured transitions. */
export const transitionItem = (
  slug: string,
  key: string,
  body: { toStateId: string; version: number },
) => apiFetch<WorkItem>(`/orgs/${slug}/items/${key}/transition`, { method: 'POST', body })

type EditableField =
  | 'type'
  | 'title'
  | 'descriptionMarkdown'
  | 'stateId'
  | 'priority'
  | 'assigneeId'
  | 'teamId'
  | 'parentId'
  | 'points'
  | 'estimateHours'
  | 'remainingHours'
  | 'completedHours'
  | 'dueDate'

/** Edits an item without disturbing the fields the caller did not mention.
 *
 * PATCH is an absolute assignment for the nullable fields: an omitted `teamId` takes the
 * item off its team (and its board), an omitted `parentId` detaches it from its epic. So
 * the body always starts from the item as last read and `changes` overwrite it - the same
 * contract the CLI follows. The item's `version` makes a stale starting point a 409. */
export function editItem(
  slug: string,
  current: WorkItem,
  changes: Partial<Pick<WorkItem, EditableField>>,
) {
  const {
    type,
    title,
    descriptionMarkdown,
    stateId,
    priority,
    assigneeId,
    teamId,
    parentId,
    points,
    estimateHours,
    remainingHours,
    completedHours,
    dueDate,
  } = current
  return updateItem(slug, current.key, {
    type,
    title,
    descriptionMarkdown,
    stateId,
    priority,
    assigneeId,
    teamId,
    parentId,
    points,
    estimateHours,
    remainingHours,
    completedHours,
    dueDate,
    ...changes,
    version: current.version,
  })
}

/** Atomic rank/team/sprint/parent move. `null` is intentionally omitted: the endpoint's
 * move grammar uses an absent value for "leave unchanged" and routes removals through
 * the explicit APIs that own those invariants. */
export const moveItem = (
  slug: string,
  key: string,
  body: {
    afterKey?: string
    beforeKey?: string
    parentKey?: string
    teamId?: string
    sprintId?: string
    removeSprint?: boolean
    version: number
  },
) => apiFetch<WorkItem>(`/orgs/${slug}/items/${key}/move`, { method: 'POST', body })

/** What deleting an item takes with it; `descendants` are in tree order, `depth` 1 is a direct child. */
export interface ItemDeletePreview {
  descendants: { key: string; title: string; type: WorkItemType; depth: number }[]
  comments: number
  attachments: number
  links: number
  relations: number
}

export const itemDeletePreview = (slug: string, key: string) =>
  apiFetch<ItemDeletePreview>(`/orgs/${slug}/items/${key}/delete-preview`)

/** Deletes the item and every item below it, for good. */
export const deleteItem = (slug: string, key: string) =>
  apiFetch<void>(`/orgs/${slug}/items/${key}`, { method: 'DELETE' })

export interface ItemWatcher {
  user: { id: string; displayName: string; avatarKey: string | null; isAgent: boolean }
  reason: string
}
/** Releases a claim. The claimant may always do it; a project admin may break someone
 *  else's, which is how an agent that died stops holding the item. */
export const releaseItem = (slug: string, key: string) =>
  apiFetch<void>(`/orgs/${slug}/items/${key}/release`, { method: 'POST' })

export const listWatchers = (slug: string, key: string) =>
  apiFetch<ItemWatcher[]>(`/orgs/${slug}/items/${key}/watch`)
export const watchItem = (slug: string, key: string) =>
  apiFetch<void>(`/orgs/${slug}/items/${key}/watch`, { method: 'PUT' })
export const unwatchItem = (slug: string, key: string) =>
  apiFetch<void>(`/orgs/${slug}/items/${key}/watch`, { method: 'DELETE' })
